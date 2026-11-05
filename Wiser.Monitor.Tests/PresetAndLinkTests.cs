using System.Text.Json;

namespace Wiser.Monitor.Tests;

public class PresetAndLinkTests
{
    [Fact]
    public void Normalize_clamps_rounds_and_drops_blank_boosts()
    {
        var p = QuickActionPresets.Normalize(new QuickActionPresets(
            [new BoostPresetInfo(" Hot ", 40, 999), new BoostPresetInfo("Odd", 20.3, 1), new BoostPresetInfo("  ", 21, 30)],
            [new EaseOffPreset(0.2, 60, TurnOff: false)],
            0), 123);

        Assert.Equal(2, p.Boosts.Count);
        Assert.Equal(new BoostPresetInfo("Hot", 30, 240), p.Boosts[0]);
        Assert.Equal(new BoostPresetInfo("Odd", 20.5, 5), p.Boosts[1]);
        Assert.Equal(0.5, p.EaseOffs[0].DropC);
        Assert.Equal(123, p.UpdatedTs);
    }

    [Fact]
    public void Normalize_falls_back_to_defaults_for_empty_lists_and_caps_count()
    {
        var many = Enumerable.Range(0, 10).Select(i => new EaseOffPreset(1, 60, false)).ToList();
        var p = QuickActionPresets.Normalize(new QuickActionPresets([], many, 0), 0);

        Assert.Equal(QuickActionPresets.Default.Boosts, p.Boosts);
        Assert.Equal(QuickActionPresets.MaxPresets, p.EaseOffs.Count);
    }

    [Fact]
    public void Normalize_keeps_until_next_change_and_zeroes_drop_for_off()
    {
        var p = QuickActionPresets.Normalize(new QuickActionPresets(
            QuickActionPresets.Default.Boosts,
            [new EaseOffPreset(3, 0, TurnOff: true), new EaseOffPreset(1, -10, TurnOff: false)],
            0), 0);

        Assert.Equal(new EaseOffPreset(0, QuickActionPresets.UntilNextChange, true), p.EaseOffs[0]);
        Assert.True(p.EaseOffs[1].UntilNextChange);
    }

    [Fact]
    public void Presets_round_trip_through_json_and_bad_json_gives_defaults()
    {
        var json = JsonSerializer.Serialize(QuickActionPresets.Default);

        var parsed = QuickActionPresets.Parse(json);

        Assert.Equal(QuickActionPresets.Default.Boosts, parsed.Boosts);
        Assert.Equal(QuickActionPresets.Default.EaseOffs, parsed.EaseOffs);
        Assert.Same(QuickActionPresets.Default, QuickActionPresets.Parse("{not json"));
        Assert.Same(QuickActionPresets.Default, QuickActionPresets.Parse(null));
    }

    [Theory]
    [InlineData(60, "1 h")]
    [InlineData(90, "1½ h")]
    [InlineData(45, "45 min")]
    [InlineData(240, "4 h")]
    public void FormatMinutes(int minutes, string expected) =>
        Assert.Equal(expected, QuickActionPresets.FormatMinutes(minutes));

    [Theory]
    [InlineData("1-60", 1, 60, false)]
    [InlineData("1.5-90", 1.5, 90, false)]
    [InlineData("off-next", 0, QuickActionPresets.UntilNextChange, true)]
    [InlineData("OFF-60", 0, 60, true)]
    [InlineData("2-next", 2, QuickActionPresets.UntilNextChange, false)]
    public void Ease_link_parses(string value, double drop, int minutes, bool off)
    {
        var p = EaseOffLinks.TryParse(value);

        Assert.Equal(new EaseOffPreset(drop, minutes, off), p);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("x-60")]
    [InlineData("1-2")]   // under the minimum duration
    [InlineData("1-abc")]
    public void Ease_link_rejects_bad_values(string? value) =>
        Assert.Null(EaseOffLinks.TryParse(value));

    [Fact]
    public void Ease_link_round_trips_every_default_preset()
    {
        foreach (var preset in QuickActionPresets.Default.EaseOffs)
            Assert.Equal(preset, EaseOffLinks.TryParse(EaseOffLinks.ToQueryValue(preset)));
    }

    [Fact]
    public void Ease_link_build_uses_room_slug_and_needs_a_base_url()
    {
        var preset = new EaseOffPreset(1, 60, false);

        Assert.Equal("https://wiser.example/rooms/living-room?ease=1-60",
            EaseOffLinks.Build("https://wiser.example/", "Living Room", preset));
        Assert.Null(EaseOffLinks.Build(null, "Living Room", preset));
        Assert.Null(EaseOffLinks.NtfyActions("  ", "Living Room", [preset]));
    }

    [Fact]
    public void Ntfy_actions_are_ascii_and_at_most_three()
    {
        var presets = QuickActionPresets.Default.EaseOffs.Concat(QuickActionPresets.Default.EaseOffs).ToList();

        var actions = EaseOffLinks.NtfyActions("https://w.example", "Living room", presets)!;

        Assert.True(actions.All(char.IsAscii));
        Assert.Equal(3, actions.Split("; ").Length);
        Assert.StartsWith("view, Lower 1C for 1h, https://w.example/rooms/living-room?ease=1-60, clear=true", actions);
    }
}
