namespace Wiser.Monitor.Services;

/// <summary>A timed override this app sent to the hub, as recorded in <c>override_log</c>.</summary>
/// <param name="EndsTs">Planned end, moved earlier on cancel and later on extend.</param>
/// <param name="PreviousTargetC">The room's target just before the override (from the live snapshot); null when unknown.</param>
public sealed record OverrideLogEntry(
    long Id,
    int RoomId,
    string Room,
    long StartedTs,
    long EndsTs,
    double SetpointC,
    double? PreviousTargetC,
    string Source)
{
    public bool TurnedOff => WiserSetpointDisplay.IsHubOffSentinel(SetpointC);

    /// <summary>Heating off, or a setpoint below the target the room had.</summary>
    public bool IsEaseOff =>
        TurnedOff || (PreviousTargetC is { } prev && !WiserSetpointDisplay.IsHubOffSentinel(prev) && SetpointC < prev - 0.05);

    /// <summary>"Off", "−1°", or "21°" for a non-ease-off override.</summary>
    public string ChangeLabel
    {
        get
        {
            if (TurnedOff)
                return "Off";
            if (IsEaseOff && PreviousTargetC is { } prev)
                return $"−{prev - SetpointC:0.#}°";
            return $"{SetpointC:0.#}°";
        }
    }
}

/// <summary>Where an override came from, stored in <c>override_log.source</c>.</summary>
public static class OverrideSources
{
    public const string RoomPreset = "room_preset";
    public const string RoomSlider = "room_slider";
    public const string RoomEaseOff = "room_ease_off";
    public const string AlertLink = "alert_link";
    public const string HomeQuick = "home_quick";
    public const string HeatingNow = "heating_now";
    public const string Auto = "auto";
    public const string Api = "api";

    /// <summary>Not a new override: pushes the running one's end later.</summary>
    public const string Extend = "extend";

    public static string Describe(string source) => source switch
    {
        RoomPreset => "Room boost",
        RoomSlider => "Room timed boost",
        RoomEaseOff => "Room ease-off",
        AlertLink => "Alert link",
        HomeQuick => "Home quick action",
        HeatingNow => "Heating now",
        Auto => "Automatic",
        _ => "API",
    };
}

/// <summary>
/// Records overrides into <see cref="TemperatureStore"/>. Room name and the target before the change come from the
/// shared live snapshot, so callers only pass what they sent. Never throws: logging must not break heating control.
/// </summary>
public sealed class OverrideLogger(TemperatureStore store, RoomsLiveDataCache cache, ILogger<OverrideLogger> log)
{
    public void RecordApplied(int roomId, double setpointC, int minutes, string source)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HubLiveRoom? room = null;
            if (cache.TryGetSnapshot(out var overview, out _))
                room = overview.Rooms.FirstOrDefault(r => r.Id == roomId);

            // A new override replaces whatever was running, so close that first.
            store.SetOpenOverrideEnd(roomId, now, now);
            store.AddOverrideLog(
                roomId,
                room?.Name ?? $"Room {roomId}",
                now,
                now + Math.Max(1, minutes) * 60L,
                setpointC,
                room?.SetpointC,
                source);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record override for room {RoomId}", roomId);
        }
    }

    /// <summary>The running override now lasts <paramref name="minutes"/> from now.</summary>
    public void RecordExtended(int roomId, int minutes)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            store.SetOpenOverrideEnd(roomId, now, now + Math.Max(1, minutes) * 60L);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record override extension for room {RoomId}", roomId);
        }
    }

    /// <summary>The room went back to its schedule (cancel, or a mode change that clears overrides).</summary>
    public void RecordEnded(int roomId)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            store.SetOpenOverrideEnd(roomId, now, now);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not record override end for room {RoomId}", roomId);
        }
    }
}

/// <summary>What happened in the room during one override, from stored polls.</summary>
/// <param name="CallingShareDuring">Share of polls during the override with the room calling heat (0–1); null without samples.</param>
/// <param name="CallingShareBefore">Same share over an equally long window just before it, as a baseline.</param>
public sealed record OverrideOutcome(
    OverrideLogEntry Entry,
    bool Running,
    int DurationMinutes,
    double? TempStartC,
    double? TempEndC,
    double? TempMinC,
    double? CallingShareDuring,
    double? CallingShareBefore,
    int SamplesDuring)
{
    public double? TempChangeC => TempStartC is { } s && TempEndC is { } e ? e - s : null;
}

/// <summary>Per change-label summary, e.g. all "−1°" ease-offs.</summary>
public sealed record EaseOffPresetSummary(
    string ChangeLabel,
    int Count,
    double? AvgTempChangeC,
    double? AvgCallingShareDuring,
    double? AvgCallingShareBefore);

public static class OverrideOutcomeAnalyzer
{
    /// <summary>How far before the start a poll may be and still count as the starting temperature.</summary>
    private const long StartSampleToleranceSec = 15 * 60;

    /// <param name="samples">The room's stored polls, ascending by time, covering at least the baseline window before the start.</param>
    public static OverrideOutcome Analyze(OverrideLogEntry entry, IReadOnlyList<RoomSeriesRow> samples, long nowTs)
    {
        var running = entry.EndsTs > nowTs;
        var end = Math.Min(entry.EndsTs, nowTs);
        var length = Math.Max(0, end - entry.StartedTs);

        var during = samples.Where(s => s.Ts >= entry.StartedTs && s.Ts <= end && !IsOffline(s.TempC)).ToList();
        var before = samples
            .Where(s => s.Ts < entry.StartedTs && s.Ts >= entry.StartedTs - Math.Max(length, 60) && !IsOffline(s.TempC))
            .ToList();

        var startSample = samples.LastOrDefault(s =>
            s.Ts <= entry.StartedTs && s.Ts >= entry.StartedTs - StartSampleToleranceSec && !IsOffline(s.TempC))
            ?? during.FirstOrDefault();

        return new OverrideOutcome(
            entry,
            running,
            (int)Math.Round(length / 60.0),
            startSample?.TempC,
            during.Count > 0 ? during[^1].TempC : null,
            during.Count > 0 ? during.Min(static s => s.TempC) : null,
            CallingShare(during),
            CallingShare(before),
            during.Count);
    }

    /// <summary>Averages finished ease-offs by change label, most-used first.</summary>
    public static IReadOnlyList<EaseOffPresetSummary> SummarizeEaseOffs(IEnumerable<OverrideOutcome> outcomes) =>
        outcomes
            .Where(static o => o.Entry.IsEaseOff && !o.Running && o.SamplesDuring > 0)
            .GroupBy(static o => o.Entry.ChangeLabel)
            .Select(static g => new EaseOffPresetSummary(
                g.Key,
                g.Count(),
                Average(g.Select(static o => o.TempChangeC)),
                Average(g.Select(static o => o.CallingShareDuring)),
                Average(g.Select(static o => o.CallingShareBefore))))
            .OrderByDescending(static s => s.Count)
            .ThenBy(static s => s.ChangeLabel, StringComparer.Ordinal)
            .ToList();

    private static double? CallingShare(IReadOnlyCollection<RoomSeriesRow> rows) =>
        rows.Count == 0 ? null : rows.Count(static r => r.CallingForHeat) / (double)rows.Count;

    private static double? Average(IEnumerable<double?> values)
    {
        var known = values.OfType<double>().ToList();
        return known.Count == 0 ? null : known.Average();
    }

    private static bool IsOffline(double tempC) => Math.Abs(tempC - (-327.68)) < 0.001;
}
