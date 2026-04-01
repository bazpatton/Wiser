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
    bool HeatingActive);

public static class WiserDomainParser
{
    private const int OfflineShortMinTenths = short.MinValue;
    private const double OfflineShortMinTenthsDiv10 = short.MinValue / 10.0;
    private const double OfflineShortMinTenthsDiv100 = short.MinValue / 100.0;

    public static DomainPollResult ParseDomain(JsonDocument domain)
    {
        var rooms = ParseRooms(domain);
        var relay = ParseHeatingRelayOn(domain.RootElement);
        var active = relay || rooms.Any(s => s.HeatDemand != 0);
        return new DomainPollResult(rooms, relay, active);
    }

    public static IReadOnlyList<WiserRoomSample> ParseRooms(JsonDocument domain)
    {
        if (!domain.RootElement.TryGetProperty("Room", out var roomEl))
            domain.RootElement.TryGetProperty("room", out roomEl);

        if (roomEl.ValueKind != JsonValueKind.Array)
        {
            var keys = string.Join(", ", domain.RootElement.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException($"No Room array in domain JSON (keys: {keys})");
        }

        var list = new List<WiserRoomSample>();
        foreach (var room in roomEl.EnumerateArray())
        {
            var name = RoomName(room);
            var temp = RoomTemperatureC(room);
            if (temp is null)
                continue;
            list.Add(new WiserRoomSample(
                name,
                temp.Value,
                RoomSetpointC(room),
                HeatDemand(room),
                RoomPercentageDemand(room)));
        }

        return list;
    }

    private static string RoomName(JsonElement room)
    {
        if (room.TryGetProperty("Name", out var n))
            return n.GetString()?.Trim() ?? "Room";
        if (room.TryGetProperty("name", out n))
            return n.GetString()?.Trim() ?? "Room";
        return "Room";
    }

    private static double? TenthsToC(JsonElement? tenthsEl)
    {
        if (tenthsEl is null || tenthsEl.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (!tenthsEl.Value.TryGetDouble(out var v))
            return null;
        // Some offline TRVs report short.MinValue in different scaled forms; ignore those samples.
        if (Math.Abs(v - OfflineShortMinTenths) < 0.5
            || Math.Abs(v - OfflineShortMinTenthsDiv10) < 0.05
            || Math.Abs(v - OfflineShortMinTenthsDiv100) < 0.005)
            return null;
        if (v >= 2000)
            return null;
        var c = v / 10.0;
        if (c < -50 || c > 80)
            return null;
        return c;
    }

    private static double? RoomTemperatureC(JsonElement room)
    {
        if (room.TryGetProperty("CalculatedTemperature", out var t))
        {
            var c = TenthsToC(t);
            if (c is not null)
                return c;
        }
        if (room.TryGetProperty("DisplayedTemperature", out t))
            return TenthsToC(t);
        return null;
    }

    private static double? RoomSetpointC(JsonElement room)
    {
        if (room.TryGetProperty("CurrentSetPoint", out var t))
        {
            var c = TenthsToC(t);
            if (c is not null)
                return c;
        }
        if (room.TryGetProperty("ScheduledSetPoint", out t))
            return TenthsToC(t);
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
