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

        var maxLabels = hoursWindow switch
        {
            <= 12 => 18,
            <= 24 => 15,
            <= 48 => 12,
            <= 96 => 11,
            _ => 10,
        };
        if (compactAxes)
            maxLabels = Math.Max(5, maxLabels * 2 / 3);
        maxLabels = Math.Clamp(maxLabels, compactAxes ? 5 : 8, 20);
        var step = Math.Max(1, (n + maxLabels - 1) / maxLabels);

        var format = hoursWindow >= 168
            ? "MMM d"
            : hoursWindow >= 72
                ? "ddd d HH:mm"
                : hoursWindow > 24
                    ? "M/d HH:mm"
                    : "HH:mm";

        var labels = new string[n];
        for (var i = 0; i < n; i++)
        {
            var show = i == 0 || i == n - 1 || i % step == 0;
            labels[i] = show
                ? TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestampsUtc[i]), zone).ToString(format, CultureInfo.CurrentCulture)
                : string.Empty;
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
