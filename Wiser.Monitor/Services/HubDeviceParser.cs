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
        AddFromArray(root, "RoomStat", "Room sensor", list);
        AddFromArray(root, "SmartValve", "TRV", list);
        AddFromArray(root, "Device", "Device", list);
        AddFromRooms(root, list);

        // Deduplicate by id + name + type.
        return list
            .GroupBy(x => $"{x.Id}|{x.Name}|{x.DeviceType}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.DeviceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFromRooms(JsonElement root, List<HubDeviceStatus> list)
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
            var signal = GetInt(room, "SignalStrength", "Signal", "Rssi");
            if (battery is null && signal is null)
                continue;

            list.Add(new HubDeviceStatus(
                roomId,
                roomName ?? $"Room {roomId}",
                type,
                battery,
                signal,
                roomName,
                "Room"));
        }
    }

    private static void AddFromArray(JsonElement root, string key, string deviceType, List<HubDeviceStatus> list)
    {
        if (!TryGetArray(root, key, out var arr))
            return;

        foreach (var item in arr.EnumerateArray())
        {
            var id = GetInt(item, "id", "Id", $"{key}Id") ?? -1;
            var name = GetString(item, "Name", "name", "DisplayName") ?? $"{deviceType} {id}";
            var room = GetString(item, "RoomName", "Room", "RoomNameOverride");
            var battery = GetBatteryPercent(item);
            var signal = GetInt(item, "SignalStrength", "Signal", "Rssi");

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
        if (v is null)
            return null;
        return Math.Clamp(v.Value, 0, 100);
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
}
