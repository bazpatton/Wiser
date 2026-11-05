using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

/// <summary>
/// A timed set-back offered while a room is calling heat: lower by <see cref="DropC"/> or turn heating off.
/// <see cref="Minutes"/> of <see cref="QuickActionPresets.UntilNextChange"/> runs until the room's schedule next changes.
/// </summary>
public sealed record EaseOffPreset(
    [property: JsonPropertyName("drop_c")] double DropC,
    [property: JsonPropertyName("minutes")] int Minutes,
    [property: JsonPropertyName("turn_off")] bool TurnOff)
{
    [JsonIgnore]
    public bool UntilNextChange => Minutes == QuickActionPresets.UntilNextChange;
}

/// <summary>Boost and ease-off presets shown on Home and Rooms; edited in Settings and stored in <c>app_settings</c>.</summary>
public sealed record QuickActionPresets(
    [property: JsonPropertyName("boosts")] IReadOnlyList<BoostPresetInfo> Boosts,
    [property: JsonPropertyName("ease_offs")] IReadOnlyList<EaseOffPreset> EaseOffs,
    [property: JsonPropertyName("updated_ts")] long UpdatedTs)
{
    public const int MaxPresets = 6;
    public const int MinMinutes = 5;

    /// <summary>Ease-off duration meaning "until the room's schedule next changes".</summary>
    public const int UntilNextChange = 0;

    /// <summary>Longest timed override the app sends to the hub (see <see cref="WiserHubFetch.PatchRoomBoostAsync"/>).</summary>
    public const int MaxMinutes = 240;

    public const double MinTempC = 5;
    public const double MaxTempC = 30;
    public const double MaxDropC = 10;

    public static readonly QuickActionPresets Default = new(
        [
            new BoostPresetInfo("Quick", 21, 30),
            new BoostPresetInfo("Comfort", 22, 60),
            new BoostPresetInfo("Gentle", 20, 45),
        ],
        [
            new EaseOffPreset(1, 60, TurnOff: false),
            new EaseOffPreset(2, 120, TurnOff: false),
            new EaseOffPreset(0, 60, TurnOff: true),
            new EaseOffPreset(0, UntilNextChange, TurnOff: true),
        ],
        0);

    /// <summary>Clamps values to what the hub accepts, drops blanks, and falls back to defaults for an empty list.</summary>
    public static QuickActionPresets Normalize(QuickActionPresets presets, long updatedTs)
    {
        var boosts = (presets.Boosts ?? [])
            .Where(static b => !string.IsNullOrWhiteSpace(b.Name))
            .Take(MaxPresets)
            .Select(static b => new BoostPresetInfo(
                b.Name.Trim(),
                Math.Clamp(Math.Round(b.TemperatureC * 2, MidpointRounding.AwayFromZero) / 2, MinTempC, MaxTempC),
                Math.Clamp(b.Minutes, MinMinutes, MaxMinutes)))
            .ToList();
        var easeOffs = (presets.EaseOffs ?? [])
            .Take(MaxPresets)
            .Select(static e => new EaseOffPreset(
                e.TurnOff ? 0 : Math.Clamp(Math.Round(e.DropC * 2, MidpointRounding.AwayFromZero) / 2, 0.5, MaxDropC),
                e.Minutes <= UntilNextChange ? UntilNextChange : Math.Clamp(e.Minutes, MinMinutes, MaxMinutes),
                e.TurnOff))
            .ToList();

        return new QuickActionPresets(
            boosts.Count > 0 ? boosts : Default.Boosts,
            easeOffs.Count > 0 ? easeOffs : Default.EaseOffs,
            updatedTs);
    }

    public static QuickActionPresets Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;
        try
        {
            var parsed = JsonSerializer.Deserialize<QuickActionPresets>(json);
            return parsed is null ? Default : Normalize(parsed, parsed.UpdatedTs);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    /// <summary>"1 h", "1½ h", "45 min".</summary>
    public static string FormatMinutes(int minutes)
    {
        if (minutes >= 60 && minutes % 60 == 0)
            return $"{minutes / 60} h";
        if (minutes >= 60 && minutes % 60 == 30)
            return $"{minutes / 60}½ h";
        return $"{minutes} min";
    }
}

/// <summary>Durations for re-sending a timed override: extending one, or running until the schedule next changes.</summary>
public static class TimedOverrideMath
{
    public const int ExtendStepMinutes = 30;

    /// <summary>
    /// Duration for a fresh override that ends <see cref="ExtendStepMinutes"/> after <paramref name="endUtc"/>;
    /// null when the hub's maximum leaves less than 5 minutes to add.
    /// </summary>
    public static int? ExtendMinutes(DateTimeOffset endUtc, DateTimeOffset now)
    {
        var remaining = Math.Max(0, (int)Math.Ceiling((endUtc - now).TotalMinutes));
        var minutes = Math.Min(QuickActionPresets.MaxMinutes, remaining + ExtendStepMinutes);
        return minutes - remaining >= QuickActionPresets.MinMinutes ? minutes : null;
    }

    /// <summary>Minutes until the room's schedule next changes, when that fits in one timed override.</summary>
    public static int? MinutesUntilNextChange(HubLiveRoom room, DateTimeOffset now) =>
        MinutesUntilNextChange(room.NextScheduleChangeUnix, now);

    public static int? MinutesUntilNextChange(long? nextChangeUnix, DateTimeOffset now)
    {
        if (nextChangeUnix is not { } at)
            return null;
        var minutes = (int)Math.Ceiling((at - now.ToUnixTimeSeconds()) / 60.0);
        return minutes is >= QuickActionPresets.MinMinutes and <= QuickActionPresets.MaxMinutes ? minutes : null;
    }

    /// <summary>
    /// Setpoint an ease-off sends: the off sentinel, or the current target lowered by the preset (not below the hub minimum).
    /// Null when lowering is asked for but the current target is unknown.
    /// </summary>
    public static double? EaseOffTargetC(double? currentTargetC, EaseOffPreset preset)
    {
        if (preset.TurnOff)
            return WiserSetpointDisplay.HubOffSentinelC;
        return currentTargetC is { } target
            ? Math.Max(QuickActionPresets.MinTempC, target - preset.DropC)
            : null;
    }

    /// <summary>Override length for an ease-off; null for "until next change" when that is unknown or too far off.</summary>
    public static int? EaseOffMinutes(long? nextChangeUnix, EaseOffPreset preset, DateTimeOffset now) =>
        preset.UntilNextChange ? MinutesUntilNextChange(nextChangeUnix, now) : preset.Minutes;
}
