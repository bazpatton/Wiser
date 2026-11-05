namespace Wiser.Monitor.Tests;

public class OverrideOutcomeTests
{
    private const long Start = 1_800_000_000;

    private static OverrideLogEntry Entry(double setpoint = 20, double? previous = 21, long minutes = 60) =>
        new(1, 7, "Living room", Start, Start + minutes * 60, setpoint, previous, OverrideSources.HeatingNow);

    private static RoomSeriesRow Row(long offsetMin, double temp, bool calling) =>
        new(Start + offsetMin * 60, temp, 21, 21, 21, calling ? 1 : 0, calling ? 40 : 0);

    [Fact]
    public void Ease_off_detection_and_labels()
    {
        Assert.True(Entry(20, 21).IsEaseOff);
        Assert.Equal("−1°", Entry(20, 21).ChangeLabel);
        Assert.True(Entry(WiserSetpointDisplay.HubOffSentinelC, 21).IsEaseOff);
        Assert.Equal("Off", Entry(WiserSetpointDisplay.HubOffSentinelC, null).ChangeLabel);
        Assert.False(Entry(22, 21).IsEaseOff);
        Assert.Equal("22°", Entry(22, 21).ChangeLabel);
        Assert.False(Entry(20, null).IsEaseOff); // unknown previous target: can't tell
    }

    [Fact]
    public void Analyze_measures_temps_and_calling_share_before_and_during()
    {
        var samples = new[]
        {
            Row(-50, 20.4, true), Row(-30, 20.5, true), Row(-10, 20.6, true), // before: all calling
            Row(0, 20.6, true), Row(20, 20.5, false), Row(40, 20.3, false), Row(60, 20.2, false),
            Row(80, 20.9, true), // after the end: ignored
        };

        var o = OverrideOutcomeAnalyzer.Analyze(Entry(), samples, nowTs: Start + 3 * 3600);

        Assert.False(o.Running);
        Assert.Equal(60, o.DurationMinutes);
        Assert.Equal(20.6, o.TempStartC);
        Assert.Equal(20.2, o.TempEndC);
        Assert.Equal(20.2, o.TempMinC);
        Assert.Equal(0.25, o.CallingShareDuring);
        Assert.Equal(1.0, o.CallingShareBefore);
        Assert.Equal(4, o.SamplesDuring);
        Assert.Equal(-0.4, o.TempChangeC!.Value, 6);
    }

    [Fact]
    public void Analyze_running_override_stops_at_now_and_skips_offline_samples()
    {
        var samples = new[] { Row(0, 20.6, true), Row(10, -327.68, false), Row(20, 20.4, false) };

        var o = OverrideOutcomeAnalyzer.Analyze(Entry(), samples, nowTs: Start + 25 * 60);

        Assert.True(o.Running);
        Assert.Equal(25, o.DurationMinutes);
        Assert.Equal(2, o.SamplesDuring);
        Assert.Equal(20.4, o.TempMinC);
    }

    [Fact]
    public void Analyze_without_samples_reports_unknowns()
    {
        var o = OverrideOutcomeAnalyzer.Analyze(Entry(), [], nowTs: Start + 7200);

        Assert.Null(o.TempStartC);
        Assert.Null(o.CallingShareDuring);
        Assert.Null(o.TempChangeC);
    }

    [Fact]
    public void Summaries_group_finished_ease_offs_only()
    {
        var finished = new[] { Row(0, 21, true), Row(30, 20.5, false), Row(60, 20.4, false) };
        var outcomes = new[]
        {
            OverrideOutcomeAnalyzer.Analyze(Entry(20, 21), finished, Start + 7200),
            OverrideOutcomeAnalyzer.Analyze(Entry(20, 21), finished, Start + 7200),
            OverrideOutcomeAnalyzer.Analyze(Entry(22, 21), finished, Start + 7200),        // boost: excluded
            OverrideOutcomeAnalyzer.Analyze(Entry(19, 21), finished, Start + 10 * 60),     // still running: excluded
        };

        var summary = Assert.Single(OverrideOutcomeAnalyzer.SummarizeEaseOffs(outcomes));

        Assert.Equal("−1°", summary.ChangeLabel);
        Assert.Equal(2, summary.Count);
        Assert.Equal(-0.6, summary.AvgTempChangeC!.Value, 6);
    }
}
