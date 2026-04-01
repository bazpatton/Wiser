using System.Globalization;

namespace Wiser.Monitor;

/// <summary>
/// Formats UTC instants for the UI. Blazor Server runs on the host — use <c>DISPLAY_TIMEZONE</c>
/// (IANA id, e.g. <c>Europe/London</c>) when the container/OS is UTC so boost/chart times match the home.
/// </summary>
public static class DisplayClock
{
    public static TimeZoneInfo ResolveTimeZone(MonitorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DisplayTimeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(options.DisplayTimeZoneId.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                /* fall through */
            }
            catch (InvalidTimeZoneException)
            {
                /* fall through */
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>Convert a UTC instant to the configured (or host) display zone.</summary>
    public static DateTimeOffset ToDisplayTime(DateTimeOffset utcInstant, MonitorOptions options)
    {
        var tz = ResolveTimeZone(options);
        return TimeZoneInfo.ConvertTime(utcInstant, tz);
    }

    public static string FormatShortTime(DateTimeOffset utcInstant, MonitorOptions options) =>
        ToDisplayTime(utcInstant, options).ToString("t", CultureInfo.CurrentCulture);

    public static string FormatGeneral(DateTimeOffset utcInstant, MonitorOptions options) =>
        ToDisplayTime(utcInstant, options).ToString("g", CultureInfo.CurrentCulture);

    public static string FormatExplicit(DateTimeOffset utcInstant, MonitorOptions options, string format) =>
        ToDisplayTime(utcInstant, options).ToString(format, CultureInfo.CurrentCulture);
}
