using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed record WiserRoomSample(
    string Name,
    double TempC,
    double? SetpointC,
    int HeatDemand,
    int? PercentageDemand);

/// <summary>One hub poll: rooms plus heating/boiler proxy flags (matches Wiser.Control <c>IsHeatingActive</c> idea).</summary>
public sealed record DomainPollResult(
    IReadOnlyList<WiserRoomSample> Rooms,
    bool HeatingRelayOn,
    bool HeatingActive,
    IReadOnlyList<ExcludedRoomReading> ExcludedReadings);

public sealed record ExcludedRoomReading(
    string Room,
    string Source,
    string Reason,
    double? RawValue);

public static class WiserDomainParser
{
    private const int OfflineShortMinTenths = short.MinValue;
    private const double OfflineShortMinTenthsDiv10 = short.MinValue / 10.0;
    private const double OfflineShortMinTenthsDiv100 = short.MinValue / 100.0;

    public static DomainPollResult ParseDomain(JsonDocument domain)
    {
        var (rooms, excluded) = ParseRooms(domain);
        var relay = ParseHeatingRelayOn(domain.RootElement);
        var active = relay || rooms.Any(s => s.HeatDemand != 0);
        return new DomainPollResult(rooms, relay, active, excluded);
    }

    public static (IReadOnlyList<WiserRoomSample> Rooms, IReadOnlyList<ExcludedRoomReading> Excluded) ParseRooms(JsonDocument domain)
    {
        if (!domain.RootElement.TryGetProperty("Room", out var roomEl))
            domain.RootElement.TryGetProperty("room", out roomEl);

        if (roomEl.ValueKind != JsonValueKind.Array)
        {
            var keys = string.Join(", ", domain.RootElement.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException($"No Room array in domain JSON (keys: {keys})");
        }

        var list = new List<WiserRoomSample>();
        var excluded = new List<ExcludedRoomReading>();
        foreach (var room in roomEl.EnumerateArray())
        {
            var name = RoomName(room);
            var temp = RoomTemperatureC(room, out var reason, out var rawValue);
            if (temp is null)
            {
                if (!string.IsNullOrWhiteSpace(reason))
                    excluded.Add(new ExcludedRoomReading(name, "hub_domain_parser", reason!, rawValue));
                continue;
            }
            list.Add(new WiserRoomSample(
                name,
                temp.Value,
                RoomSetpointC(room),
                HeatDemand(room),
                RoomPercentageDemand(room)));
        }

        return (list, excluded);
    }

    private static string RoomName(JsonElement room)
    {
        if (room.TryGetProperty("Name", out var n))
            return n.GetString()?.Trim() ?? "Room";
        if (room.TryGetProperty("name", out n))
            return n.GetString()?.Trim() ?? "Room";
        return "Room";
    }

    private static double? TenthsToC(JsonElement? tenthsEl, out string? reason, out double? rawValue)
    {
        reason = null;
        rawValue = null;
        if (tenthsEl is null || tenthsEl.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (!tenthsEl.Value.TryGetDouble(out var v))
        {
            reason = "invalid_value";
            return null;
        }
        rawValue = v;
        // Some offline TRVs report short.MinValue in different scaled forms; ignore those samples.
        if (Math.Abs(v - OfflineShortMinTenths) < 0.5
            || Math.Abs(v - OfflineShortMinTenthsDiv10) < 0.05
            || Math.Abs(v - OfflineShortMinTenthsDiv100) < 0.005)
        {
            reason = "offline_sentinel";
            return null;
        }
        if (v >= 2000)
        {
            reason = "out_of_range";
            return null;
        }
        var c = v / 10.0;
        if (c < -50 || c > 80)
        {
            reason = "out_of_range";
            return null;
        }
        return c;
    }

    private static double? RoomTemperatureC(JsonElement room, out string? reason, out double? rawValue)
    {
        reason = null;
        rawValue = null;
        if (room.TryGetProperty("CalculatedTemperature", out var t))
        {
            var c = TenthsToC(t, out reason, out rawValue);
            if (c is not null)
                return c;
        }
        if (room.TryGetProperty("DisplayedTemperature", out t))
        {
            var c = TenthsToC(t, out reason, out rawValue);
            if (c is not null)
                return c;
        }

        return null;
    }

    private static double? RoomSetpointC(JsonElement room)
    {
        if (room.TryGetProperty("CurrentSetPoint", out var t))
        {
            var c = TenthsToC(t, out _, out _);
            if (c is not null)
                return c;
        }
        if (room.TryGetProperty("ScheduledSetPoint", out t))
            return TenthsToC(t, out _, out _);
        return null;
    }

    private static int? RoomPercentageDemand(JsonElement room)
    {
        if (!room.TryGetProperty("PercentageDemand", out var d))
            return null;
        if (!d.TryGetInt32(out var p))
            return null;
        if (p < 0)
            return 0;
        if (p > 100)
            return 100;
        return p;
    }

    private static int HeatDemand(JsonElement room)
    {
        if (room.TryGetProperty("PercentageDemand", out var d) && d.TryGetInt32(out var p) && p > 0)
            return 1;
        if (room.TryGetProperty("ControlOutputState", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var st = s.GetString();
            if (st is not null)
            {
                if (st.Equals("On", StringComparison.OrdinalIgnoreCase))
                    return 1;
                if (st.Equals("Open", StringComparison.OrdinalIgnoreCase))
                    return 1;
            }
        }
        return 0;
    }

    private static bool ParseHeatingRelayOn(JsonElement root)
    {
        if (!root.TryGetProperty("HeatingChannel", out var arr))
            root.TryGetProperty("heatingChannel", out arr);
        if (arr.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var ch in arr.EnumerateArray())
        {
            if (!ch.TryGetProperty("HeatingRelayState", out var s) || s.ValueKind != JsonValueKind.String)
                continue;
            if (string.Equals(s.GetString(), "On", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
