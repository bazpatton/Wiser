namespace Wiser.Monitor.Services;

public sealed record BoilerCycleSummary(
    int TotalCycles,
    int ShortCycles,
    double ShortCycleRatio,
    string ShortCycleRatioText,
    double ShortCycleThresholdMin,
    double ShortestCycleMinutes,
    double MedianCycleMinutes);

public static class BoilerCycleAnalyzer
{
    public static BoilerCycleSummary? Summarize(IReadOnlyList<SystemSeriesRow> rows, double shortCycleThresholdMinutes)
    {
        if (rows.Count < 2)
            return null;

        var durationsMin = new List<double>();
        long? cycleStartTs = null;
        var previousOn = rows[0].HeatingRelayOn != 0;
        if (previousOn)
            cycleStartTs = rows[0].Ts;

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var isOn = row.HeatingRelayOn != 0;
            if (!previousOn && isOn)
            {
                cycleStartTs = row.Ts;
            }
            else if (previousOn && !isOn && cycleStartTs is { } start)
            {
                var durationMin = Math.Max(0, (row.Ts - start) / 60.0);
                if (durationMin > 0)
                    durationsMin.Add(durationMin);
                cycleStartTs = null;
            }

            previousOn = isOn;
        }

        if (durationsMin.Count == 0)
            return null;

        durationsMin.Sort();
        var shortCycles = durationsMin.Count(x => x < shortCycleThresholdMinutes);
        var ratio = shortCycles / (double)durationsMin.Count;
        var median = durationsMin.Count % 2 == 1
            ? durationsMin[durationsMin.Count / 2]
            : (durationsMin[(durationsMin.Count / 2) - 1] + durationsMin[durationsMin.Count / 2]) / 2.0;

        return new BoilerCycleSummary(
            durationsMin.Count,
            shortCycles,
            ratio,
            $"{ratio * 100:0.#}%",
            shortCycleThresholdMinutes,
            durationsMin[0],
            median);
    }
}
