using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using Wiser.Monitor.Services;

namespace Wiser.Monitor.Workers;

/// <summary>
/// Spawns the Python EasyOCR uvicorn worker once so models stay loaded between scans.
/// </summary>
public sealed class OcrWorkerHostedService : IHostedService
{
    private readonly MonitorOptions _options;
    private readonly ILogger<OcrWorkerHostedService> _logger;
    private Process? _process;

    public OcrWorkerHostedService(MonitorOptions options, ILogger<OcrWorkerHostedService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.OcrPersistentWorker || !_options.OcrWorkerAutoStart)
            return;

        if (!Uri.TryCreate(_options.OcrWorkerBaseUrl, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("OCR worker auto-start skipped: invalid OCR_WORKER_URL.");
            return;
        }

        var scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
        var workerPy = Path.Combine(scriptsDir, "ocr_worker.py");
        if (!File.Exists(workerPy))
        {
            _logger.LogWarning("OCR worker auto-start skipped: scripts/ocr_worker.py not found at {Path}.", workerPy);
            return;
        }

        var host = string.IsNullOrWhiteSpace(uri.Host) ? "127.0.0.1" : uri.Host;
        var port = uri.IsDefaultPort ? 8765 : uri.Port;
        if (uri.Scheme != "http")
        {
            _logger.LogWarning("OCR worker auto-start only supports http; got {Scheme}.", uri.Scheme);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _options.OcrPythonPath,
            WorkingDirectory = scriptsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("uvicorn");
        psi.ArgumentList.Add("ocr_worker:app");
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));

        _process = Process.Start(psi);
        if (_process is null)
        {
            _logger.LogError("Failed to start OCR worker process.");
            return;
        }

        _logger.LogInformation(
            "OCR worker starting (EasyOCR loads once; first ready may take 1–3 min on slow CPUs) at http://{Host}:{Port}/",
            host,
            port);

        var ready = await WaitForReadyAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!ready)
            _logger.LogError(
                "OCR worker did not become ready within {Seconds}s. Scans may fail until it is healthy.",
                _options.OcrWorkerStartupTimeoutSec);
        else
            _logger.LogInformation("OCR worker is ready.");
    }

    private async Task<bool> WaitForReadyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        var healthUrl = new Uri(baseUri, "/health");
        using var handler = new HttpClientHandler();
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        var deadline = DateTime.UtcNow.AddSeconds(_options.OcrWorkerStartupTimeoutSec);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await client.GetAsync(healthUrl, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (body.Contains("\"ok\"", StringComparison.OrdinalIgnoreCase)
                        && body.Contains("true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // Worker still starting or models loading.
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
            return Task.CompletedTask;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OCR worker shutdown.");
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }

        return Task.CompletedTask;
    }
}
