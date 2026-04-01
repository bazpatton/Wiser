namespace Wiser.Monitor.Services;

public static class TimeZoneResolver
{
    public static TimeZoneInfo Resolve(string? configuredId)
    {
        if (string.IsNullOrWhiteSpace(configuredId))
            return TimeZoneInfo.Local;

        var id = configuredId.Trim();
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            // Common UK aliases across Linux/Windows.
            if (id.Equals("Europe/London", StringComparison.OrdinalIgnoreCase))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); } catch { }
            }
            if (id.Equals("GMT Standard Time", StringComparison.OrdinalIgnoreCase))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); } catch { }
            }
        }
        catch (InvalidTimeZoneException)
        {
            // Fall through to local
        }

        return TimeZoneInfo.Local;
    }
}
