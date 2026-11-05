namespace Wiser.Monitor.Tests;

public class HomeUiPrefsTests
{
    [Fact]
    public void Round_trips_every_field()
    {
        var prefs = new HomeUiPrefs(QuickCollapsed: true, BoostTempC: 21.5, BoostMinutes: 90, AdjustMode: true, AdjustDeltaC: -1.5);

        Assert.Equal(prefs, HomeUiPrefs.Parse(prefs.Serialize()));
    }

    [Fact]
    public void Parses_the_browser_encoded_cookie_value() =>
        Assert.Equal(
            new HomeUiPrefs(QuickCollapsed: true, BoostMinutes: QuickActionPresets.UntilNextChange),
            HomeUiPrefs.Parse(Uri.EscapeDataString("c=1&m=0")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Missing_or_junk_is_empty(string? value) =>
        Assert.True(HomeUiPrefs.Parse(value).IsEmpty);

    [Fact]
    public void Out_of_range_values_are_ignored()
    {
        var p = HomeUiPrefs.Parse("c=2&t=99&m=3&a=yes&d=0.1");

        Assert.True(p.IsEmpty);
    }

    [Fact]
    public void Serialize_skips_unset_fields() =>
        Assert.Equal("c=0", new HomeUiPrefs(QuickCollapsed: false).Serialize());
}
