namespace Wiser.Monitor.Services;

public static class TimedAwayDeepLinks
{
    public static string? SettingsTimedAway(string? appPublicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(appPublicBaseUrl))
            return null;
        var b = appPublicBaseUrl.Trim().TrimEnd('/');
        return $"{b}/settings#timed-away";
    }
}
