using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

public sealed record HubLiveRoom(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("temp_c")] double TempC,
    [property: JsonPropertyName("setpoint_c")] double? SetpointC,
    [property: JsonPropertyName("heat_demand")] int HeatDemand,
    /// <summary>on | off | unknown — valve/output state for UI symbol.</summary>
    [property: JsonPropertyName("radiator_state")] string RadiatorState,
    [property: JsonPropertyName("percentage_demand")] int? PercentageDemand,
    [property: JsonPropertyName("override_timeout_unix")] long? OverrideTimeoutUnix,
    [property: JsonPropertyName("setpoint_origin")] string? SetpointOrigin,
    [property: JsonPropertyName("control_output_state")] string? ControlOutputState);

public sealed record HubLiveOverview(
    IReadOnlyList<HubLiveRoom> Rooms,
    bool HeatingRelayOn,
    bool HeatingActive,
    IReadOnlyList<BoostPresetInfo> BoostPresets,
    bool SystemAway = false,
    double? AwaySetpointLimitC = null);

public sealed record BoostPresetInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("temperature_c")] double TemperatureC,
    [property: JsonPropertyName("minutes")] int Minutes);

public static class HubLiveRoomsParser
{
    private const int OfflineShortMinTenths = short.MinValue;
    private const double OfflineShortMinTenthsDiv10 = short.MinValue / 10.0;
    private const double OfflineShortMinTenthsDiv100 = short.MinValue / 100.0;

    public static HubLiveOverview ParseOverview(JsonDocument domain, IReadOnlyList<BoostPresetInfo> boostPresets)
    {
        var root = domain.RootElement;
        var rooms = ParseRooms(root);
        var relay = ParseHeatingRelayOn(root);
        var active = relay || rooms.Any(r => r.HeatDemand != 0);
        var (systemAway, awayLimitC) = ParseSystemAwayState(root);
        return new HubLiveOverview(rooms, relay, active, boostPresets, systemAway, awayLimitC);
    }

    /// <summary>Matches aioWiserHeatAPI: <c>OverrideType == "Away"</c> and optional <c>AwayModeSetPointLimit</c> (tenths °C).</summary>
    private static (bool Away, double? LimitC) ParseSystemAwayState(JsonElement root)
    {
        if (!TryGetSystemObject(root, out var sys))
            return (false, null);

        var away = false;
        if (TryGetStringProperty(sys, "OverrideType", out var ot)
            && string.Equals(ot, "Away", StringComparison.OrdinalIgnoreCase))
            away = true;

        double? limitC = null;
        if (TryGetIntProperty(sys, "AwayModeSetPointLimit", out var tenths))
            limitC = tenths / 10.0;

        return (away, limitC);
    }

    private static bool TryGetSystemObject(JsonElement root, out JsonElement sys)
    {
        sys = default;
        if (root.ValueKind != JsonValueKind.Object)
            return false;
        if (root.TryGetProperty("System", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            sys = s;
            return true;
        }

        if (root.TryGetProperty("system", out s) && s.ValueKind == JsonValueKind.Object)
        {
            sys = s;
            return true;
        }

        return false;
    }

    private static bool TryGetStringProperty(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var p))
            return false;
        if (p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString()?.Trim() ?? "";
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetIntProperty(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var p))
            return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value))
            return true;
        return p.ValueKind == JsonValueKind.String
            && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static IReadOnlyList<HubLiveRoom> ParseRooms(JsonElement root)
    {
        if (!root.TryGetProperty("Room", out var roomEl))
            root.TryGetProperty("room", out roomEl);

        if (roomEl.ValueKind != JsonValueKind.Array)
        {
            var keys = string.Join(", ", root.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException($"No Room array in domain JSON (keys: {keys})");
        }

        var list = new List<HubLiveRoom>();
        foreach (var room in roomEl.EnumerateArray())
        {
            var id = RoomId(room);
            if (id < 0)
                continue;
            var name = RoomName(room);
            var temp = RoomTemperatureC(room);
            if (temp is null)
                continue;
            list.Add(new HubLiveRoom(
                id,
                name,
                RoomMode(room),
                temp.Value,
                RoomSetpointC(room),
                HeatDemand(room),
                RadiatorState(room),
                RoomPercentageDemand(room),
                OverrideTimeout(room),
                SetpointOrigin(room),
                ControlOutputStateStr(room)));
        }

        return list;
    }

    private static int RoomId(JsonElement room)
    {
        if (room.TryGetProperty("id", out var id) && id.TryGetInt32(out var v))
            return v;
        if (room.TryGetProperty("Id", out id) && id.TryGetInt32(out v))
            return v;
        return -1;
    }

    private static string? RoomMode(JsonElement room)
    {
        if (room.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String)
            return m.GetString();
        if (room.TryGetProperty("mode", out m) && m.ValueKind == JsonValueKind.String)
            return m.GetString();
        return null;
    }

    private static string RoomName(JsonElement room)
    {
        if (room.TryGetProperty("Name", out var n))
            return n.GetString()?.Trim() ?? "Room";
        if (room.TryGetProperty("name", out n))
            return n.GetString()?.Trim() ?? "Room";
        return "Room";
    }

    private static string? ControlOutputStateStr(JsonElement room)
    {
        if (!room.TryGetProperty("ControlOutputState", out var s) || s.ValueKind != JsonValueKind.String)
            return null;
        return s.GetString();
    }

    private static long? OverrideTimeout(JsonElement room)
    {
        if (!room.TryGetProperty("OverrideTimeoutUnixTime", out var u))
            return null;
        if (u.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (u.TryGetInt64(out var l))
            return l;
        if (u.TryGetDouble(out var d))
            return (long)d;
        return null;
    }

    private static string? SetpointOrigin(JsonElement room)
    {
        if (room.TryGetProperty("SetpointOrigin", out var o) && o.ValueKind == JsonValueKind.String)
            return o.GetString();
        if (room.TryGetProperty("SetPointOrigin", out o) && o.ValueKind == JsonValueKind.String)
            return o.GetString();
        return null;
    }

    private static double? TenthsToC(JsonElement tenthsEl)
    {
        if (tenthsEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (!tenthsEl.TryGetDouble(out var v))
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
        if (WiserHubRoomJson.TryGetPropertyIgnoreCase(room, ["CurrentSetPoint", "CurrentSetpoint"], out var t))
        {
            var c = TenthsToC(t);
            if (c is not null)
                return c;
        }

        if (WiserHubRoomJson.TryGetPropertyIgnoreCase(room, ["ScheduledSetPoint", "ScheduledSetpoint"], out t))
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

    private static string RadiatorState(JsonElement room)
    {
        if (HeatDemand(room) != 0)
            return "on";
        if (room.TryGetProperty("ControlOutputState", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var st = s.GetString();
            if (st is not null &&
                (st.Equals("Off", StringComparison.OrdinalIgnoreCase)
                 || st.Equals("Close", StringComparison.OrdinalIgnoreCase)))
                return "off";
        }

        return "unknown";
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
