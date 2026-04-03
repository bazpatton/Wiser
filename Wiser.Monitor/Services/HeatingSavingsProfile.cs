namespace Wiser.Monitor.Services;

public sealed record HeatingSavingsProfile(
    bool WeekdayDaytimeAway,
    int WeekdayAwayStartMin,
    int WeekdayAwayEndMin,
    bool WeekendDaytimeAway,
    int WeekendAwayStartMin,
    int WeekendAwayEndMin,
    bool NightSetbackOk,
    IReadOnlyList<string> PriorityRooms,
    long UpdatedTs)
{
    public static HeatingSavingsProfile Default =>
        new(
            WeekdayDaytimeAway: false,
            WeekdayAwayStartMin: 9 * 60,
            WeekdayAwayEndMin: 17 * 60,
            WeekendDaytimeAway: false,
            WeekendAwayStartMin: 10 * 60,
            WeekendAwayEndMin: 16 * 60,
            NightSetbackOk: true,
            PriorityRooms: [],
            UpdatedTs: 0);
}
