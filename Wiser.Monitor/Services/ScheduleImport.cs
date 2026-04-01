using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

public sealed record ScheduleApplyResult(
    bool Succeeded,
    bool DryRun,
    IReadOnlyList<int> AppliedScheduleIds,
    IReadOnlyList<SchedulePatchSummary>? Summaries,
    int? FailedScheduleId,
    string? ErrorMessage);

public sealed record SchedulePatchSummary(
    [property: JsonPropertyName("schedule_id")] int ScheduleId,
    [property: JsonPropertyName("patch_body_bytes")] int PatchBodyBytes);

public static class ScheduleImport
{
    /// <summary>
    /// Collects schedule objects from: root array, <c>schedules</c> property (export file), or a single schedule object with <c>id</c>.
    /// </summary>
    public static IReadOnlyList<JsonElement> EnumerateSchedulesToApply(JsonElement root)
    {
        var list = new List<JsonElement>();
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var el in root.EnumerateArray())
                    list.Add(el);
                return list;
            case JsonValueKind.Object:
                if (root.TryGetProperty("schedules", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                        list.Add(el);
                    return list;
                }

                if (GetScheduleId(root) > 0)
                {
                    list.Add(root);
                    return list;
                }

                return list;
            default:
                return list;
        }
    }

    public static int GetScheduleId(JsonElement sched)
    {
        if (sched.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var i))
            return i;
        if (sched.TryGetProperty("Id", out id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out i))
            return i;
        return -1;
    }
}
