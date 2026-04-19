namespace Wiser.Monitor.Services;

/// <summary>
/// Fetches current room temperatures from the hub for UI that should match <c>/rooms</c> (live),
/// with SQLite used only as a fallback when the hub is unavailable.
/// </summary>
public static class LiveHubRoomTemps
{
    private static readonly IReadOnlyList<BoostPresetInfo> BoostPresets =
    [
        new BoostPresetInfo("Quick", 21, 30),
        new BoostPresetInfo("Comfort", 22, 60),
        new BoostPresetInfo("Gentle", 20, 45),
    ];

    /// <summary>Same sentinel as <see cref="Rooms"/> offline handling.</summary>
    public static bool IsUnavailableTemp(double tempC) =>
        Math.Abs(tempC - (-327.68)) < 0.001;

    public static bool IsHubConfigured(MonitorOptions o) =>
        !string.IsNullOrWhiteSpace(o.WiserIp)
        && o.WiserIp != "192.168.x.x"
        && !string.IsNullOrWhiteSpace(o.WiserSecret)
        && o.WiserSecret != "your-secret-here";

    /// <summary>Live hub snapshot. Null if hub not configured or fetch failed.</summary>
    public static async Task<HubLiveOverview?> TryFetchOverviewAsync(
        WiserHubFetch hub,
        MonitorOptions options,
        CancellationToken ct = default)
    {
        if (!IsHubConfigured(options))
            return null;

        try
        {
            using var doc = await hub.FetchDomainDocumentAsync(options, ct).ConfigureAwait(false);
            return HubLiveRoomsParser.ParseOverview(doc, BoostPresets);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First live row per room name (usable °C only), for merging onto DB-backed tiles.</summary>
    public static Dictionary<string, HubLiveRoom> ToAvailableTempByRoomName(HubLiveOverview overview) =>
        overview.Rooms
            .Where(r => !IsUnavailableTemp(r.TempC))
            .GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
}
