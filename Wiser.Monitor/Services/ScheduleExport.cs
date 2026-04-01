using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public static class ScheduleExport
{
    private static readonly string[] DayOrder =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    ];

    /// <summary>UTF-8 JSON: hub <c>Schedule</c> array plus room → schedule_id map and export timestamp.</summary>
    public static byte[] BuildSchedulesJsonBytes(JsonDocument domain)
    {
        var root = domain.RootElement;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("exported_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            writer.WritePropertyName("hub_local_time");
            if (root.TryGetProperty("System", out var sys) && sys.TryGetProperty("LocalDateAndTime", out var ldt))
            {
                ldt.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            writer.WritePropertyName("schedules");
            if (TryGetArray(root, "Schedule", out var schedArr))
                schedArr.WriteTo(writer);
            else
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            writer.WritePropertyName("rooms");
            writer.WriteStartArray();
            if (TryGetArray(root, "Room", out var roomArr))
            {
                foreach (var room in roomArr.EnumerateArray())
                {
                    writer.WriteStartObject();
                    WriteInt(writer, "id", room, "id", "Id");
                    WriteString(writer, "Name", room, "Name", "name");
                    WriteInt(writer, "ScheduleId", room, "ScheduleId", "scheduleId");
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static string BuildSchedulesCsv(JsonDocument domain)
    {
        var root = domain.RootElement;
        var scheduleIdToRooms = new Dictionary<int, List<string>>();
        if (TryGetArray(root, "Room", out var rooms))
        {
            foreach (var room in rooms.EnumerateArray())
            {
                var rid = GetInt(room, "id", "Id");
                var name = GetString(room, "Name", "name");
                if (string.IsNullOrWhiteSpace(name))
                    name = rid >= 0 ? $"Room {rid}" : "Room";
                var sid = GetInt(room, "ScheduleId", "scheduleId");
                if (sid < 0)
                    continue;
                if (!scheduleIdToRooms.TryGetValue(sid, out var list))
                {
                    list = [];
                    scheduleIdToRooms[sid] = list;
                }

                list.Add(name);
            }
        }

        foreach (var list in scheduleIdToRooms.Values)
            list.Sort(StringComparer.OrdinalIgnoreCase);

        var csv = new StringBuilder();
        csv.AppendLine("schedule_id,day,time_raw,time_hhmm,temp_tenths,temp_label,rooms");

        if (!TryGetArray(root, "Schedule", out var schedules))
            return csv.ToString();

        foreach (var sched in schedules.EnumerateArray().OrderBy(GetScheduleId))
        {
            var sid = GetScheduleId(sched);
            if (sid < 0)
                continue;
            var roomsCol = scheduleIdToRooms.TryGetValue(sid, out var rn)
                ? EscapeCsv(string.Join("; ", rn))
                : "";

            foreach (var day in DayOrder)
            {
                if (!sched.TryGetProperty(day, out var dayEl) || dayEl.ValueKind != JsonValueKind.Object)
                    continue;
                if (!dayEl.TryGetProperty("SetPoints", out var pts) || pts.ValueKind != JsonValueKind.Array)
                    continue;

                var points = new List<(int Time, int Deg)>();
                foreach (var pt in pts.EnumerateArray())
                {
                    var tim = GetIntStrict(pt, "Time");
                    var deg = GetIntStrict(pt, "DegreesC");
                    if (tim is null || deg is null)
                        continue;
                    points.Add((tim.Value, deg.Value));
                }

                points.Sort((a, b) => HubScheduleHints.NormalizeSeconds(a.Time).CompareTo(HubScheduleHints.NormalizeSeconds(b.Time)));

                foreach (var (timeRaw, degTenths) in points)
                {
                    csv.Append(sid.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(EscapeCsv(day)).Append(',')
                        .Append(timeRaw.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(EscapeCsv(HubScheduleHints.FormatTimeLabel(timeRaw))).Append(',')
                        .Append(degTenths.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(EscapeCsv(HubScheduleHints.FormatDegreesC(degTenths))).Append(',')
                        .Append(roomsCol)
                        .AppendLine();
                }
            }
        }

        return csv.ToString();
    }

    private static int GetScheduleId(JsonElement sched)
    {
        if (sched.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var i))
            return i;
        if (sched.TryGetProperty("Id", out id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out i))
            return i;
        return -1;
    }

    private static bool TryGetArray(JsonElement root, string name, out JsonElement arr)
    {
        if (!root.TryGetProperty(name, out arr))
        {
            var alt = char.ToLowerInvariant(name[0]) + name[1..];
            if (!root.TryGetProperty(alt, out arr))
            {
                arr = default;
                return false;
            }
        }

        return arr.ValueKind == JsonValueKind.Array;
    }

    private static void WriteInt(Utf8JsonWriter writer, string name, JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            {
                writer.WriteNumber(name, i);
                return;
            }
        }

        writer.WriteNull(name);
    }

    private static void WriteString(Utf8JsonWriter writer, string name, JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!obj.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                writer.WriteString(name, s);
                return;
            }
        }

        writer.WriteString(name, "");
    }

    private static int GetInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                return i;
        }

        return -1;
    }

    private static int? GetIntStrict(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
            return i;
        return null;
    }

    private static string GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        }

        return "";
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var needs = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needs)
            return value;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
