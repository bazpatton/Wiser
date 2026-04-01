namespace Wiser.Monitor.Services;

/// <summary>Resolves hub “now” for schedule timelines (aligned with the MAUI Wiser.Control app).</summary>
public static class HubScheduleHints
{
    private static readonly string[] DayNames =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    ];

    public static string ResolveCurrentDay(System.Text.Json.JsonElement? system)
    {
        if (system is { ValueKind: System.Text.Json.JsonValueKind.Object } sys
            && sys.TryGetProperty("LocalDateAndTime", out var ldt)
            && ldt.TryGetProperty("Day", out var d)
            && d.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var day = d.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(day) && DayNames.Contains(day))
                return day;
        }

        return DateTime.Now.DayOfWeek switch
        {
            DayOfWeek.Monday => "Monday",
            DayOfWeek.Tuesday => "Tuesday",
            DayOfWeek.Wednesday => "Wednesday",
            DayOfWeek.Thursday => "Thursday",
            DayOfWeek.Friday => "Friday",
            DayOfWeek.Saturday => "Saturday",
            _ => "Sunday",
        };
    }

    public static int ResolveCurrentSeconds(System.Text.Json.JsonElement? system)
    {
        long raw = 0;
        if (system is { ValueKind: System.Text.Json.JsonValueKind.Object } sys
            && sys.TryGetProperty("LocalDateAndTime", out var ldt)
            && ldt.TryGetProperty("Time", out var t)
            && t.ValueKind == System.Text.Json.JsonValueKind.Number
            && t.TryGetInt64(out var v))
        {
            raw = v;
        }

        return NormalizeSeconds((int)raw);
    }

    /// <summary>
    /// Hub <c>Time</c> values are usually minutes-from-midnight (0–1440). Some firmware uses HHMM for values in 1441–2359.
    /// </summary>
    public static int NormalizeSeconds(int raw)
    {
        if (raw < 0)
            return 0;

        if (raw is > 1440 and <= 2359)
        {
            var h = raw / 100;
            var m = raw % 100;
            if (h < 24 && m < 60)
                return h * 3600 + m * 60;
        }

        if (raw <= 1440)
            return raw * 60;

        if (raw > 86400)
            return raw % 86400;

        return raw;
    }

    public static string FormatTimeLabel(int rawScheduleTime)
    {
        var seconds = NormalizeSeconds(rawScheduleTime);
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
    }

    public static string FormatDegreesC(int tenths)
    {
        if (tenths <= -200)
            return "Off";
        var c = tenths / 10.0;
        return $"{c:0.#} °C";
    }
}
