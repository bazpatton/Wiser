using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

/// <summary>
/// Opt-in rule: when a room is within <see cref="WithinC"/> of its target but has kept calling for heat for
/// <see cref="SustainedMinutes"/>, apply one of the ease-off presets. Rooms already in an override are left alone.
/// </summary>
public sealed record AutoEaseOffPolicy(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("within_c")] double WithinC,
    [property: JsonPropertyName("sustained_minutes")] int SustainedMinutes,
    [property: JsonPropertyName("ease_off_index")] int EaseOffIndex,
    [property: JsonPropertyName("cooldown_minutes")] int CooldownMinutes,
    [property: JsonPropertyName("excluded_rooms")] IReadOnlyList<string> ExcludedRooms,
    [property: JsonPropertyName("notify")] bool Notify)
{
    public static readonly AutoEaseOffPolicy Default = new(
        Enabled: false,
        WithinC: 0.5,
        SustainedMinutes: 60,
        EaseOffIndex: 0,
        CooldownMinutes: 180,
        ExcludedRooms: [],
        Notify: true);

    public static AutoEaseOffPolicy Normalize(AutoEaseOffPolicy p) => p with
    {
        WithinC = Math.Clamp(Math.Round(p.WithinC * 10) / 10, 0, 3),
        SustainedMinutes = Math.Clamp(p.SustainedMinutes, 10, 240),
        EaseOffIndex = Math.Clamp(p.EaseOffIndex, 0, QuickActionPresets.MaxPresets - 1),
        CooldownMinutes = Math.Clamp(p.CooldownMinutes, 30, 1440),
        ExcludedRooms = (p.ExcludedRooms ?? [])
            .Where(static r => !string.IsNullOrWhiteSpace(r))
            .Select(static r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    public static AutoEaseOffPolicy Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Default;
        try
        {
            var parsed = JsonSerializer.Deserialize<AutoEaseOffPolicy>(json);
            return parsed is null ? Default : Normalize(parsed);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public bool IsExcluded(string roomName) =>
        ExcludedRooms.Any(r => string.Equals(r, roomName.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The configured preset, falling back to the first when the list has shrunk.</summary>
    public EaseOffPreset? ResolvePreset(IReadOnlyList<EaseOffPreset> presets) =>
        presets.Count == 0 ? null : presets[EaseOffIndex < presets.Count ? EaseOffIndex : 0];
}

public enum AutoEaseOffStatus
{
    /// <summary>Not near target while calling, so nothing to watch.</summary>
    Idle,

    /// <summary>Near target and calling heat; counting towards the sustained time.</summary>
    Waiting,

    /// <summary>Would apply, but something stops it (override running, cooldown, away, preset can't apply).</summary>
    Blocked,

    /// <summary>Apply the ease-off now.</summary>
    Apply,
}

/// <summary>Per-room memory between polls. <see cref="NearTargetSinceTs"/> resets whenever the condition breaks.</summary>
public sealed record AutoEaseOffRoomState(long? NearTargetSinceTs, long? LastAppliedTs)
{
    public static readonly AutoEaseOffRoomState Empty = new(null, null);
}

public sealed record AutoEaseOffDecision(int RoomId, string Room, AutoEaseOffStatus Status, string Reason, long? NearTargetSinceTs);

public static class AutoEaseOffEvaluator
{
    /// <summary>Decides for one room on one poll; pure, so the rule can be tested without a hub.</summary>
    public static (AutoEaseOffDecision Decision, AutoEaseOffRoomState State) Evaluate(
        AutoEaseOffPolicy policy,
        HubLiveRoom room,
        AutoEaseOffRoomState state,
        long nowTs,
        bool systemAway)
    {
        AutoEaseOffDecision D(AutoEaseOffStatus status, string reason, long? since = null) =>
            new(room.Id, room.Name, status, reason, since);
        var reset = state with { NearTargetSinceTs = null };

        if (policy.IsExcluded(room.Name))
            return (D(AutoEaseOffStatus.Idle, "Excluded in settings"), reset);
        if (room.Id <= 0 || Math.Abs(room.TempC - (-327.68)) < 0.001)
            return (D(AutoEaseOffStatus.Idle, "No live reading"), reset);
        if (room.HeatDemand == 0)
            return (D(AutoEaseOffStatus.Idle, "Not calling for heat"), reset);
        if (room.SetpointC is not { } target || WiserSetpointDisplay.IsHubOffSentinel(target))
            return (D(AutoEaseOffStatus.Idle, "No target"), reset);
        if (RoomDisplayHelpers.GetActiveOverrideEndUtc(room, DateTimeOffset.FromUnixTimeSeconds(nowTs)) is not null)
            return (D(AutoEaseOffStatus.Blocked, "An override is already running"), reset);
        if (systemAway)
            return (D(AutoEaseOffStatus.Blocked, "Whole house is in away mode"), reset);
        if (room.TempC < target - policy.WithinC - 1e-9)
            return (D(AutoEaseOffStatus.Idle, $"{room.TempC:F1} °C is more than {policy.WithinC:0.#} °C below target {target:F1} °C"), reset);

        var since = state.NearTargetSinceTs ?? nowTs;
        var waited = nowTs - since;
        var next = state with { NearTargetSinceTs = since };
        if (waited < policy.SustainedMinutes * 60L)
            return (D(AutoEaseOffStatus.Waiting, $"Near target and calling for {waited / 60} of {policy.SustainedMinutes} min", since), next);

        if (state.LastAppliedTs is { } last && nowTs - last < policy.CooldownMinutes * 60L)
            return (D(AutoEaseOffStatus.Blocked, $"Cooling down: eased off {(nowTs - last) / 60} min ago", since), next);

        return (D(AutoEaseOffStatus.Apply, $"Near target and calling for {waited / 60} min", since), next);
    }
}

/// <summary>
/// Runs <see cref="AutoEaseOffEvaluator"/> after each hub poll and applies ease-offs. State lives in memory, so a restart
/// starts the sustained-time count again (it never makes the rule fire sooner).
/// </summary>
public sealed class AutoEaseOffService(
    TemperatureStore store,
    NtfyClient ntfy,
    OverrideLogger overrideLog,
    ILogger<AutoEaseOffService> log)
{
    private readonly Dictionary<int, AutoEaseOffRoomState> _states = [];
    private readonly object _gate = new();
    private IReadOnlyList<AutoEaseOffDecision> _lastDecisions = [];

    public DateTimeOffset? LastEvaluatedAt { get; private set; }

    /// <summary>Latest decision per room, for the Settings diagnostics.</summary>
    public IReadOnlyList<AutoEaseOffDecision> LastDecisions
    {
        get
        {
            lock (_gate)
                return _lastDecisions;
        }
    }

    public async Task EvaluateAsync(HubLiveOverview overview, WiserHubFetch hub, MonitorOptions options, CancellationToken ct)
    {
        var policy = store.GetAutoEaseOffPolicy();
        if (!policy.Enabled)
        {
            lock (_gate)
            {
                _states.Clear();
                _lastDecisions = [];
            }

            LastEvaluatedAt = null;
            return;
        }

        var presets = store.GetQuickActionPresets().EaseOffs;
        var preset = policy.ResolvePreset(presets);
        var now = DateTimeOffset.UtcNow;
        var nowTs = now.ToUnixTimeSeconds();
        var decisions = new List<AutoEaseOffDecision>();

        foreach (var room in overview.Rooms.Where(static r => r.Id > 0))
        {
            AutoEaseOffRoomState state;
            lock (_gate)
                state = _states.GetValueOrDefault(room.Id, AutoEaseOffRoomState.Empty);

            var (decision, next) = AutoEaseOffEvaluator.Evaluate(policy, room, state, nowTs, overview.SystemAway);
            if (decision.Status == AutoEaseOffStatus.Apply)
                (decision, next) = await ApplyAsync(room, decision, next, preset, hub, options, now, ct).ConfigureAwait(false);

            lock (_gate)
                _states[room.Id] = next;
            decisions.Add(decision);
        }

        lock (_gate)
            _lastDecisions = decisions.OrderBy(static d => d.Room, StringComparer.OrdinalIgnoreCase).ToList();
        LastEvaluatedAt = now;
    }

    private async Task<(AutoEaseOffDecision, AutoEaseOffRoomState)> ApplyAsync(
        HubLiveRoom room,
        AutoEaseOffDecision decision,
        AutoEaseOffRoomState state,
        EaseOffPreset? preset,
        WiserHubFetch hub,
        MonitorOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (preset is null
            || TimedOverrideMath.EaseOffTargetC(room.SetpointC, preset) is not { } targetC
            || TimedOverrideMath.EaseOffMinutes(room.NextScheduleChangeUnix, preset, now) is not { } minutes)
        {
            return (decision with { Status = AutoEaseOffStatus.Blocked, Reason = "The chosen ease-off can't apply (no schedule change within 4 h?)" }, state);
        }

        try
        {
            await hub.PatchRoomBoostAsync(options, room.Id, targetC, minutes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auto ease-off failed for {Room}", room.Name);
            return (decision with { Status = AutoEaseOffStatus.Blocked, Reason = $"Hub rejected the ease-off: {ex.Message}" }, state);
        }

        overrideLog.RecordApplied(room.Id, targetC, minutes, OverrideSources.Auto);
        var applied = state with { NearTargetSinceTs = null, LastAppliedTs = now.ToUnixTimeSeconds() };
        var what = WiserSetpointDisplay.IsHubOffSentinel(targetC)
            ? "Heating off"
            : $"Lowered to {targetC:F1} °C";
        var message = $"{room.Name} was {room.TempC:F1} °C (target {room.SetpointC:F1} °C) and calling for heat for "
            + $"{(now.ToUnixTimeSeconds() - (decision.NearTargetSinceTs ?? now.ToUnixTimeSeconds())) / 60} min. "
            + $"{what} for {QuickActionPresets.FormatMinutes(minutes)}, then back to schedule.";
        log.LogInformation("Auto ease-off: {Message}", message);

        if (store.GetAutoEaseOffPolicy().Notify && !string.IsNullOrWhiteSpace(options.NtfyTopic))
        {
            try
            {
                await ntfy.SendAsync(
                    options.NtfyTopic,
                    "Auto ease-off",
                    message,
                    ct,
                    tags: "fire",
                    priority: "default",
                    kind: "auto_ease_off",
                    clickUrl: EaseOffLinks.RoomPage(options.AppPublicBaseUrl, room.Name)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auto ease-off notification failed");
            }
        }

        return (decision with { Reason = $"Applied: {what.ToLowerInvariant()} for {QuickActionPresets.FormatMinutes(minutes)}" }, applied);
    }
}
