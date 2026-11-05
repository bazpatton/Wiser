namespace Wiser.Monitor.Tests;

public class AutoEaseOffTests
{
    private const long T0 = 1_800_000_000;

    private static readonly AutoEaseOffPolicy Policy = AutoEaseOffPolicy.Default with
    {
        Enabled = true,
        WithinC = 0.5,
        SustainedMinutes = 60,
        CooldownMinutes = 180,
    };

    private static (AutoEaseOffDecision D, AutoEaseOffRoomState S) Eval(
        HubLiveRoom room, AutoEaseOffRoomState? state = null, long now = T0, bool away = false, AutoEaseOffPolicy? policy = null) =>
        AutoEaseOffEvaluator.Evaluate(policy ?? Policy, room, state ?? AutoEaseOffRoomState.Empty, now, away);

    [Fact]
    public void Starts_waiting_when_near_target_and_calling()
    {
        var (d, s) = Eval(TestRooms.Room(tempC: 20.6, setpointC: 21));

        Assert.Equal(AutoEaseOffStatus.Waiting, d.Status);
        Assert.Equal(T0, s.NearTargetSinceTs);
    }

    [Fact]
    public void Applies_once_sustained_and_keeps_the_original_start()
    {
        var state = new AutoEaseOffRoomState(T0, null);

        var (early, _) = Eval(TestRooms.Room(), state, now: T0 + 59 * 60);
        var (due, s) = Eval(TestRooms.Room(), state, now: T0 + 60 * 60);

        Assert.Equal(AutoEaseOffStatus.Waiting, early.Status);
        Assert.Equal(AutoEaseOffStatus.Apply, due.Status);
        Assert.Equal(T0, s.NearTargetSinceTs);
    }

    [Theory]
    [InlineData(20.4, 21.0)]   // more than 0.5 below target: still legitimately warming up
    public void Far_below_target_is_idle_and_resets_the_count(double temp, double target)
    {
        var (d, s) = Eval(TestRooms.Room(tempC: temp, setpointC: target), new AutoEaseOffRoomState(T0 - 3600, null));

        Assert.Equal(AutoEaseOffStatus.Idle, d.Status);
        Assert.Null(s.NearTargetSinceTs);
    }

    [Fact]
    public void Exactly_at_the_threshold_counts_as_near()
    {
        var (d, _) = Eval(TestRooms.Room(tempC: 20.5, setpointC: 21));

        Assert.Equal(AutoEaseOffStatus.Waiting, d.Status);
    }

    [Fact]
    public void Not_calling_for_heat_is_idle_and_resets()
    {
        var (d, s) = Eval(TestRooms.Room(callingHeat: false), new AutoEaseOffRoomState(T0 - 3600, null));

        Assert.Equal(AutoEaseOffStatus.Idle, d.Status);
        Assert.Null(s.NearTargetSinceTs);
    }

    [Fact]
    public void Running_override_blocks_so_user_boosts_are_never_fought()
    {
        var room = TestRooms.Room(overrideTimeoutUnix: T0 + 600);

        var (d, s) = Eval(room, new AutoEaseOffRoomState(T0 - 7200, null));

        Assert.Equal(AutoEaseOffStatus.Blocked, d.Status);
        Assert.Null(s.NearTargetSinceTs);
    }

    [Fact]
    public void Whole_house_away_blocks() =>
        Assert.Equal(AutoEaseOffStatus.Blocked, Eval(TestRooms.Room(), away: true).D.Status);

    [Fact]
    public void Excluded_room_is_ignored_case_insensitively()
    {
        var policy = Policy with { ExcludedRooms = ["LIVING ROOM"] };

        Assert.Equal(AutoEaseOffStatus.Idle, Eval(TestRooms.Room(), policy: policy).D.Status);
    }

    [Fact]
    public void Cooldown_blocks_a_repeat_then_allows_it()
    {
        var state = new AutoEaseOffRoomState(T0 - 2 * 3600, LastAppliedTs: T0 - 60 * 60);

        var (during, _) = Eval(TestRooms.Room(), state, now: T0);
        var (after, _) = Eval(TestRooms.Room(), state, now: T0 + 2 * 3600);

        Assert.Equal(AutoEaseOffStatus.Blocked, during.Status);
        Assert.Equal(AutoEaseOffStatus.Apply, after.Status);
    }

    [Fact]
    public void Room_that_is_off_or_has_no_target_is_idle()
    {
        Assert.Equal(AutoEaseOffStatus.Idle, Eval(TestRooms.Room(setpointC: null)).D.Status);
        Assert.Equal(AutoEaseOffStatus.Idle, Eval(TestRooms.Room(setpointC: WiserSetpointDisplay.HubOffSentinelC)).D.Status);
    }

    [Fact]
    public void Policy_normalize_clamps_and_dedupes()
    {
        var p = AutoEaseOffPolicy.Normalize(new AutoEaseOffPolicy(true, 9, 1, 99, 1, ["Bath", "bath", " "], false));

        Assert.Equal(3, p.WithinC);
        Assert.Equal(10, p.SustainedMinutes);
        Assert.Equal(QuickActionPresets.MaxPresets - 1, p.EaseOffIndex);
        Assert.Equal(30, p.CooldownMinutes);
        Assert.Equal(new[] { "Bath" }, p.ExcludedRooms);
    }

    [Fact]
    public void Resolve_preset_falls_back_to_first_when_list_shrank()
    {
        var presets = QuickActionPresets.Default.EaseOffs.Take(2).ToList();

        Assert.Equal(presets[0], (Policy with { EaseOffIndex = 3 }).ResolvePreset(presets));
        Assert.Equal(presets[1], (Policy with { EaseOffIndex = 1 }).ResolvePreset(presets));
        Assert.Null(Policy.ResolvePreset([]));
    }
}
