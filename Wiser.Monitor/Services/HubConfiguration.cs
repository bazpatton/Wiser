namespace Wiser.Monitor.Services;

/// <summary>Single source of truth for whether the Wiser hub is configured for live API calls.</summary>
public static class HubConfiguration
{
    public static bool IsConfigured(MonitorOptions o) =>
        GetConfigurationErrors(o).Count == 0;

    public static IReadOnlyList<string> GetConfigurationErrors(MonitorOptions o)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(o.WiserIp) || o.WiserIp == "192.168.x.x")
            e.Add("Set WISER_IP to your hub LAN address.");
        if (string.IsNullOrWhiteSpace(o.WiserSecret) || o.WiserSecret == "your-secret-here")
            e.Add("Set WISER_SECRET to your hub SECRET.");
        return e;
    }
}
