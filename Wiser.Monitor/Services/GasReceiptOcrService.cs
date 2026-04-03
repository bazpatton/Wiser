using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class GasReceiptOcrService
{
    private readonly MonitorOptions _options;

    public GasReceiptOcrService(MonitorOptions options)
    {
        _options = options;
    }

    public async Task<GasReceiptScanResult> ScanAsync(byte[] imageBytes, string? originalFileName, CancellationToken ct)
    {
        if (imageBytes.Length == 0)
            throw new InvalidOperationException("Empty image payload.");

        var workDir = Path.GetFullPath(_options.DataDir);
        Directory.CreateDirectory(workDir);
        var tempDir = Path.Combine(workDir, "gas-meter", "scan-temp");
        Directory.CreateDirectory(tempDir);

        var ext = Path.GetExtension(originalFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".jpg";
        var imagePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
        await File.WriteAllBytesAsync(imagePath, imageBytes, ct).ConfigureAwait(false);

        try
        {
            var scriptPath = ResolveScriptPath();
            var psi = new ProcessStartInfo
            {
                FileName = _options.OcrPythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(imagePath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start OCR process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.OcrTimeoutSec));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"OCR process failed: {detail.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(stdout))
                throw new InvalidOperationException("OCR process returned no output.");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var rawText = root.TryGetProperty("raw_text", out var rawTextEl) && rawTextEl.ValueKind == JsonValueKind.String
                ? rawTextEl.GetString()
                : null;
            var dateText = root.TryGetProperty("date_ddmmyy", out var dateEl) && dateEl.ValueKind == JsonValueKind.String
                ? dateEl.GetString()
                : null;

            DateOnly? entryDate = null;
            if (!string.IsNullOrWhiteSpace(dateText))
            {
                if (DateOnly.TryParseExact(dateText, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    entryDate = parsed;
                else if (DateOnly.TryParseExact(dateText, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    entryDate = parsed;
            }

            int? volCredit = null;
            if (root.TryGetProperty("vol_credit", out var volEl))
            {
                if (volEl.ValueKind == JsonValueKind.Number && volEl.TryGetInt32(out var vi))
                    volCredit = vi;
                else if (volEl.ValueKind == JsonValueKind.String && int.TryParse(volEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out vi))
                    volCredit = vi;
            }

            decimal? amountGbp = null;
            if (root.TryGetProperty("amount_gbp", out var amountEl))
            {
                if (amountEl.ValueKind == JsonValueKind.Number && amountEl.TryGetDecimal(out var dec))
                    amountGbp = dec;
                else if (amountEl.ValueKind == JsonValueKind.String)
                {
                    var s = amountEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)
                        && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out dec))
                    {
                        amountGbp = dec;
                    }
                }
            }

            double? confidence = null;
            if (root.TryGetProperty("confidence", out var confidenceEl) && confidenceEl.ValueKind == JsonValueKind.Number && confidenceEl.TryGetDouble(out var conf))
                confidence = conf;

            return new GasReceiptScanResult(volCredit, amountGbp, entryDate, confidence, rawText, stdout);
        }
        finally
        {
            try
            {
                File.Delete(imagePath);
            }
            catch
            {
                // Ignore cleanup failures for temp files.
            }
        }
    }

    public async Task<(bool Ok, string Detail)> CheckReadinessAsync(CancellationToken ct)
    {
        var scriptPath = ResolveScriptPath();
        if (!File.Exists(scriptPath))
            return (false, $"OCR script not found: {scriptPath}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.OcrPythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import easyocr; print('ready')");

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Failed to start Python process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.OcrTimeoutSec, 5, 60)));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return (false, $"Python/EasyOCR check failed: {detail}");
            }

            return (true, string.IsNullOrWhiteSpace(stdout) ? "ready" : stdout);
        }
        catch (OperationCanceledException)
        {
            return (false, "OCR readiness check timed out.");
        }
        catch (Exception ex)
        {
            return (false, $"OCR readiness check failed: {ex.Message}");
        }
    }

    private string ResolveScriptPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.OcrScriptPath))
            return Path.GetFullPath(_options.OcrScriptPath);

        return Path.Combine(AppContext.BaseDirectory, "scripts", "gas_receipt_ocr.py");
    }
}

public sealed record GasReceiptScanResult(
    int? VolCredit,
    decimal? AmountGbp,
    DateOnly? EntryDate,
    double? Confidence,
    string? RawText,
    string RawJson);
