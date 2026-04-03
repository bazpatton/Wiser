using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class GasReceiptOcrService
{
    private readonly MonitorOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public GasReceiptOcrService(MonitorOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    private bool UsePersistentWorker =>
        _options.OcrPersistentWorker && !string.IsNullOrWhiteSpace(_options.OcrWorkerBaseUrl);

    public async Task<GasReceiptScanResult> ScanAsync(byte[] imageBytes, string? originalFileName, CancellationToken ct)
    {
        if (imageBytes.Length == 0)
            throw new InvalidOperationException("Empty image payload.");

        if (UsePersistentWorker)
            return await ScanViaWorkerAsync(imageBytes, originalFileName, ct).ConfigureAwait(false);

        return await ScanViaSubprocessAsync(imageBytes, originalFileName, ct).ConfigureAwait(false);
    }

    private async Task<GasReceiptScanResult> ScanViaWorkerAsync(byte[] imageBytes, string? originalFileName, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient("GasReceiptOcr");
        var ext = Path.GetExtension(originalFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".jpg";
        var uploadName = $"receipt{ext}";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", uploadName);

        using var response = await client.PostAsync("scan", content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException($"OCR worker returned empty body (HTTP {(int)response.StatusCode}).");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
        {
            var scriptError = errorEl.GetString() ?? "unknown OCR error";
            if (scriptError.Contains("!ssize.empty()", StringComparison.OrdinalIgnoreCase)
                || scriptError.Contains("can't open/read file", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("OCR could not read the image. Please upload a clear JPG/PNG/WEBP photo (HEIC is not supported).");
            }

            throw new InvalidOperationException($"OCR worker error: {scriptError}");
        }

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OCR worker HTTP {(int)response.StatusCode}: {body.Trim()}");

        return ParseScanJson(root, body);
    }

    private async Task<GasReceiptScanResult> ScanViaSubprocessAsync(byte[] imageBytes, string? originalFileName, CancellationToken ct)
    {
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
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort only.
                }

                throw new InvalidOperationException(
                    $"OCR timed out after {_options.OcrTimeoutSec} seconds. On first run, model download can take longer on Raspberry Pi. Increase OCR_TIMEOUT_SEC and try again.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(stdout))
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim();
                throw new InvalidOperationException($"OCR process returned no output: {detail}");
            }

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            {
                var scriptError = errorEl.GetString() ?? "unknown OCR error";
                if (scriptError.Contains("!ssize.empty()", StringComparison.OrdinalIgnoreCase)
                    || scriptError.Contains("can't open/read file", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("OCR could not read the image. Please upload a clear JPG/PNG/WEBP photo (HEIC is not supported).");
                }

                var extra = string.IsNullOrWhiteSpace(stderr) ? "" : $" ({stderr.Trim()})";
                throw new InvalidOperationException($"OCR script error: {scriptError}{extra}");
            }

            return ParseScanJson(root, stdout);
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

    private static GasReceiptScanResult ParseScanJson(JsonElement root, string rawJson)
    {
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

        return new GasReceiptScanResult(volCredit, amountGbp, entryDate, confidence, rawText, rawJson);
    }

    public async Task<(bool Ok, string Detail)> CheckReadinessAsync(CancellationToken ct)
    {
        if (UsePersistentWorker && Uri.TryCreate(_options.OcrWorkerBaseUrl, UriKind.Absolute, out var baseUri))
        {
            try
            {
                using var client = _httpClientFactory.CreateClient("GasReceiptOcr");
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.OcrTimeoutSec, 5, 60)));
                var healthUrl = new Uri(baseUri, "/health");
                using var response = await client.GetAsync(healthUrl, timeoutCts.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (false, $"OCR worker /health returned HTTP {(int)response.StatusCode}: {body.Trim()}");

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True)
                    return (true, "worker ready");

                return (false, $"OCR worker /health unexpected: {body.Trim()}");
            }
            catch (OperationCanceledException)
            {
                return (false, "OCR worker health check timed out (is the worker running?).");
            }
            catch (Exception ex)
            {
                return (false, $"OCR worker health check failed: {ex.Message}");
            }
        }

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
