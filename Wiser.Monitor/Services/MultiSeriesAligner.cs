namespace Wiser.Monitor.Services;

/// <summary>A named line for a shared-time-axis chart.</summary>
public sealed record AlignedSeries(string Name, IReadOnlyList<ChartPoint> Points);

/// <summary>
/// Puts several rooms' samples on one shared, downsampled time axis so a chart's shared tooltip
/// can list every room at the hovered time.
/// </summary>
public static class MultiSeriesAligner
{
    public enum Aggregate { Average, Max }

    /// <param name="series">Per series: (unix seconds, value) samples, any order.</param>
    /// <param name="holdBetweenSamples">Repeat a series' last value until its next sample, within its own first..last range.</param>
    public static List<AlignedSeries> Align(
        IReadOnlyList<(string Name, IReadOnlyList<(long Ts, double Value)> Samples)> series,
        TimeZoneInfo zone,
        Aggregate aggregate,
        int decimals,
        bool holdBetweenSamples = true,
        int maxPoints = RoomHistoryChartBuilder.DefaultMaxPoints)
    {
        var axis = series
            .SelectMany(static s => s.Samples.Select(static x => x.Ts))
            .Distinct()
            .Order()
            .ToArray();
        if (axis.Length == 0)
            return [];

        var bucket = Math.Max(1, (int)Math.Ceiling(axis.Length / (double)Math.Max(1, maxPoints)));
        var bucketCount = (axis.Length + bucket - 1) / bucket;
        var bucketX = new long[bucketCount];
        for (var b = 0; b < bucketCount; b++)
            bucketX[b] = RoomHistoryChartBuilder.ToWallClockMs(axis[b * bucket], zone);

        var result = new List<AlignedSeries>(series.Count);
        foreach (var (name, samples) in series)
        {
            var byTs = new Dictionary<long, double>();
            foreach (var (ts, v) in samples)
            {
                if (double.IsFinite(v))
                    byTs[ts] = v; // last sample wins for duplicate timestamps
            }

            var points = new ChartPoint[bucketCount];
            if (byTs.Count == 0)
            {
                for (var b = 0; b < bucketCount; b++)
                    points[b] = new ChartPoint(bucketX[b], null);
                result.Add(new AlignedSeries(name, points));
                continue;
            }

            var first = byTs.Keys.Min();
            var last = byTs.Keys.Max();
            double? held = null;
            for (var b = 0; b < bucketCount; b++)
            {
                double sum = 0;
                double? max = null;
                var count = 0;
                var start = b * bucket;
                var end = Math.Min(axis.Length, start + bucket);
                for (var i = start; i < end; i++)
                {
                    if (!byTs.TryGetValue(axis[i], out var v))
                        continue;
                    sum += v;
                    count++;
                    if (max is null || v > max)
                        max = v;
                }

                double? value = count == 0
                    ? null
                    : aggregate == Aggregate.Max ? max : sum / count;

                if (value is not null)
                    held = value;
                else if (holdBetweenSamples && held is not null && axis[start] > first && axis[start] <= last)
                    value = held;

                points[b] = new ChartPoint(
                    bucketX[b],
                    value is { } d ? Math.Round((decimal)d, decimals) : null);
            }

            result.Add(new AlignedSeries(name, points));
        }

        return result;
    }
}
