using System.Globalization;
using Wiser.Monitor;
using Wiser.Monitor.Services;

namespace Wiser.Monitor.Workers;

public sealed class TimedAwayWorker(
    MonitorOptions options,
    TemperatureStore store,
    WiserHubFetch hub,
    OutdoorWeatherClient outdoor,
    NtfyClient ntfy,
    ILogger<TimedAwayWorker> log) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));
        try
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[timed_away] event=error phase=initial_tick");
        }

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "[timed_away] event=error phase=tick");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var session = store.TryGetActiveTimedAwaySession();
        if (session is not null)
        {
            await ProcessActiveSessionAsync(session, now, ct).ConfigureAwait(false);
            return;
        }

        await TrySmartAwayArmAsync(now, ct).ConfigureAwait(false);
    }

    private async Task ProcessActiveSessionAsync(TimedAwaySessionRow session, long now, CancellationToken ct)
    {
        WriteDiagnostics(
            now,
            "active_session",
            skipReason: null,
            minForecast: null,
            indoorAvg: null,
            idlePollCount: null,
            sessionId: session.SessionId.ToString("D"),
            endsAtUnix: session.EndsAtUnix,
            errorMessage: null);

        var policy = store.GetTimedAwayPolicy();
        var click = TimedAwayDeepLinks.SettingsTimedAway(options.AppPublicBaseUrl);

        if (now >= session.EndsAtUnix)
        {
            if (HubConfiguration.IsConfigured(options))
            {
                try
                {
                    await hub.PatchSystemHomeAwayAsync(options, "HOME", null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "[timed_away] event=hub_home_fail session_id={SessionId}", session.SessionId);
                    return;
                }
            }
            else
                log.LogInformation("[timed_away] event=expire session_id={SessionId} hub=skipped_not_configured", session.SessionId);

            store.CompleteTimedAwaySession(session.SessionId);
            if (session.Source == TimedAwaySource.Smart)
                store.SetLastSmartAwayEndedUnix(now);

            log.LogInformation("[timed_away] event=expire session_id={SessionId} ends_at={EndsAt}", session.SessionId, session.EndsAtUnix);

            if (!string.IsNullOrWhiteSpace(options.NtfyTopic) && click is not null)
            {
                try
                {
                    await ntfy
                        .SendAsync(
                            options.NtfyTopic,
                            "Timed away ended",
                            "Monitor away session expired; hub set to Home. Open settings to adjust smart away.",
                            ct,
                            tags: "house",
                            priority: "default",
                            kind: "timed_away_expired",
                            clickUrl: click)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "[timed_away] event=error phase=expiry_ntfy");
                }
            }

            return;
        }

        var leadSec = Math.Max(1, policy.ExtensionLeadMinutes) * 60L;
        if (now < session.EndsAtUnix - leadSec)
            return;
        if (session.ExtensionPromptAtUnix.HasValue)
            return;
        if (string.IsNullOrWhiteSpace(options.NtfyTopic))
            return;

        try
        {
            var zone = TimeZoneResolver.Resolve(options.TimeZoneId);
            var endLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(session.EndsAtUnix), zone);
            const string title = "Away mode ending soon";
            var msg =
                $"Monitor away ends at {endLocal:yyyy-MM-dd HH:mm} ({zone.Id}). Open Wiser Monitor to extend or let heating restore.";
            await ntfy
                .SendAsync(
                    options.NtfyTopic,
                    title,
                    msg,
                    ct,
                    tags: "house",
                    priority: "default",
                    kind: "timed_away_extend",
                    clickUrl: click)
                .ConfigureAwait(false);
            store.MarkTimedAwayExtensionPromptSent(session.SessionId, now);
            log.LogInformation("[timed_away] event=extend_ntfy session_id={SessionId} ends_at={EndsAt}", session.SessionId, session.EndsAtUnix);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[timed_away] event=error phase=extend_ntfy");
        }
    }

    private async Task TrySmartAwayArmAsync(long now, CancellationToken ct)
    {
        var policy = store.GetTimedAwayPolicy();
        if (!policy.SmartAutoEnabled)
        {
            WriteDiagnostics(now, "skipped", "disabled", null, null, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=disabled");
            return;
        }

        if (!HubConfiguration.IsConfigured(options))
        {
            WriteDiagnostics(now, "skipped", "no_hub", null, null, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=no_hub");
            return;
        }

        if (options.OpenMeteoLat is not { } lat || options.OpenMeteoLon is not { } lon)
        {
            WriteDiagnostics(now, "skipped", "no_open_meteo", null, null, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=no_open_meteo");
            return;
        }

        var lastEnd = store.GetLastSmartAwayEndedUnix();
        if (lastEnd is { } le &&
            now - le < policy.SmartCooldownHours * 3600L)
        {
            WriteDiagnostics(now, "skipped", "cooldown", null, null, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=cooldown");
            return;
        }

        double? minForecast;
        try
        {
            minForecast = await outdoor
                .GetMinHourlyTempCNextHoursAsync(lat, lon, policy.ForecastHorizonHours, options.TimeZoneId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "[timed_away] event=skip reason=forecast_error");
            WriteDiagnostics(now, "skipped", "forecast_error", null, null, null, null, null, ex.Message);
            return;
        }

        if (minForecast is null || minForecast.Value < policy.MinForecastTempCTrigger)
        {
            WriteDiagnostics(now, "skipped", "forecast_low", minForecast, null, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=forecast_low min_forecast={Min:F1}", minForecast ?? double.NaN);
            return;
        }

        var avgIndoor = store.GetLatestRoomsAverageTempC();
        if (avgIndoor is null || avgIndoor.Value < policy.IndoorMinAvgTempC)
        {
            WriteDiagnostics(now, "skipped", "indoor_low", minForecast, avgIndoor, null, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=indoor_low indoor_avg={Indoor:F1}", avgIndoor ?? double.NaN);
            return;
        }

        var snaps = store.GetRecentSystemSnapshots(policy.HeatingIdlePollsRequired);
        if (snaps.Count < policy.HeatingIdlePollsRequired)
        {
            WriteDiagnostics(now, "skipped", "heating_not_idle", minForecast, avgIndoor, snaps.Count, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=heating_not_idle polls={Polls}", snaps.Count);
            return;
        }

        foreach (var s in snaps)
        {
            if (s.HeatingActive)
            {
                WriteDiagnostics(now, "skipped", "heating_not_idle", minForecast, avgIndoor, snaps.Count, null, null, null);
                log.LogInformation("[timed_away] event=skip reason=heating_not_idle polls={Polls}", snaps.Count);
                return;
            }
        }

        var overview = await LiveHubRoomTemps.TryFetchOverviewAsync(hub, options, ct).ConfigureAwait(false);
        if (overview is null)
        {
            WriteDiagnostics(now, "skipped", "hub_fetch_failed", minForecast, avgIndoor, snaps.Count, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=hub_fetch_failed");
            return;
        }

        if (overview.SystemAway)
        {
            WriteDiagnostics(now, "skipped", "hub_already_away", minForecast, avgIndoor, snaps.Count, null, null, null);
            log.LogInformation("[timed_away] event=skip reason=hub_already_away");
            return;
        }

        var durationMin = Math.Clamp(policy.SmartDefaultDurationMinutes, 30, 10080);
        var endsAt = now + durationMin * 60L;
        var limit = Math.Round(policy.AwayLimitC, 1);
        var click = TimedAwayDeepLinks.SettingsTimedAway(options.AppPublicBaseUrl);

        try
        {
            await hub.PatchSystemHomeAwayAsync(options, "AWAY", limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteDiagnostics(now, "error", "hub_away_failed", minForecast, avgIndoor, snaps.Count, null, null, ex.Message);
            log.LogWarning(ex, "[timed_away] event=skip reason=hub_away_failed");
            return;
        }

        var row = store.StartTimedAwaySession(endsAt, limit, TimedAwaySource.Smart);
        WriteDiagnostics(now, "armed", null, minForecast, avgIndoor, snaps.Count, row.SessionId.ToString("D"), endsAt, null);

        log.LogInformation(
            "[timed_away] event=arm session_id={SessionId} ends_at={EndsAt} forecast_min={Forecast:F1} indoor_avg={Indoor:F1}",
            row.SessionId,
            endsAt,
            minForecast.Value,
            avgIndoor.Value);

        if (!string.IsNullOrWhiteSpace(options.NtfyTopic) && click is not null)
        {
            try
            {
                await ntfy
                    .SendAsync(
                        options.NtfyTopic,
                        "Smart timed away armed",
                        $"Monitor armed away until {DateTimeOffset.FromUnixTimeSeconds(endsAt).ToString("O", CultureInfo.InvariantCulture)} (forecast min {minForecast.Value:F1} °C).",
                        ct,
                        tags: "house",
                        priority: "default",
                        kind: "timed_away_smart_arm",
                        clickUrl: click)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[timed_away] event=error phase=arm_ntfy");
            }
        }
    }

    private void WriteDiagnostics(
        long evaluatedAtUnix,
        string outcome,
        string? skipReason,
        double? minForecast,
        double? indoorAvg,
        int? idlePollCount,
        string? sessionId,
        long? endsAtUnix,
        string? errorMessage)
    {
        store.SetTimedAwayDiagnostics(
            new TimedAwayDiagnostics(
                evaluatedAtUnix,
                outcome,
                skipReason,
                minForecast,
                indoorAvg,
                idlePollCount,
                sessionId,
                endsAtUnix,
                errorMessage));
    }
}
