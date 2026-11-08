using Wiser.Monitor.Services;
using Xunit;

namespace Wiser.Monitor.Tests;

public class MultiSeriesAlignerTests
{
    private static (string, IReadOnlyList<(long Ts, double Value)>) S(string name, params (long, double)[] samples) =>
        (name, samples);

    [Fact]
    public void Series_share_one_time_axis()
    {
        var result = MultiSeriesAligner.Align(
            [S("A", (0, 20), (60, 21)), S("B", (30, 18))],
            TimeZoneInfo.Utc,
            MultiSeriesAligner.Aggregate.Average,
            decimals: 1);

        Assert.Equal(2, result.Count);
        Assert.Equal([0L, 30_000L, 60_000L], result[0].Points.Select(p => p.X));
        Assert.Equal(result[0].Points.Select(p => p.X), result[1].Points.Select(p => p.X));
    }

    [Fact]
    public void Holds_value_between_samples_but_not_outside_series_range()
    {
        var result = MultiSeriesAligner.Align(
            [S("A", (0, 20), (120, 22)), S("B", (60, 18), (90, 19))],
            TimeZoneInfo.Utc,
            MultiSeriesAligner.Aggregate.Average,
            decimals: 1);

        // Axis: 0, 60, 90, 120
        Assert.Equal([20m, 20m, 20m, 22m], result[0].Points.Select(p => p.Y));
        Assert.Equal([null, 18m, 19m, null], result[1].Points.Select(p => p.Y));
    }

    [Fact]
    public void Max_aggregate_keeps_peaks_when_downsampling()
    {
        var samples = Enumerable.Range(0, 100).Select(i => ((long)i * 60, i == 57 ? 80.0 : 0.0)).ToArray();

        var result = MultiSeriesAligner.Align(
            [("Valve", samples)],
            TimeZoneInfo.Utc,
            MultiSeriesAligner.Aggregate.Max,
            decimals: 0,
            maxPoints: 10);

        Assert.Equal(10, result[0].Points.Count);
        Assert.Contains(result[0].Points, p => p.Y == 80m);
    }

    [Fact]
    public void Series_without_samples_is_all_gaps()
    {
        var result = MultiSeriesAligner.Align(
            [S("A", (0, 20)), S("Empty")],
            TimeZoneInfo.Utc,
            MultiSeriesAligner.Aggregate.Average,
            decimals: 1);

        Assert.All(result[1].Points, p => Assert.Null(p.Y));
    }

    [Fact]
    public void No_samples_returns_empty() =>
        Assert.Empty(MultiSeriesAligner.Align([S("A")], TimeZoneInfo.Utc, MultiSeriesAligner.Aggregate.Average, 1));
}
