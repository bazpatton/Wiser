using System.Globalization;

namespace Wiser.Monitor.Services;

public sealed record HeatingSavingsSuggestionContext(
    HeatingSavingsProfile Profile,
    IReadOnlyList<DailySummaryRow> DailyRows,
    bool OutdoorConfigured,
    double? PastWeekHddSum,
    double? ForecastWeekHddSum,
    HubScheduleSavingsHints? HubHints);

public static class HeatingSavingsSuggestionBuilder
{
    public static IReadOnlyList<HeatingSavingsSuggestion> Build(HeatingSavingsSuggestionContext context)
    {
        var list = new List<HeatingSavingsSuggestion>();

        if (!context.OutdoorConfigured)
        {
            list.Add(new HeatingSavingsSuggestion(
                "Enable weather-aware scheduling",
                "Set Open-Meteo latitude/longitude so Home can compare last week to the forecast and suggest when to lower setpoints.",
                "/settings",
                "Open settings",
                HeatingSavingsSuggestionLevel.Info));
        }

        var (previous7, last7) = SplitWeeks(context.DailyRows);
        var prevHeat = previous7.Sum(d => d.HeatingActiveEstimateMin);
        var lastHeat = last7.Sum(d => d.HeatingActiveEstimateMin);

        if (previous7.Count >= 5 && last7.Count >= 5 && prevHeat > 0)
        {
            var change = (lastHeat - prevHeat) / prevHeat;
            if (change > 0.18)
            {
                list.Add(new HeatingSavingsSuggestion(
                    "Heating runtime rose versus the previous week",
                    $"Estimated heating-active time is up by {(change * 100):0}% week-on-week. Review schedule blocks to trim non-essential comfort periods.",
                    "/schedules",
                    "Review schedules",
                    HeatingSavingsSuggestionLevel.Warning));
            }
        }

        if (context.PastWeekHddSum is { } pastHdd && context.ForecastWeekHddSum is { } forecastHdd)
        {
            const double band = 0.15;
            if (forecastHdd > pastHdd * (1 + band))
            {
                var body = context.Profile.NightSetbackOk
                    ? "Forecast days look colder than last week. A small overnight setback and tighter daytime comfort blocks can reduce gas use."
                    : "Forecast days look colder than last week. Focus savings on daytime away periods and less-used rooms rather than overnight setback.";
                list.Add(new HeatingSavingsSuggestion(
                    "Colder week ahead",
                    body,
                    "/schedules",
                    "Tune schedule setpoints",
                    HeatingSavingsSuggestionLevel.Warning));
            }
            else if (forecastHdd < pastHdd * (1 - band))
            {
                list.Add(new HeatingSavingsSuggestion(
                    "Milder week ahead",
                    "Use milder weather to step down daytime setpoints by 0.5–1.0 C in rooms that overheat.",
                    "/schedules",
                    "Adjust schedule",
                    HeatingSavingsSuggestionLevel.Info));
            }
        }

        if (context.Profile.WeekdayDaytimeAway)
        {
            list.Add(new HeatingSavingsSuggestion(
                "Weekday away window can save energy",
                $"Set lower weekday setpoints between {FormatWindow(context.Profile.WeekdayAwayStartMin, context.Profile.WeekdayAwayEndMin)} in non-priority rooms.",
                "/schedules",
                "Edit weekday blocks",
                HeatingSavingsSuggestionLevel.Info));
        }

        if (context.Profile.WeekendDaytimeAway)
        {
            list.Add(new HeatingSavingsSuggestion(
                "Weekend away window can save energy",
                $"Set lower weekend setpoints between {FormatWindow(context.Profile.WeekendAwayStartMin, context.Profile.WeekendAwayEndMin)} where comfort is not needed.",
                "/schedules",
                "Edit weekend blocks",
                HeatingSavingsSuggestionLevel.Info));
        }

        if (context.Profile.PriorityRooms.Count > 0)
        {
            var rooms = string.Join(", ", context.Profile.PriorityRooms.Take(3));
            var suffix = context.Profile.PriorityRooms.Count > 3 ? ", ..." : "";
            list.Add(new HeatingSavingsSuggestion(
                "Concentrate comfort where it matters",
                $"Prioritize comfort in {rooms}{suffix}. Reduce target temperatures slightly in other rooms during overlapping hours.",
                "/schedules",
                "Open schedules",
                HeatingSavingsSuggestionLevel.Info));
        }

        if (context.HubHints is { RoomsWithoutUserProgram: > 0 } hints)
        {
            list.Add(new HeatingSavingsSuggestion(
                "Some rooms are not on a user schedule",
                $"{hints.RoomsWithoutUserProgram} room(s) are not linked to a user program. Putting them on schedules can prevent avoidable heating.",
                "/schedules",
                "View schedule mapping",
                HeatingSavingsSuggestionLevel.Warning));
        }

        if (context.HubHints is { MaxUserProgramSetpointTenths: >= 220 })
        {
            list.Add(new HeatingSavingsSuggestion(
                "High setpoint found in schedule program",
                "At least one user schedule block reaches 22 C or higher. Lowering these peaks in less-used rooms can cut costs.",
                "/schedules",
                "Inspect high setpoints",
                HeatingSavingsSuggestionLevel.Warning));
        }

        if (list.Count == 0)
        {
            list.Add(new HeatingSavingsSuggestion(
                "No obvious savings flags",
                "Your recent pattern looks steady. For extra savings, reduce daytime setpoints by 0.5 C in non-priority rooms and review results after a week.",
                "/schedules",
                "Review schedules",
                HeatingSavingsSuggestionLevel.Info));
        }

        return list.Take(6).ToList();
    }

    private static (IReadOnlyList<DailySummaryRow> Previous7, IReadOnlyList<DailySummaryRow> Last7) SplitWeeks(IReadOnlyList<DailySummaryRow> rows)
    {
        if (rows.Count == 0)
            return ([], []);

        var last7 = rows.TakeLast(7).ToList();
        var previous7 = rows.Take(Math.Max(0, rows.Count - last7.Count)).TakeLast(7).ToList();
        return (previous7, last7);
    }

    private static string FormatWindow(int startMin, int endMin)
    {
        var start = TimeSpan.FromMinutes(Math.Clamp(startMin, 0, 1439));
        var end = TimeSpan.FromMinutes(Math.Clamp(endMin, 0, 1439));
        return $"{start.Hours:D2}:{start.Minutes:D2}-{end.Hours:D2}:{end.Minutes:D2}";
    }
}
