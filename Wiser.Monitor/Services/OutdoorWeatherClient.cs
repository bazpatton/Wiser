using System.Globalization;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed record DailyForecastHddDay(DateOnly Date, double TempMaxC, double TempMinC, double HddProxy);

public sealed class OutdoorWeatherClient(HttpClient http)
{
    private const double HddBaseC = 15.5;
    private readonly object _forecastGate = new();
    private string? _forecastCacheKey;
    private DateTimeOffset _forecastCachedAt;
    private IReadOnlyList<DailyForecastHddDay>? _forecastCache;

    private readonly object _hourlyGate = new();
    private string? _hourlyCacheKey;
    private DateTimeOffset _hourlyCachedAt;
    private IReadOnlyList<double>? _hourlyTempsCache;

    public async Task<double?> GetCurrentTempCAsync(double lat, double lon, CancellationToken ct)
    {
        var uri = new Uri(
            $"https://api.open-meteo.com/v1/forecast?latitude={Uri.EscapeDataString(lat.ToString(CultureInfo.InvariantCulture))}" +
            $"&longitude={Uri.EscapeDataString(lon.ToString(CultureInfo.InvariantCulture))}" +
            "&current=temperature_2m");

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("current", out var cur) ||
            !cur.TryGetProperty("temperature_2m", out var t))
            return null;
        return t.GetDouble();
    }

    /// <summary>
    /// Next 7 calendar days from Open-Meteo daily max/min; HDD proxy per day matches <see cref="TemperatureStore.GetDailySummaries"/> (15.5 − mean outdoor, floored at 0).
    /// Cached ~45 minutes per lat/lon/timezone key.
    /// </summary>
    public async Task<SevenDayHddOutlook?> GetSevenDayHddOutlookAsync(
        double lat,
        double lon,
        string? timeZoneId,
        CancellationToken ct)
    {
        var zone = TimeZoneResolver.Resolve(timeZoneId);
        var tzParam = ToOpenMeteoTimeZone(zone);
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{lat:R}|{lon:R}|{tzParam}");

        lock (_forecastGate)
        {
            if (_forecastCache is { } hit &&
                _forecastCacheKey == cacheKey &&
                DateTimeOffset.UtcNow - _forecastCachedAt < TimeSpan.FromMinutes(45))
                return new SevenDayHddOutlook(hit, hit.Sum(d => d.HddProxy));
        }

        var uri =
            "https://api.open-meteo.com/v1/forecast?" +
            $"latitude={Uri.EscapeDataString(lat.ToString(CultureInfo.InvariantCulture))}" +
            $"&longitude={Uri.EscapeDataString(lon.ToString(CultureInfo.InvariantCulture))}" +
            "&daily=temperature_2m_max,temperature_2m_min" +
            "&forecast_days=7" +
            $"&timezone={Uri.EscapeDataString(tzParam)}";

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("daily", out var daily))
            return null;

        if (!daily.TryGetProperty("time", out var timeEl) ||
            timeEl.ValueKind != JsonValueKind.Array ||
            !daily.TryGetProperty("temperature_2m_max", out var maxEl) ||
            maxEl.ValueKind != JsonValueKind.Array ||
            !daily.TryGetProperty("temperature_2m_min", out var minEl) ||
            minEl.ValueKind != JsonValueKind.Array)
            return null;

        var n = Math.Min(timeEl.GetArrayLength(), Math.Min(maxEl.GetArrayLength(), minEl.GetArrayLength()));
        if (n == 0)
            return null;

        var days = new List<DailyForecastHddDay>(n);
        for (var i = 0; i < n; i++)
        {
            var tStr = timeEl[i].GetString();
            if (string.IsNullOrWhiteSpace(tStr) ||
                !DateOnly.TryParse(tStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;

            if (!maxEl[i].TryGetDouble(out var tMax) || !minEl[i].TryGetDouble(out var tMin))
                continue;

            var mean = (tMax + tMin) / 2.0;
            var hdd = Math.Max(0, HddBaseC - mean);
            days.Add(new DailyForecastHddDay(date, tMax, tMin, hdd));
        }

        if (days.Count == 0)
            return null;

        var outlook = new SevenDayHddOutlook(days, days.Sum(d => d.HddProxy));
        lock (_forecastGate)
        {
            _forecastCacheKey = cacheKey;
            _forecastCachedAt = DateTimeOffset.UtcNow;
            _forecastCache = days;
        }

        return outlook;
    }

    /// <summary>
    /// Minimum hourly temperature (°C) over the next <paramref name="hours"/> hours. Cached ~15 minutes per lat/lon/hours key.
    /// Returns null if Open-Meteo response cannot be parsed.
    /// </summary>
    public async Task<double?> GetMinHourlyTempCNextHoursAsync(
        double lat,
        double lon,
        int hours,
        string? timeZoneId,
        CancellationToken ct)
    {
        hours = Math.Clamp(hours, 1, 48);
        var zone = TimeZoneResolver.Resolve(timeZoneId);
        var tzParam = ToOpenMeteoTimeZone(zone);
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{lat:R}|{lon:R}|{hours}|{tzParam}");

        lock (_hourlyGate)
        {
            if (_hourlyTempsCache is { } temps &&
                _hourlyCacheKey == cacheKey &&
                DateTimeOffset.UtcNow - _hourlyCachedAt < TimeSpan.FromMinutes(15))
                return temps.Count == 0 ? null : temps.Min();
        }

        var uri =
            "https://api.open-meteo.com/v1/forecast?" +
            $"latitude={Uri.EscapeDataString(lat.ToString(CultureInfo.InvariantCulture))}" +
            $"&longitude={Uri.EscapeDataString(lon.ToString(CultureInfo.InvariantCulture))}" +
            "&hourly=temperature_2m" +
            $"&forecast_hours={hours.ToString(CultureInfo.InvariantCulture)}" +
            $"&timezone={Uri.EscapeDataString(tzParam)}";

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("hourly", out var hourly) ||
            !hourly.TryGetProperty("temperature_2m", out var tEl) ||
            tEl.ValueKind != JsonValueKind.Array)
            return null;

        var n = Math.Min(tEl.GetArrayLength(), hours);
        if (n == 0)
            return null;

        var list = new List<double>(n);
        for (var i = 0; i < n; i++)
        {
            if (!tEl[i].TryGetDouble(out var v))
                return null;
            list.Add(v);
        }

        lock (_hourlyGate)
        {
            _hourlyCacheKey = cacheKey;
            _hourlyCachedAt = DateTimeOffset.UtcNow;
            _hourlyTempsCache = list;
        }

        return list.Min();
    }

    private static string ToOpenMeteoTimeZone(TimeZoneInfo zone)
    {
        try
        {
            if (OperatingSystem.IsWindows() && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana))
                return iana;
        }
        catch
        {
            // ignore
        }

        if (zone.Id.Contains('/', StringComparison.Ordinal))
            return zone.Id;

        return "UTC";
    }
}

public sealed record SevenDayHddOutlook(IReadOnlyList<DailyForecastHddDay> Days, double TotalHddProxy);
