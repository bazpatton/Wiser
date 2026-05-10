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
    /// Normalises a raw hub schedule <c>Time</c> value to seconds from midnight.
    /// Values above 2359 are seconds (modern firmware). Values 0–2359 are treated
    /// as HHMM (e.g. 630 → 06:30, 2200 → 22:00). Falls back to minutes-from-midnight
    /// only when the low two digits are ≥ 60 (not a valid HHMM minute component).
    /// </summary>
    public static int NormalizeSeconds(int raw)
    {
        if (raw < 0)
            return 0;

        // Above the HHMM ceiling — treat as seconds from midnight.
        if (raw > 2359)
            return raw > 86400 ? raw % 86400 : raw;

        // 0–2359: interpret as HHMM where value = HH * 100 + MM.
        var h = raw / 100;
        var m = raw % 100;
        if (m < 60)
            return h * 3600 + m * 60;

        // MM ≥ 60 → not valid HHMM; fall back to minutes-from-midnight.
        return raw * 60;
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
