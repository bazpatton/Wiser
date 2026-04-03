using System.Globalization;

namespace Wiser.Monitor;

public sealed class MonitorOptions
{
    public string WiserIp { get; set; } = "";
    public string WiserSecret { get; set; } = "";
    public string NtfyTopic { get; set; } = "";
    public int IntervalSec { get; set; } = 300;
    public double TempAlertAboveC { get; set; } = 22;
    public double? TempAlertBelowC { get; set; } = 14;
    public int RetentionDays { get; set; } = 60;
    public double? OpenMeteoLat { get; set; }
    public double? OpenMeteoLon { get; set; }
    public string DataDir { get; set; } = "./data";
    public string? TimeZoneId { get; set; }
    public string OcrPythonPath { get; set; } = "python3";
    public string? OcrScriptPath { get; set; }
    public int OcrTimeoutSec { get; set; } = 120;

    /// <summary>
    /// When true, scans use a persistent Python EasyOCR worker (HTTP) so models load once. When false, each scan spawns <c>gas_receipt_ocr.py</c> (slow).
    /// </summary>
    public bool OcrPersistentWorker { get; set; } = true;

    /// <summary>Base URL of the OCR worker (e.g. http://127.0.0.1:8765). Used when <see cref="OcrPersistentWorker"/> is true.</summary>
    public string? OcrWorkerBaseUrl { get; set; }

    /// <summary>When true with <see cref="OcrPersistentWorker"/>, the app spawns uvicorn for <c>ocr_worker.py</c> on startup.</summary>
    public bool OcrWorkerAutoStart { get; set; } = true;

    /// <summary>Max seconds to wait for the worker /health after process start (first EasyOCR model load).</summary>
    public int OcrWorkerStartupTimeoutSec { get; set; } = 180;

    public bool UseHighAlert => TempAlertAboveC > 0;
    public bool UseLowAlert => TempAlertBelowC.HasValue;
    public bool AlertsEnabled => !string.IsNullOrEmpty(NtfyTopic) && (UseHighAlert || UseLowAlert);

    public string DbPath => Path.Combine(DataDir, "wiser_monitor.sqlite3");

    public static MonitorOptions FromConfiguration(IConfiguration cfg)
    {
        // .env typo OPEN_METEO_LAT==54.4 or user-secrets "=54.4" leaves a leading '=' on the value.
        static string? SanitizeNumericRaw(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var s = raw.Trim();
            while (s.Length > 0 && (s[0] == '=' || s[0] == ' '))
                s = s.TrimStart('=', ' ').Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        static double? ParseBelow(string? raw)
        {
            var s = SanitizeNumericRaw(raw);
            if (string.IsNullOrWhiteSpace(s) || s == "0")
                return null;
            return double.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        static double? ParseOptionalDouble(string? raw)
        {
            var s = SanitizeNumericRaw(raw);
            if (s is null)
                return null;
            return double.TryParse(s, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        var lat = ParseOptionalDouble(cfg["OPEN_METEO_LAT"]);
        var lon = ParseOptionalDouble(cfg["OPEN_METEO_LON"]);
        if (lat is null ^ lon is null)
            throw new InvalidOperationException("Set both OPEN_METEO_LAT and OPEN_METEO_LON, or neither.");

        return new MonitorOptions
        {
            WiserIp = cfg["WISER_IP"]?.Trim() ?? "",
            WiserSecret = cfg["WISER_SECRET"]?.Trim() ?? "",
            NtfyTopic = cfg["NTFY_TOPIC"]?.Trim() ?? "",
            IntervalSec = int.TryParse(SanitizeNumericRaw(cfg["INTERVAL_SEC"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)
                ? iv
                : 300,
            TempAlertAboveC = double.TryParse(
                SanitizeNumericRaw(cfg["TEMP_ALERT_ABOVE_C"]),
                CultureInfo.InvariantCulture,
                out var ah)
                ? ah
                : 22,
            TempAlertBelowC = ParseBelow(cfg["TEMP_ALERT_BELOW_C"] ?? "14"),
            RetentionDays = int.TryParse(SanitizeNumericRaw(cfg["RETENTION_DAYS"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rd)
                ? rd
                : 60,
            OpenMeteoLat = lat,
            OpenMeteoLon = lon,
            DataDir = string.IsNullOrWhiteSpace(cfg["DATA_DIR"]) ? "./data" : cfg["DATA_DIR"]!.Trim(),
            TimeZoneId = string.IsNullOrWhiteSpace(cfg["TIME_ZONE"])
                ? (string.IsNullOrWhiteSpace(cfg["TZ"]) ? null : cfg["TZ"]!.Trim())
                : cfg["TIME_ZONE"]!.Trim(),
            OcrPythonPath = string.IsNullOrWhiteSpace(cfg["OCR_PYTHON_PATH"]) ? "python3" : cfg["OCR_PYTHON_PATH"]!.Trim(),
            OcrScriptPath = string.IsNullOrWhiteSpace(cfg["OCR_SCRIPT_PATH"]) ? null : cfg["OCR_SCRIPT_PATH"]!.Trim(),
            OcrTimeoutSec = int.TryParse(SanitizeNumericRaw(cfg["OCR_TIMEOUT_SEC"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ocrTimeout)
                ? Math.Clamp(ocrTimeout, 5, 180)
                : 120,
            OcrPersistentWorker = ParseEnvBool(cfg["OCR_PERSISTENT_WORKER"], defaultValue: true),
            OcrWorkerBaseUrl = ResolveOcrWorkerUrl(cfg),
            OcrWorkerAutoStart = ParseEnvBool(cfg["OCR_WORKER_AUTO_START"], defaultValue: true),
            OcrWorkerStartupTimeoutSec = int.TryParse(
                    SanitizeNumericRaw(cfg["OCR_WORKER_STARTUP_TIMEOUT_SEC"]),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ocrStart)
                ? Math.Clamp(ocrStart, 30, 600)
                : 180,
        };
    }

    private static string? ResolveOcrWorkerUrl(IConfiguration cfg)
    {
        if (!ParseEnvBool(cfg["OCR_PERSISTENT_WORKER"], defaultValue: true))
            return null;
        var raw = cfg["OCR_WORKER_URL"]?.Trim();
        if (!string.IsNullOrWhiteSpace(raw))
            return raw;
        return "http://127.0.0.1:8765";
    }

    private static bool ParseEnvBool(string? raw, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        var s = raw.Trim();
        if (string.Equals(s, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "off", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(s, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "on", StringComparison.OrdinalIgnoreCase))
            return true;
        return defaultValue;
    }

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(WiserIp) || WiserIp == "192.168.x.x")
            e.Add("Set WISER_IP to your hub LAN address.");
        if (string.IsNullOrWhiteSpace(WiserSecret) || WiserSecret == "your-secret-here")
            e.Add("Set WISER_SECRET to your hub SECRET.");
        if (!string.IsNullOrEmpty(NtfyTopic) && !AlertsEnabled)
            e.Add("NTFY_TOPIC is set but no alert thresholds: use TEMP_ALERT_ABOVE_C>0 and/or TEMP_ALERT_BELOW_C.");
        return e;
    }
}
