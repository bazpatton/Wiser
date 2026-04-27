using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed record HubRoomScheduleRow(
    string RoomName,
    int RoomId,
    int ScheduleId,
    string NextChangeText,
    string TimelineTodayText);

public sealed record HubScheduleDayColumn(string DayName, string TimelineText);

public sealed record HubScheduleProgram(int ScheduleId, IReadOnlyList<string> RoomNames, IReadOnlyList<HubScheduleDayColumn> Days);

public sealed record HubSchedulesOverview(
    string ClockHint,
    IReadOnlyList<HubRoomScheduleRow> RoomRows,
    IReadOnlyList<HubScheduleProgram> Programs);

public sealed record HubScheduleSavingsHints(
    int RoomsWithoutUserProgram,
    int RoomsOnUserProgram,
    int MaxUserProgramSetpointTenths);

public static class HubSchedulesParser
{
    private static readonly string[] DayNames =
    [
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
    ];

    public static HubSchedulesOverview ParseOverview(JsonDocument domain)
    {
        var root = domain.RootElement;
        var system = TryGetObject(root, "System");
        var todayName = HubScheduleHints.ResolveCurrentDay(system);
        var currentSeconds = HubScheduleHints.ResolveCurrentSeconds(system);
        var clockHint = BuildClockHint(system);

        var schedules = ParseSchedules(root);
        var rooms = ParseRooms(root);

        var roomRows = new List<HubRoomScheduleRow>();
        foreach (var room in rooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var sid = room.ScheduleId;
            if (!schedules.TryGetValue(sid, out var sched))
            {
                roomRows.Add(new HubRoomScheduleRow(
                    room.Name,
                    room.Id,
                    sid,
                    "No schedule linked.",
                    "Today: no schedule."));
                continue;
            }

            var next = BuildNextChangeText(sched, todayName, currentSeconds);
            var todayLine = BuildTodayTimeline(sched, todayName);
            roomRows.Add(new HubRoomScheduleRow(room.Name, room.Id, room.ScheduleId, next, todayLine));
        }

        var scheduleIdToRooms = rooms
            .GroupBy(r => r.ScheduleId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());

        var programs = new List<HubScheduleProgram>();
        foreach (var kv in schedules.OrderBy(x => x.Key))
        {
            scheduleIdToRooms.TryGetValue(kv.Key, out var names);
            names ??= [];
            var days = new List<HubScheduleDayColumn>();
            foreach (var day in DayNames)
            {
                var line = BuildDayTimeline(kv.Value, day);
                days.Add(new HubScheduleDayColumn(day, line));
            }

            programs.Add(new HubScheduleProgram(kv.Key, names, days));
        }

        return new HubSchedulesOverview(clockHint, roomRows, programs);
    }

    public static HubScheduleSavingsHints AnalyzeSavingsHints(JsonDocument domain)
    {
        var root = domain.RootElement;
        var schedules = ParseSchedules(root);
        var rooms = ParseRooms(root);

        var roomsWithoutProgram = rooms.Count(r =>
            !HubScheduleIdPolicy.IsUserProgramScheduleId(r.ScheduleId) || !schedules.ContainsKey(r.ScheduleId));
        var roomsOnProgram = Math.Max(0, rooms.Count - roomsWithoutProgram);

        var maxTenths = int.MinValue;
        foreach (var schedule in schedules.Values)
        {
            foreach (var day in DayNames)
            {
                var points = schedule.OrderedPoints(day);
                if (points.Count == 0)
                    continue;
                var dayMax = points.Max(static p => p.DegreesC);
                if (dayMax > maxTenths)
                    maxTenths = dayMax;
            }
        }

        return new HubScheduleSavingsHints(
            roomsWithoutProgram,
            roomsOnProgram,
            maxTenths == int.MinValue ? -1 : maxTenths);
    }

