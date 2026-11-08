namespace Wiser.Monitor.Services;

/// <summary>One plotted point. <see cref="X"/> is the display-zone wall-clock time as Unix ms (rendered with datetimeUTC so labels show local time).</summary>
public sealed record ChartPoint(long X, decimal? Y);

/// <summary>A span of time (Unix ms, display-zone wall clock) where the room's heating was switched off.</summary>
public sealed record ChartRange(long Start, long End);

/// <summary>
/// Builds time-axis series for the single-room history charts (ApexCharts on Rooms → room).
/// "Off" is not drawn as 0 °C: the target line has a gap
/// and the span is returned in <see cref="Result.OffRanges"/> so the chart can shade it.
/// </summary>
public static class RoomHistoryChartBuilder
{
    public const int DefaultMaxPoints = 720;

    public sealed record Result(
        IReadOnlyList<ChartPoint> Room,
        IReadOnlyList<ChartPoint> Target,
        IReadOnlyList<ChartPoint> Outdoor,
        IReadOnlyList<ChartPoint> Valve,
        IReadOnlyList<ChartRange> OffRanges,
        decimal YMin,
        decimal YMax,
        decimal YStep)
    {
        public int YTickAmount => (int)((YMax - YMin) / YStep);

        public bool HasTarget => Target.Any(static p => p.Y is not null);
        public bool HasOutdoor => Outdoor.Any(static p => p.Y is not null);
        public bool HasValve => Valve.Any(static p => p.Y is not null);
    }

    private enum TargetKind { Unknown, Off, On }

