namespace Wiser.Monitor.Services;

/// <summary>
/// App-wide last successful hub room snapshot (singleton). Filled by <see cref="Workers.WiserPollWorker"/> every poll
/// and by pages that fetch the hub directly, so any new page load can render live data immediately and only refresh
/// from the hub in the background when the snapshot is stale.
/// </summary>
public sealed class RoomsLiveDataCache
{
    private sealed record Entry(HubLiveOverview Overview, HubScheduleSavingsHints? SavingsHints, DateTimeOffset CachedAt);

    // Swapped atomically; readers never see a half-updated snapshot.
    private volatile Entry? _entry;

    public bool TryGetSnapshot(out HubLiveOverview overview, out DateTimeOffset cachedAt)
    {
        var e = _entry;
        if (e is null)
        {
            overview = default!;
            cachedAt = default;
            return false;
        }

        overview = e.Overview;
        cachedAt = e.CachedAt;
        return true;
    }

    /// <summary>Snapshot plus savings hints, only when no older than <paramref name="maxAge"/>.</summary>
    public bool TryGetFresh(TimeSpan maxAge, out HubLiveOverview overview, out HubScheduleSavingsHints? savingsHints)
    {
        var e = _entry;
        if (e is null || DateTimeOffset.Now - e.CachedAt > maxAge)
        {
            overview = default!;
            savingsHints = null;
            return false;
        }

        overview = e.Overview;
        savingsHints = e.SavingsHints;
        return true;
    }

    /// <param name="savingsHints">Null keeps the previously cached hints (schedules change far less often than temps).</param>
    public void Update(HubLiveOverview overview, HubScheduleSavingsHints? savingsHints = null)
    {
        var copy = new HubLiveOverview(
            overview.Rooms.ToList(),
            overview.HeatingRelayOn,
            overview.HeatingActive,
            overview.BoostPresets,
            overview.SystemAway,
            overview.AwaySetpointLimitC);
        _entry = new Entry(copy, savingsHints ?? _entry?.SavingsHints, DateTimeOffset.Now);
    }
}
