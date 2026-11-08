using Wiser.Monitor.Services;
using Xunit;

namespace Wiser.Monitor.Tests;

public class RoomHistoryChartBuilderTests
{
    private static RoomSeriesRow Row(long ts, double temp, double? sp, int? valve = 0) =>
        new(ts, temp, sp, null, null, valve > 0 ? 1 : 0, valve);

    [Fact]
    public void Empty_rows_return_null() =>
        Assert.Null(RoomHistoryChartBuilder.Build([], null, TimeZoneInfo.Utc));

    [Fact]
    public void Off_setpoint_becomes_gap_and_shaded_range_not_zero()
    {
        var rows = new[]
        {
            Row(0, 20, 21),
            Row(60, 20, -20),
            Row(120, 20, -20),
            Row(180, 20, 18),
        };

        var r = RoomHistoryChartBuilder.Build(rows, null, TimeZoneInfo.Utc)!;

        Assert.Equal([21m, null, null, 18m], r.Target.Select(p => p.Y));
        var off = Assert.Single(r.OffRanges);
        Assert.Equal(60_000, off.Start);
        Assert.Equal(180_000, off.End);
        Assert.True(r.YMin > 0, "off must not drag the axis down to 0 °C");
    }

    [Fact]
    public void Missing_setpoint_is_forward_filled()
    {
        var rows = new[] { Row(0, 20, 21), Row(60, 20, null) };
        var r = RoomHistoryChartBuilder.Build(rows, null, TimeZoneInfo.Utc)!;
        Assert.Equal([21m, 21m], r.Target.Select(p => p.Y));
    }

    [Fact]
    public void Outdoor_is_interpolated_between_readings()
    {
        var rows = new[] { Row(0, 20, 21), Row(450, 20, 21), Row(900, 20, 21) };
        var outdoor = new[] { new OutdoorSeriesRow(0, 4), new OutdoorSeriesRow(900, 6) };

        var r = RoomHistoryChartBuilder.Build(rows, outdoor, TimeZoneInfo.Utc)!;

        Assert.Equal([4m, 5m, 6m], r.Outdoor.Select(p => p.Y));
    }

    [Fact]
    public void Long_windows_are_downsampled_keeping_valve_peaks()
    {
        var rows = Enumerable.Range(0, 1000)
            .Select(i => Row(i * 60, 20, 21, i == 503 ? 87 : 0))
            .ToArray();

        var r = RoomHistoryChartBuilder.Build(rows, null, TimeZoneInfo.Utc, maxPoints: 100)!;

        Assert.Equal(100, r.Room.Count);
        Assert.Contains(r.Valve, p => p.Y == 87m);
    }

    [Fact]
    public void X_values_are_display_zone_wall_clock()
    {
        var london = TimeZoneResolver.Resolve("Europe/London");
        var julyNoonUtc = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var ms = RoomHistoryChartBuilder.ToWallClockMs(julyNoonUtc, london);

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), ms);
    }

    [Theory]
    [InlineData(19.4, 21.0, 18, 22, 1)]   // narrow span → 1 °C ticks
    [InlineData(4.0, 21.0, 0, 25, 5)]     // with outdoor → 5 °C ticks
    [InlineData(12.0, 22.0, 10, 24, 2)]
    public void Temperature_range_snaps_to_even_ticks(double lo, double hi, int min, int max, int step)
    {
        var pts = new[] { new ChartPoint(0, (decimal)lo), new ChartPoint(1, (decimal)hi) };

        var (yMin, yMax, yStep) = RoomHistoryChartBuilder.TemperatureRange(pts);

        Assert.Equal((decimal)min, yMin);
        Assert.Equal((decimal)max, yMax);
        Assert.Equal((decimal)step, yStep);
    }
}
