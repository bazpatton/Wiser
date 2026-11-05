namespace Wiser.Monitor.Tests;

public class TimedOverrideMathTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Theory]
    [InlineData(20, 50)]     // 20 min left -> 50
    [InlineData(0, 30)]      // just ended -> fresh 30
    [InlineData(-5, 30)]     // already past -> fresh 30
    [InlineData(230, 240)]   // capped at the 4 h maximum
    public void ExtendMinutes_adds_thirty_up_to_the_cap(int remainingMinutes, int expected) =>
        Assert.Equal(expected, TimedOverrideMath.ExtendMinutes(Now.AddMinutes(remainingMinutes), Now));

    [Fact]
    public void ExtendMinutes_is_null_when_under_five_minutes_could_be_added() =>
        Assert.Null(TimedOverrideMath.ExtendMinutes(Now.AddMinutes(238), Now));

    [Fact]
    public void EaseOffTargetC_lowers_from_target_but_not_below_hub_minimum()
    {
        Assert.Equal(20, TimedOverrideMath.EaseOffTargetC(21, new EaseOffPreset(1, 60, false)));
        Assert.Equal(5, TimedOverrideMath.EaseOffTargetC(6, new EaseOffPreset(2, 60, false)));
        Assert.Null(TimedOverrideMath.EaseOffTargetC(null, new EaseOffPreset(1, 60, false)));
    }

    [Fact]
    public void EaseOffTargetC_off_ignores_unknown_target() =>
        Assert.True(WiserSetpointDisplay.IsHubOffSentinel(
            TimedOverrideMath.EaseOffTargetC(null, new EaseOffPreset(0, 60, true))!.Value));

    [Fact]
    public void EaseOffMinutes_fixed_or_until_next_change()
    {
        var untilNext = new EaseOffPreset(0, QuickActionPresets.UntilNextChange, true);

        Assert.Equal(60, TimedOverrideMath.EaseOffMinutes(null, new EaseOffPreset(1, 60, false), Now));
        Assert.Equal(45, TimedOverrideMath.EaseOffMinutes(Now.AddMinutes(45).ToUnixTimeSeconds(), untilNext, Now));
        Assert.Null(TimedOverrideMath.EaseOffMinutes(null, untilNext, Now));
        Assert.Null(TimedOverrideMath.EaseOffMinutes(Now.AddHours(6).ToUnixTimeSeconds(), untilNext, Now));
    }

    [Fact]
    public void Active_override_end_reads_seconds_and_milliseconds()
    {
        var endSec = Now.AddMinutes(10).ToUnixTimeSeconds();

        Assert.NotNull(RoomDisplayHelpers.GetActiveOverrideEndUtc(TestRooms.Room(overrideTimeoutUnix: endSec), Now));
        Assert.NotNull(RoomDisplayHelpers.GetActiveOverrideEndUtc(TestRooms.Room(overrideTimeoutUnix: endSec * 1000), Now));
        Assert.Null(RoomDisplayHelpers.GetActiveOverrideEndUtc(TestRooms.Room(overrideTimeoutUnix: Now.AddMinutes(-1).ToUnixTimeSeconds()), Now));
        Assert.Null(RoomDisplayHelpers.GetActiveOverrideEndUtc(TestRooms.Room(overrideTimeoutUnix: 0), Now));
    }
}
