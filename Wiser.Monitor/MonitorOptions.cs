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

    /// <summary>
    /// IANA timezone for UI times (e.g. <c>Europe/London</c>). When unset, uses <see cref="TimeZoneInfo.Local"/>.
    /// Set when the app runs in UTC (Docker) but users expect home-local wall times.
    /// </summary>
    public string? DisplayTimeZoneId { get; set; }

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

        static string? ResolveDisplayTimeZoneId(IConfiguration cfg)
        {
            var explicitTz = cfg["DISPLAY_TIMEZONE"]?.Trim();
            if (!string.IsNullOrWhiteSpace(explicitTz))
                return explicitTz;
            var tzEnv = cfg["TZ"]?.Trim() ?? Environment.GetEnvironmentVariable("TZ")?.Trim();
            return string.IsNullOrWhiteSpace(tzEnv) || string.Equals(tzEnv, "UTC", StringComparison.OrdinalIgnoreCase)
                ? null
                : tzEnv;
        }

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
            DisplayTimeZoneId = ResolveDisplayTimeZoneId(cfg),
        };
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
        if (!string.IsNullOrWhiteSpace(DisplayTimeZoneId))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(DisplayTimeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                e.Add(
                    $"Invalid DISPLAY_TIMEZONE / TZ id '{DisplayTimeZoneId}'. Use an IANA id (e.g. Europe/London) so boost and chart times match your home.");
            }
            catch (InvalidTimeZoneException)
            {
                e.Add(
                    $"Invalid DISPLAY_TIMEZONE / TZ id '{DisplayTimeZoneId}'. Use an IANA id (e.g. Europe/London) so boost and chart times match your home.");
            }
        }

        return e;
    }
}
