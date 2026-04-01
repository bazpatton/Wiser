namespace Wiser.Monitor.Services;

/// <summary>
/// Cached merged room name list for <c>GET /api/rooms</c> (store + last hub poll). Invalidated after each successful poll.
/// </summary>
public sealed class ApiRoomsNamesCache
{
    private readonly object _gate = new();
    private IReadOnlyList<string>? _rooms;
    private DateTimeOffset _cachedAtUtc;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public IReadOnlyList<string> GetOrCreate(TemperatureStore store, MonitorState state)
    {
        lock (_gate)
        {
            if (_rooms is not null && DateTimeOffset.UtcNow - _cachedAtUtc < Ttl)
                return _rooms;
        }

        var names = new HashSet<string>(store.ListRooms(), StringComparer.OrdinalIgnoreCase);
        foreach (var n in state.GetLastRooms())
            names.Add(n);
        var list = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        lock (_gate)
        {
            _rooms = list;
            _cachedAtUtc = DateTimeOffset.UtcNow;
            return _rooms;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
            _rooms = null;
    }
}
