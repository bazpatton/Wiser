namespace Wiser.Monitor.Services;

/// <summary>
/// Tie-break ordering for room heat tiles so main living spaces are not buried after bedrooms
/// when status/trend metrics are similar.
/// </summary>
public static class RoomDisplayPriority
{
    /// <summary>Lower values sort earlier (after status/trend keys in the tile list).</summary>
    public static int KindOrder(string roomName)
    {
        var r = roomName.Trim().ToLowerInvariant();
        if (r.Contains("living", StringComparison.Ordinal) || r.Contains("lounge", StringComparison.Ordinal))
            return 0;
        if (r.Contains("kitchen", StringComparison.Ordinal))
            return 1;
        if (r.Contains("dining", StringComparison.Ordinal))
            return 2;
        if (r.Contains("hall", StringComparison.Ordinal) || r.Contains("landing", StringComparison.Ordinal))
            return 3;
        if (r.Contains("bed", StringComparison.Ordinal) || r.Contains("bedroom", StringComparison.Ordinal))
            return 25;
        if (r.Contains("bath", StringComparison.Ordinal) || r.Contains("ensuite", StringComparison.Ordinal) || r.Contains("wc", StringComparison.Ordinal))
            return 40;
        return 35;
    }
}
