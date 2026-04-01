namespace Wiser.Monitor.Services;

/// <summary>
/// Holds the last successful hub room snapshot for the Blazor circuit so revisiting <c>/rooms</c>
/// can render immediately (e.g. deep links with <c>#room-…</c>) before a background refresh.
/// </summary>
public sealed class RoomsLiveDataCache
{
    private HubLiveOverview? _overview;
    private DateTimeOffset _cachedAt;

    public bool TryGetSnapshot(out HubLiveOverview overview, out DateTimeOffset cachedAt)
    {
        if (_overview is null)
        {
            overview = default!;
            cachedAt = default;
            return false;
        }

        overview = _overview;
        cachedAt = _cachedAt;
        return true;
    }

    public void Update(HubLiveOverview overview)
    {
        _overview = new HubLiveOverview(
            overview.Rooms.ToList(),
            overview.HeatingRelayOn,
            overview.HeatingActive,
            overview.BoostPresets);
        _cachedAt = DateTimeOffset.Now;
    }
}