    public static Result? Build(
        IReadOnlyList<RoomSeriesRow> rows,
        IReadOnlyList<OutdoorSeriesRow>? outdoor,
        TimeZoneInfo zone,
        int maxPoints = DefaultMaxPoints)
    {
        if (rows.Count == 0)
            return null;

        var n = rows.Count;
        var xs = new long[n];
        for (var i = 0; i < n; i++)
            xs[i] = ToWallClockMs(rows[i].Ts, zone);

        var room = rows.Select(static r => r.TempC).ToArray();
        SmoothInPlace(room);

        // Target: forward-fill missing samples; hub "off" sentinel becomes a gap, not 0 °C.
        var target = new double?[n];
        var kind = new TargetKind[n];
        double? lastSp = null;
        for (var i = 0; i < n; i++)
        {
            var r = rows[i];
            var sp = r.SetpointC ?? r.CurrentSetpointC ?? r.ScheduledSetpointC ?? lastSp;
            if (sp is { } v)
            {
                lastSp = v;
                if (WiserSetpointDisplay.IsHubOffSentinel(v))
                    kind[i] = TargetKind.Off;
                else
                {
                    kind[i] = TargetKind.On;
                    target[i] = v;
                }
            }
        }

        var outdoorAligned = outdoor is { Count: > 0 }
            ? AlignOutdoor(rows, outdoor)
            : new double?[n];
        var valve = rows.Select(static r => r.PercentageDemand is { } p ? (double?)p : null).ToArray();

        // Downsample into buckets so long windows stay light over the SignalR circuit.
        var bucket = Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, maxPoints)));
        var roomPts = new List<ChartPoint>();
        var targetPts = new List<ChartPoint>();
        var outdoorPts = new List<ChartPoint>();
        var valvePts = new List<ChartPoint>();
        var bucketKinds = new List<TargetKind>();
        var bucketXs = new List<long>();

        for (var start = 0; start < n; start += bucket)
        {
            var end = Math.Min(n, start + bucket);
            var x = xs[start];
            bucketXs.Add(x);
            roomPts.Add(new ChartPoint(x, Round(Average(room, start, end), 2)));
            outdoorPts.Add(new ChartPoint(x, Round(Average(outdoorAligned, start, end), 2)));
            valvePts.Add(new ChartPoint(x, Round(Max(valve, start, end), 0)));
            // Step line: the bucket takes the target that was active at its start.
            targetPts.Add(new ChartPoint(x, Round(target[start], 1)));
            bucketKinds.Add(kind[start]);
        }

        var offRanges = new List<ChartRange>();
        for (var i = 0; i < bucketKinds.Count; i++)
        {
            if (bucketKinds[i] != TargetKind.Off)
                continue;
            var j = i;
            while (j + 1 < bucketKinds.Count && bucketKinds[j + 1] == TargetKind.Off)
                j++;
            var endX = j + 1 < bucketXs.Count ? bucketXs[j + 1] : xs[n - 1];
            offRanges.Add(new ChartRange(bucketXs[i], Math.Max(endX, bucketXs[i])));
            i = j;
        }

        var (yMin, yMax, yStep) = TemperatureRange(roomPts, targetPts, outdoorPts);
        return new Result(roomPts, targetPts, outdoorPts, valvePts, offRanges, yMin, yMax, yStep);
    }

    /// <summary>Unix seconds → Unix ms of the display-zone wall clock (so a UTC-rendering chart shows local time).</summary>
    public static long ToWallClockMs(long unixSeconds, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds), zone);
        return new DateTimeOffset(local.DateTime, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Y-axis bounds around every plotted temperature, padded by a degree and snapped to a tick step
    /// (1, 2 or 5 °C) so the axis reads in even whole-degree increments.
    /// </summary>
    public static (decimal Min, decimal Max, decimal Step) TemperatureRange(params IEnumerable<ChartPoint>[] series)
    {
        var values = series.SelectMany(static s => s).Where(static p => p.Y is not null).Select(static p => p.Y!.Value).ToList();
        if (values.Count == 0)
            return (0, 25, 5);
        var lo = values.Min() - 1;
        var hi = values.Max() + 1;
        var span = hi - lo;
        decimal step = span <= 8 ? 1 : span <= 16 ? 2 : 5;
        var min = Math.Floor(lo / step) * step;
        var max = Math.Ceiling(hi / step) * step;
        if (max - min < 4)
            max = min + 4; // only reachable with a 1 °C step

        return (min, max, step);
    }

    /// <summary>Outdoor readings are sparse (every ~15 min); interpolate so the line doesn't stair-step.</summary>
    /// <summary>Centred moving average that softens poll-to-poll sensor jitter (0.1 °C steps).</summary>
    public static void SmoothInPlace(double[] values, int window = 5)
    {
        if (values.Length < 3)
            return;
        var src = (double[])values.Clone();
        var half = window / 2;
        for (var i = 0; i < values.Length; i++)
        {
            var lo = Math.Max(0, i - half);
            var hi = Math.Min(values.Length - 1, i + half);
            double sum = 0;
            var c = 0;
            for (var j = lo; j <= hi; j++)
            {
                if (double.IsFinite(src[j]))
                {
                    sum += src[j];
                    c++;
                }
            }
            if (c > 0)
                values[i] = sum / c;
        }
    }

    private static double?[] AlignOutdoor(IReadOnlyList<RoomSeriesRow> rows, IReadOnlyList<OutdoorSeriesRow> outdoor)
    {
        var sorted = outdoor.OrderBy(static x => x.Ts).ToList();
        var result = new double?[rows.Count];
        var j = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var ts = rows[i].Ts;
            while (j + 1 < sorted.Count && sorted[j + 1].Ts <= ts)
                j++;
            var a = sorted[j];
            if (a.Ts > ts)
                continue; // before the first outdoor reading
            if (j + 1 < sorted.Count)
            {
                var b = sorted[j + 1];
                var f = (ts - a.Ts) / (double)(b.Ts - a.Ts);
                result[i] = a.TempC + ((b.TempC - a.TempC) * f);
            }
            else
            {
                result[i] = a.TempC;
            }
        }
        return result;
    }

    private static double? Average(double[] values, int start, int end)
    {
        double sum = 0;
        var c = 0;
        for (var i = start; i < end; i++)
        {
            if (double.IsFinite(values[i]))
            {
                sum += values[i];
                c++;
            }
        }
        return c > 0 ? sum / c : null;
    }

    private static double? Average(double?[] values, int start, int end)
    {
        double sum = 0;
        var c = 0;
        for (var i = start; i < end; i++)
        {
            if (values[i] is { } v && double.IsFinite(v))
            {
                sum += v;
                c++;
            }
        }
        return c > 0 ? sum / c : null;
    }

    private static double? Max(double?[] values, int start, int end)
    {
        double? best = null;
        for (var i = start; i < end; i++)
        {
            if (values[i] is { } v && (best is null || v > best))
                best = v;
        }
        return best;
    }

    private static decimal? Round(double? v, int decimals) =>
        v is { } d && double.IsFinite(d) ? Math.Round((decimal)d, decimals) : null;
}
