using System.Text.Json;

namespace Wiser.Monitor.Services;

/// <summary>
/// Fetches current room temperatures from the hub for UI that should match <c>/rooms</c> (live),
/// with SQLite used only as a fallback when the hub is unavailable.
/// </summary>
public static class LiveHubRoomTemps
{
    /// <summary>Pages read the editable presets from the store; the overview carries the defaults.</summary>
    private static IReadOnlyList<BoostPresetInfo> BoostPresets => QuickActionPresets.Default.Boosts;

    /// <summary>Same sentinel as <see cref="Rooms"/> offline handling.</summary>
    public static bool IsUnavailableTemp(double tempC) =>
        Math.Abs(tempC - (-327.68)) < 0.001;

    public static bool IsHubConfigured(MonitorOptions o) => HubConfiguration.IsConfigured(o);

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

    /// <summary>
    /// One hub domain fetch parsed for both the live overview and schedule savings hints (Home page).
    /// Either part is null if the hub is not configured, the fetch failed, or that parse failed.
    /// </summary>
    public static async Task<(HubLiveOverview? Overview, HubScheduleSavingsHints? SavingsHints)> TryFetchOverviewWithSavingsHintsAsync(
        WiserHubFetch hub,
        MonitorOptions options,
        CancellationToken ct = default)
    {
        if (!IsHubConfigured(options))
            return (null, null);

        JsonDocument doc;
        try
        {
            doc = await hub.FetchDomainDocumentAsync(options, ct).ConfigureAwait(false);
        }
        catch
        {
            return (null, null);
        }

        using (doc)
        {
            HubLiveOverview? overview = null;
            HubScheduleSavingsHints? hints = null;
            try
            {
                overview = HubLiveRoomsParser.ParseOverview(doc, BoostPresets);
            }
            catch
            {
                // leave null
            }

            try
            {
                hints = HubSchedulesParser.AnalyzeSavingsHints(doc);
            }
            catch
            {
                // leave null
            }

            return (overview, hints);
        }
    }

    /// <summary>First live row per room name (usable °C only), for merging onto DB-backed tiles.</summary>
    public static Dictionary<string, HubLiveRoom> ToAvailableTempByRoomName(HubLiveOverview overview) =>
        overview.Rooms
            .Where(r => !IsUnavailableTemp(r.TempC))
            .GroupBy(r => r.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.OrdinalIgnoreCase);
}
