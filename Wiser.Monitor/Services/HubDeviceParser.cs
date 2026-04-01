using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

public sealed record HubDeviceStatus(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("device_type")] string DeviceType,
    [property: JsonPropertyName("battery_percent")] int? BatteryPercent,
    [property: JsonPropertyName("signal")] int? Signal,
    [property: JsonPropertyName("room")] string? Room,
    [property: JsonPropertyName("raw_kind")] string RawKind);

public static class HubDeviceParser
{
    public static IReadOnlyList<HubDeviceStatus> ParseDevices(JsonDocument domain)
    {
        var root = domain.RootElement;
        var list = new List<HubDeviceStatus>();
        var roomNameById = BuildRoomNameMap(root);

        AddFromArray(root, "RoomStat", "Room sensor", roomNameById, list);
        AddFromArray(root, "SmartValve", "TRV", roomNameById, list);
        AddFromArray(root, "Device", "Device", roomNameById, list);
        AddFromRooms(root, roomNameById, list);

        // Deduplicate by id + name + type.
        return list
            .GroupBy(x => $"{x.Id}|{x.Name}|{x.DeviceType}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.DeviceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<int, string> BuildRoomNameMap(JsonElement root)
    {
        var map = new Dictionary<int, string>();
        if (!TryGetArray(root, "Room", out var roomArr))
            return map;

        foreach (var room in roomArr.EnumerateArray())
        {
            var roomId = GetInt(room, "id", "Id");
            var roomName = GetString(room, "Name", "name");
            if (roomId is int id && !string.IsNullOrWhiteSpace(roomName))
                map[id] = roomName!;
        }

        return map;
    }

    private static void AddFromRooms(JsonElement root, IReadOnlyDictionary<int, string> roomNameById, List<HubDeviceStatus> list)
    {
        if (!TryGetArray(root, "Room", out var roomArr))
            return;

        foreach (var room in roomArr.EnumerateArray())
        {
            var roomName = GetString(room, "Name", "name");
            var roomId = GetInt(room, "id", "Id") ?? -1;
            var pctDemand = GetInt(room, "PercentageDemand");

            // Room cards can represent either room sensor or TRV depending on installation.
            // If PercentageDemand is present, bias toward TRV; otherwise room sensor.
            var type = pctDemand.HasValue ? "TRV" : "Room sensor";
            var battery = GetBatteryPercent(room);
            var signal = GetSignal(room);
            if (battery is null && signal is null)
                continue;

            list.Add(new HubDeviceStatus(
                roomId,
                roomName ?? $"Room {roomId}",
                type,
                battery,
                signal,
                roomName ?? (roomNameById.TryGetValue(roomId, out var fromMap) ? fromMap : null),
                "Room"));
        }
    }

    private static void AddFromArray(JsonElement root, string key, string defaultType, IReadOnlyDictionary<int, string> roomNameById, List<HubDeviceStatus> list)
    {
        if (!TryGetArray(root, key, out var arr))
            return;

        foreach (var item in arr.EnumerateArray())
        {
            var id = GetInt(item, "id", "Id", $"{key}Id") ?? -1;
            var deviceType = ResolveDeviceType(item, defaultType);
            var name = GetString(item, "Name", "name", "DisplayName") ?? $"{deviceType} {id}";
            var room = ResolveRoomName(item, roomNameById);
            var battery = GetBatteryPercent(item);
            var signal = GetSignal(item);

            list.Add(new HubDeviceStatus(
                id,
                name,
                deviceType,
                battery,
                signal,
                room,
                key));
        }
    }

    private static int? GetBatteryPercent(JsonElement item)
    {
        var v = GetInt(item, "BatteryPercent", "BatteryPercentage", "BatteryLevel", "Battery");
        v ??= FindFirstNumberByKeyContains(item, "battery");
        if (v is null && FindFirstBoolByKeyContains(item, "lowbattery") == true)
            v = 15;
        if (v is null)
        {
            var batteryState = FindFirstStringByKeyContains(item, "battery");
            if (!string.IsNullOrWhiteSpace(batteryState))
            {
                // Map common textual battery states when numeric % isn't provided.
                v = batteryState switch
                {
                    var s when s.Equals("low", StringComparison.OrdinalIgnoreCase) => 15,
                    var s when s.Equals("ok", StringComparison.OrdinalIgnoreCase) => 60,
                    var s when s.Equals("normal", StringComparison.OrdinalIgnoreCase) => 70,
                    var s when s.Equals("good", StringComparison.OrdinalIgnoreCase) => 80,
                    var s when s.Equals("full", StringComparison.OrdinalIgnoreCase) => 100,
                    _ => null,
                };
            }
        }
        if (v is null)
            return null;
        return Math.Clamp(v.Value, 0, 100);
    }

    private static int? GetSignal(JsonElement item)
    {
        var signal = GetInt(item, "SignalStrength", "Signal", "Rssi", "Lqi", "RSSI", "LQI");
        signal ??= FindFirstNumberByKeyContains(item, "signal");
        signal ??= FindFirstNumberByKeyContains(item, "rssi");
        signal ??= FindFirstNumberByKeyContains(item, "lqi");
        return signal;
    }

    private static string ResolveDeviceType(JsonElement item, string fallback)
    {
        var explicitType = GetString(item, "DeviceType", "ProductType", "ModelType", "Type");
        if (string.IsNullOrWhiteSpace(explicitType))
            return fallback;

        return explicitType!.Contains("valve", StringComparison.OrdinalIgnoreCase) ? "TRV"
            : explicitType.Contains("stat", StringComparison.OrdinalIgnoreCase) ? "Room sensor"
            : explicitType;
    }

    private static string? ResolveRoomName(JsonElement item, IReadOnlyDictionary<int, string> roomNameById)
    {
        var room = GetString(item, "RoomName", "Room", "RoomNameOverride");
        if (!string.IsNullOrWhiteSpace(room))
            return room;

        var roomId = GetInt(item, "RoomId", "RoomID", "room_id");
        if (roomId is int id && roomNameById.TryGetValue(id, out var name))
            return name;

        // Common Wiser linking convention: device id equals room id.
        var deviceId = GetInt(item, "id", "Id", "DeviceId");
        if (deviceId is int did && roomNameById.TryGetValue(did, out var byDeviceId))
            return byDeviceId;

        return null;
    }

    private static int? GetInt(JsonElement item, params string[] names)
    {
        foreach (var n in names)
        {
            if (!item.TryGetProperty(n, out var el))
                continue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i))
                return i;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out i))
                return i;
        }
        return null;
    }

    private static string? GetString(JsonElement item, params string[] names)
    {
        foreach (var n in names)
        {
            if (!item.TryGetProperty(n, out var el) || el.ValueKind != JsonValueKind.String)
                continue;
            var v = el.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    private static bool TryGetArray(JsonElement root, string key, out JsonElement arr)
    {
        if (!root.TryGetProperty(key, out arr))
        {
            arr = default;
            return false;
        }
        return arr.ValueKind == JsonValueKind.Array;
    }

    private static int? FindFirstNumberByKeyContains(JsonElement element, string token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var d))
                        return (int)Math.Round(d, MidpointRounding.AwayFromZero);
                    if (p.Value.ValueKind == JsonValueKind.String && int.TryParse(p.Value.GetString(), out var i))
                        return i;
                }

                var nested = FindFirstNumberByKeyContains(p.Value, token);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstNumberByKeyContains(item, token);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static bool? FindFirstBoolByKeyContains(JsonElement element, string token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        return p.Value.GetBoolean();
                    if (p.Value.ValueKind == JsonValueKind.String && bool.TryParse(p.Value.GetString(), out var b))
                        return b;
                }

                var nested = FindFirstBoolByKeyContains(p.Value, token);
                if (nested is not null)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstBoolByKeyContains(item, token);
                if (nested is not null)
                    return nested;
            }
        }

        return null;
    }

    private static string? FindFirstStringByKeyContains(JsonElement element, string token)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in element.EnumerateObject())
            {
                if (p.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                    && p.Value.ValueKind == JsonValueKind.String)
                {
                    var s = p.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }

                var nested = FindFirstStringByKeyContains(p.Value, token);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindFirstStringByKeyContains(item, token);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }
}
