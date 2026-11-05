namespace Wiser.Monitor.Services;

/// <summary>Shared display utilities used by Home, Charts, and Rooms pages.</summary>
public static class RoomDisplayHelpers
{
    private const double InactiveOffsetC = 2.0;

    /// <summary>
    /// Returns the comfort range for a room. If <paramref name="customRanges"/> contains an entry
    /// for this room it takes priority; otherwise falls back to the room-name heuristic.
    /// </summary>
    public static (double Min, double Max) GetRecommendedTempRange(
        string roomName, bool isActive,
        IReadOnlyDictionary<string, (double Min, double Max)>? customRanges = null)
    {
        (double Min, double Max) baseRange;
        if (customRanges is not null && customRanges.TryGetValue(roomName, out var custom))
        {
            baseRange = custom;
        }
        else
        {
            var r = roomName.ToLowerInvariant();
            baseRange = r switch
            {
                _ when r.Contains("bath")                           => (21.0, 23.0),
                _ when r.Contains("bed")                            => (17.0, 19.0),
                _ when r.Contains("kitchen")                        => (18.0, 20.0),
                _ when r.Contains("hall") || r.Contains("landing")  => (16.0, 18.0),
                _ when r.Contains("living") || r.Contains("lounge") => (20.0, 22.0),
                _                                                   => (19.0, 21.0),
            };
        }

        return isActive
            ? baseRange
            : (baseRange.Min - InactiveOffsetC, baseRange.Max - InactiveOffsetC);
    }

    public static string FormatTrend(double trendDelta)
    {
        if (trendDelta > 0.4)
            return $"up {trendDelta:+0.0;-0.0;0.0}C";
        if (trendDelta < -0.4)
            return $"down {trendDelta:+0.0;-0.0;0.0}C";
        return $"steady {trendDelta:+0.0;-0.0;0.0}C";
    }

    public static bool IsWithinIgnoreWindow(
        long timestampUtc, TimeSpan ignoreStart, TimeSpan ignoreEnd, TimeZoneInfo zone)
    {
        if (ignoreStart == ignoreEnd)
            return false;

        var localTod = TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(timestampUtc), zone).TimeOfDay;

        return ignoreStart < ignoreEnd
            ? localTod >= ignoreStart && localTod < ignoreEnd
            : localTod >= ignoreStart || localTod < ignoreEnd;
    }

    public static string GetRoomDetailsHref(string roomName) =>
        RoomLinkHelper.RoomsPagePath(roomName);

    /// <summary>
    /// End of a room's timed override (boost) when it is still in the future.
    /// The hub reports <c>OverrideTimeoutUnixTime</c> in seconds or milliseconds depending on firmware.
    /// </summary>
    public static DateTimeOffset? GetActiveOverrideEndUtc(HubLiveRoom room) =>
        GetActiveOverrideEndUtc(room, DateTimeOffset.UtcNow);

    public static DateTimeOffset? GetActiveOverrideEndUtc(HubLiveRoom room, DateTimeOffset now)
    {
        var raw = room.OverrideTimeoutUnix;
        if (raw is null or 0)
            return null;

        DateTimeOffset endUtc;
        try
        {
            var v = raw.Value;
            endUtc = v > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(v)
                : DateTimeOffset.FromUnixTimeSeconds(v);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return endUtc > now ? endUtc : null;
    }
}