    private static string BuildClockHint(JsonElement? system)
    {
        if (system is not { ValueKind: JsonValueKind.Object } sys
            || !sys.TryGetProperty("LocalDateAndTime", out var ldt)
            || ldt.ValueKind != JsonValueKind.Object)
        {
            return $"Hub time — using browser clock for “today” ({DateTime.Now:dddd HH:mm}).";
        }

        var day = ldt.TryGetProperty("Day", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        long timeRaw = 0;
        if (ldt.TryGetProperty("Time", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tr))
            timeRaw = tr;

        return string.IsNullOrWhiteSpace(day)
            ? "Hub local date/time partial in payload."
            : $"Hub clock: {day} · raw Time={timeRaw} (used for “next change” math).";
    }

    private static Dictionary<int, ScheduleDays> ParseSchedules(JsonElement root)
    {
        var map = new Dictionary<int, ScheduleDays>();
        if (!TryGetArray(root, "Schedule", out var arr))
            return map;

        foreach (var el in arr.EnumerateArray())
        {
            var id = GetInt(el, "id", "Id");
            if (id < 0)
                continue;
            var days = new Dictionary<string, List<SetPoint>>(StringComparer.Ordinal);
            foreach (var day in DayNames)
            {
                if (!el.TryGetProperty(day, out var dayEl) || dayEl.ValueKind != JsonValueKind.Object)
                    continue;
                if (!dayEl.TryGetProperty("SetPoints", out var sp) || sp.ValueKind != JsonValueKind.Array)
                    continue;
                var list = new List<SetPoint>();
                foreach (var pt in sp.EnumerateArray())
                {
                    var tim = GetInt(pt, "Time");
                    var deg = GetInt(pt, "DegreesC");
                    if (tim >= 0 && deg != int.MinValue)
                        list.Add(new SetPoint(tim, deg));
                }
                if (list.Count > 0)
                    days[day] = list;
            }

            map[id] = new ScheduleDays(days);
        }

        return map;
    }

    private static List<ParsedRoom> ParseRooms(JsonElement root)
    {
        var list = new List<ParsedRoom>();
        if (!TryGetArray(root, "Room", out var arr))
            return list;

        foreach (var el in arr.EnumerateArray())
        {
            var id = GetInt(el, "id", "Id");
            if (id < 0)
                continue;
            var name = GetString(el, "Name", "name");
            if (string.IsNullOrWhiteSpace(name))
                name = $"Room {id}";
            var sid = GetInt(el, "ScheduleId", "scheduleId");
            if (sid < 0)
                sid = 0;
            list.Add(new ParsedRoom(id, name, sid));
        }

        return list;
    }

    private static string BuildTodayTimeline(ScheduleDays sched, string dayName)
    {
        var points = sched.OrderedPoints(dayName);
        if (points.Count == 0)
            return "Today: no setpoints configured.";

        var entries = points
            .Select(p => $"{HubScheduleHints.FormatTimeLabel(p.Time)} {HubScheduleHints.FormatDegreesC(p.DegreesC)}");
        return $"Today: {string.Join("  ·  ", entries)}";
    }

    private static string BuildDayTimeline(ScheduleDays sched, string dayName)
    {
        var points = sched.OrderedPoints(dayName);
        if (points.Count == 0)
            return "—";

        return string.Join(" · ", points.Select(p =>
            $"{HubScheduleHints.FormatTimeLabel(p.Time)} {HubScheduleHints.FormatDegreesC(p.DegreesC)}"));
    }

    private static string BuildNextChangeText(ScheduleDays sched, string currentDay, int currentSeconds)
    {
        var startIndex = Array.IndexOf(DayNames, currentDay);
        if (startIndex < 0)
            startIndex = 0;

        for (var offset = 0; offset < 7; offset++)
        {
            var day = DayNames[(startIndex + offset + 7) % 7];
            var points = sched.OrderedPoints(day);
            if (points.Count == 0)
                continue;

            SetPoint candidate;
            if (offset == 0)
            {
                var idx = points.FindIndex(p => HubScheduleHints.NormalizeSeconds(p.Time) > currentSeconds);
                if (idx < 0)
                    continue;
                candidate = points[idx];
            }
            else
                candidate = points[0];

            var when = offset == 0 ? "today" : $"on {day}";
            return $"Next change {when} at {HubScheduleHints.FormatTimeLabel(candidate.Time)} to {HubScheduleHints.FormatDegreesC(candidate.DegreesC)}.";
        }

        return "No upcoming setpoint found.";
    }

    private readonly record struct SetPoint(int Time, int DegreesC);

    private sealed class ScheduleDays(Dictionary<string, List<SetPoint>> days)
    {
        public List<SetPoint> OrderedPoints(string dayName)
        {
            if (!days.TryGetValue(dayName, out var pts))
                return [];
            return pts
                .OrderBy(p => HubScheduleHints.NormalizeSeconds(p.Time))
                .ToList();
        }
    }

    private sealed record ParsedRoom(int Id, string Name, int ScheduleId);

    private static JsonElement? TryGetObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            root.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out el);
        return el.ValueKind == JsonValueKind.Object ? el : null;
    }

    private static bool TryGetArray(JsonElement root, string name, out JsonElement arr)
    {
        if (!root.TryGetProperty(name, out arr))
            root.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out arr);
        return arr.ValueKind == JsonValueKind.Array;
    }

    private static int GetInt(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
                return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i))
                return i;
        }

        return -1;
    }

    private static int GetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
            return int.MinValue;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i))
            return i;
        return int.MinValue;
    }

    private static string GetString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.String)
                continue;
            var s = v.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s!;
        }

        return "";
    }
}
