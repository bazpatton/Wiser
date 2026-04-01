namespace Wiser.Monitor.Services;

/// <summary>
/// Hub represents “off” as <c>-200</c> tenths of °C (−20 °C). UI should show that as <c>0</c> for target display.
/// Matches <see cref="WiserHubFetch"/> manual-off payload (<c>TempOffC</c>).
/// </summary>
public static class WiserSetpointDisplay
{
    public const double HubOffSentinelC = -20.0;

    private const double SentinelTolerance = 0.15;

    public static bool IsHubOffSentinel(double setpointC) =>
        Math.Abs(setpointC - HubOffSentinelC) < SentinelTolerance;

    public static double ToDisplayTargetC(double setpointC) =>
        IsHubOffSentinel(setpointC) ? 0 : setpointC;

    public static double? ToDisplayTargetC(double? setpointC) =>
        setpointC is { } v ? ToDisplayTargetC(v) : null;

    /// <summary>“—” when null; otherwise formatted display °C (off → 0).</summary>
    public static string FormatTargetForUi(double? setpointC) =>
        setpointC is null ? "—" : $"{ToDisplayTargetC(setpointC.Value):F1} °C";
}
