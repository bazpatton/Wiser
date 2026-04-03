namespace Wiser.Monitor.Services;

public sealed class AlertLatches
{
    public bool LatchedHigh { get; set; }
    public bool LatchedLow { get; set; }
}

public sealed class MonitorState(TemperatureStore store)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AlertLatches> _latchesByRoom = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> LastRooms { get; private set; } = [];
    public string? LastError { get; private set; }
    public long? LastOkUnix { get; private set; }

    public AlertLatches GetLatchesForRoom(string roomName)
    {
        var key = roomName.Trim();
        lock (_gate)
        {
            if (!_latchesByRoom.TryGetValue(key, out var latch))
            {
                var db = store.GetRoomAlertLatch(key);
                latch = new AlertLatches
                {
                    LatchedHigh = db?.LatchedHigh ?? false,
                    LatchedLow = db?.LatchedLow ?? false,
                };
                _latchesByRoom[key] = latch;
            }

            return latch;
        }
    }

    public void PruneLatchesNotIn(IReadOnlyCollection<string> currentRoomNames)
    {
        lock (_gate)
        {
            var keep = new HashSet<string>(
                currentRoomNames.Select(n => n.Trim()),
                StringComparer.OrdinalIgnoreCase);
            foreach (var k in _latchesByRoom.Keys.ToList())
            {
                if (!keep.Contains(k))
                    _latchesByRoom.Remove(k);
            }
        }
    }

    public void SetLastRooms(IReadOnlyList<string> rooms)
    {
        lock (_gate)
            LastRooms = rooms;
    }

    public IReadOnlyList<string> GetLastRooms()
    {
        lock (_gate)
            return LastRooms;
    }

    public void SetPollFailure(string message)
    {
        lock (_gate)
            LastError = message;
    }

    public void SetPollSuccess()
    {
        lock (_gate)
        {
            LastError = null;
            LastOkUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    public (string? LastError, long? LastOkUnix) Snapshot()
    {
        lock (_gate)
            return (LastError, LastOkUnix);
    }
}
