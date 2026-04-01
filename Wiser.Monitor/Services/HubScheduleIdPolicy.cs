namespace Wiser.Monitor.Services;

/// <summary>
/// The hub lists heating programs with low ids; higher ids are internal/system schedules that should not appear in user exports or the schedules UI.
/// </summary>
public static class HubScheduleIdPolicy
{
    public const int FirstInternalScheduleId = 100;

    /// <summary>User-facing program ids: below <see cref="FirstInternalScheduleId"/>.</summary>
    public static bool IsUserProgramScheduleId(int scheduleId) =>
        scheduleId >= 0 && scheduleId < FirstInternalScheduleId;
}
