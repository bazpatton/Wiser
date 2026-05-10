using System.Globalization;
using MudBlazor;

namespace Wiser.Monitor.Services;

/// <summary>Builds the same room temperature / target / outdoor / valve series as Charts → Selected room trend, for one room only.</summary>
public static class RoomTrendChartBuilder
{
    public const string SeriesRoom = "Room °C";
    public const string SeriesTarget = "Target °C";
    public const string SeriesOutdoor = "Outdoor °C";
    public const string SeriesValve = "Valve demand %";

    public sealed record Result(
        long[] PointTs,
        string[] ChartLabels,
        List<ChartSeries<double>> TempSeries,
        List<ChartSeries<double>> ValveSeries,
        bool HasValveSeries);

    public static Result? Build(
        TemperatureStore store,
        MonitorOptions options,
        string roomName,
        int hours,
        TimeZoneInfo zone,
        bool includeOutdoor,
        bool compactAxes)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return null;

        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (hours * 3600L);
        var roomRows = store.SeriesRoom(roomName.Trim(), since);
        if (roomRows.Count == 0)
            return null;

        var pointTs = roomRows.Select(static x => x.Ts).ToArray();
        var labels = BuildSparseTimeLabels(pointTs, hours, zone, compactAxes);

        var targetChartValues = roomRows.Select(ChartTargetTempC).ToArray();
        ForwardFillTargetLine(targetChartValues);

        var roomTempsSmooth = roomRows.Select(static x => x.TempC).ToArray();
        SmoothRoomTemperatureSeriesInPlace(roomTempsSmooth);

        var tempSeries = new List<ChartSeries<double>>
        {
            new() { Name = SeriesRoom, Data = roomTempsSmooth, Visible = true },
        };
        if (targetChartValues.Any(static v => !double.IsNaN(v)))
        {
            tempSeries.Add(new ChartSeries<double>
            {
                Name = SeriesTarget,
                Data = targetChartValues,
                Visible = true,
            });
        }

        if (includeOutdoor && options.OpenMeteoLat is not null)
        {
            var alignedOutdoor = AlignOutdoor(
                roomRows.Select(static x => x.Ts).ToArray(),
                store.SeriesOutdoor(since));
            if (alignedOutdoor.Any(static x => x is not null))
            {
                tempSeries.Add(new ChartSeries<double>
                {
                    Name = SeriesOutdoor,
                    Data = alignedOutdoor.Select(static x => x ?? double.NaN).ToArray(),
                    Visible = true,
                });
            }
        }

        List<ChartSeries<double>> valveSeries = [];
        var hasValve = roomRows.Any(x => x.PercentageDemand.HasValue);
        if (hasValve)
        {
            valveSeries =
            [
                new ChartSeries<double>
                {
                    Name = SeriesValve,
                    Data = roomRows.Select(x => x.PercentageDemand is null ? double.NaN : x.PercentageDemand.Value).ToArray(),
                    Visible = true,
                },
            ];
        }

        return new Result(pointTs, labels, tempSeries, valveSeries, hasValve);
    }

    public static string[] BuildSparseTimeLabels(
        IReadOnlyList<long> timestampsUtc,
        int hoursWindow,
        TimeZoneInfo zone,
        bool compactAxes = false)
    {
        var n = timestampsUtc.Count;
        if (n == 0)
            return [];

        // Convert to local wall-clock times once.
        var localTimes = timestampsUtc
            .Select(ts => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), zone).DateTime)
            .ToArray();

        // Choose a snapping interval so labels land on clean hour/half-hour boundaries.
        var intervalMinutes = hoursWindow switch
        {
            <= 6 => 30,
            <= 12 => 60,
            <= 24 => 120,
            <= 48 => 240,
            <= 96 => 480,
            _ => 1440,
        };
        if (compactAxes)
            intervalMinutes *= 2;

        var format = hoursWindow >= 168
            ? "MMM d"
            : hoursWindow >= 72
                ? "ddd HH:mm"
                : hoursWindow > 24
                    ? "M/d HH:mm"
                    : "HH:mm";

        var interval = TimeSpan.FromMinutes(intervalMinutes);
        var halfInterval = TimeSpan.FromMinutes(intervalMinutes / 2.0);

        // Generate candidate nice times from midnight of the first day.
        var start = localTimes[0].Date;
        var end = localTimes[n - 1] + interval;
        var niceTimes = new List<DateTime>();
        for (var t = start; t <= end; t += interval)
            niceTimes.Add(t);

        // Assign each nice time to the nearest data point within half the interval.
        var labels = new string[n];
        var assigned = new HashSet<int>();
        foreach (var niceTime in niceTimes)
        {
            var bestIdx = -1;
            var bestDiff = halfInterval;
            for (var i = 0; i < n; i++)
            {
                if (assigned.Contains(i))
                    continue;
                var diff = (localTimes[i] - niceTime).Duration();
                if (diff <= bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
            if (bestIdx >= 0)
            {
                assigned.Add(bestIdx);
                // Label shows the rounded nice time, not the raw poll timestamp.
                labels[bestIdx] = niceTime.ToString(format, CultureInfo.CurrentCulture);
            }
        }

        return labels;
    }

    public static void SmoothRoomTemperatureSeriesInPlace(double[] values, int window = 5)
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
                var v = src[j];
                if (double.IsFinite(v))
                {
                    sum += v;
                    c++;
                }
            }
            if (c > 0)
                values[i] = sum / c;
        }
    }

    private static double ChartTargetTempC(RoomSeriesRow x)
    {
        var sp = x.SetpointC ?? x.CurrentSetpointC ?? x.ScheduledSetpointC;
        return sp is { } v ? WiserSetpointDisplay.ToDisplayTargetC(v) : double.NaN;
    }

    private static void ForwardFillTargetLine(double[] values)
    {
        double? last = null;
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (!double.IsNaN(v) && !double.IsInfinity(v))
                last = v;
            else if (last is { } lv)
                values[i] = lv;
        }
    }

    private static double?[] AlignOutdoor(long[] timestamps, IReadOnlyList<OutdoorSeriesRow> outdoor)
    {
        if (outdoor.Count == 0)
            return new double?[timestamps.Length];

        var sorted = outdoor.OrderBy(static x => x.Ts).ToList();
        var result = new double?[timestamps.Length];
        var j = 0;
        double? last = null;
        for (var i = 0; i < timestamps.Length; i++)
        {
            var ts = timestamps[i];
            while (j < sorted.Count && sorted[j].Ts <= ts)
            {
                last = sorted[j].TempC;
                j++;
            }

            result[i] = last;
        }

        return result;
    }
}
