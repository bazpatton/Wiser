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
    private static bool HubConfigured(MonitorOptions o) =>
        !string.IsNullOrWhiteSpace(o.WiserIp) && o.WiserIp != "192.168.x.x" &&
        !string.IsNullOrWhiteSpace(o.WiserSecret) && o.WiserSecret != "your-secret-here";

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
            log.LogDebug(ex, "timed away initial tick failed");
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
                log.LogDebug(ex, "timed away tick failed");
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
        var policy = store.GetTimedAwayPolicy();

        if (now >= session.EndsAtUnix)
        {
            if (HubConfigured(options))
            {
                try
                {
                    await hub.PatchSystemHomeAwayAsync(options, "HOME", null, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "timed away expiry: hub HOME failed");
                }
            }
            else
                log.LogInformation("timed away expiry: hub not configured; completing session in DB only");

            store.CompleteTimedAwaySession(session.SessionId);
            if (session.Source == TimedAwaySource.Smart)
                store.SetLastSmartAwayEndedUnix(now);
            log.LogInformation("Timed away session {Session} completed (expired)", session.SessionId);
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
                    kind: "timed_away_extend")
                .ConfigureAwait(false);
            store.MarkTimedAwayExtensionPromptSent(session.SessionId, now);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "timed away extension ntfy failed");
        }
    }

    private async Task TrySmartAwayArmAsync(long now, CancellationToken ct)
    {
        var policy = store.GetTimedAwayPolicy();
        if (!policy.SmartAutoEnabled || !HubConfigured(options))
            return;
        if (options.OpenMeteoLat is not { } lat || options.OpenMeteoLon is not { } lon)
            return;

        var lastEnd = store.GetLastSmartAwayEndedUnix();
        if (lastEnd is { } le &&
            now - le < policy.SmartCooldownHours * 3600L)
            return;

        double? minForecast;
        try
        {
            minForecast = await outdoor
                .GetMinHourlyTempCNextHoursAsync(lat, lon, policy.ForecastHorizonHours, options.TimeZoneId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "smart away: forecast fetch failed");
            return;
        }

        if (minForecast is null || minForecast.Value < policy.MinForecastTempCTrigger)
            return;

        var avgIndoor = store.GetLatestRoomsAverageTempC();
        if (avgIndoor is null || avgIndoor.Value < policy.IndoorMinAvgTempC)
            return;

        var snaps = store.GetRecentSystemSnapshots(policy.HeatingIdlePollsRequired);
        if (snaps.Count < policy.HeatingIdlePollsRequired)
            return;
        foreach (var s in snaps)
        {
            if (s.HeatingActive)
                return;
        }

        var durationMin = Math.Clamp(policy.SmartDefaultDurationMinutes, 30, 10080);
        var endsAt = now + durationMin * 60L;
        var limit = Math.Round(policy.AwayLimitC, 1);

        try
        {
            await hub.PatchSystemHomeAwayAsync(options, "AWAY", limit, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "smart away: hub AWAY failed");
            return;
        }

        store.StartTimedAwaySession(endsAt, limit, TimedAwaySource.Smart);
        log.LogInformation(
            "Smart timed away armed until {Ends} (forecast min {Forecast:F1} °C, indoor avg {Indoor:F1} °C)",
            DateTimeOffset.FromUnixTimeSeconds(endsAt).ToString("O", CultureInfo.InvariantCulture),
            minForecast.Value,
            avgIndoor.Value);
    }
}
