using Wiser.Monitor;
using Wiser.Monitor.Services;

namespace Wiser.Monitor.Workers;

public sealed class WiserPollWorker(
    MonitorOptions options,
    TemperatureStore store,
    MonitorState state,
    WiserHubFetch hub,
    OutdoorWeatherClient outdoor,
    RoomAlertService alerts,
    ApiRoomsNamesCache apiRoomsNamesCache,
    ILogger<WiserPollWorker> log) : BackgroundService
{
    private int _pollCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, options.IntervalSec)));

        try
        {
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Initial hub poll failed (will retry)");
        }

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Catch everything — including TaskCanceledException from HttpClient timeouts —
                // so a transient hub blip or overnight reboot never kills the poll loop.
                state.SetPollFailure(ex.Message);
                log.LogWarning(ex, "hub poll error (will retry next interval)");
            }
        }
    }

    private const int LowBatteryThresholdPercent = 20;

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Fetch the domain document once; parse both room data and device battery from it.
        using var domainDoc = await hub.FetchDomainDocumentAsync(options, ct).ConfigureAwait(false);
        var poll = WiserHubFetch.ParseDomain(domainDoc);

        try
        {
            var lowBattery = HubDeviceParser.ParseDevices(domainDoc)
                .Where(d => d.BatteryPercent is <= LowBatteryThresholdPercent)
                .Select(d => string.IsNullOrWhiteSpace(d.Room) ? d.Name : $"{d.Name} ({d.Room})")
                .ToList();
            state.SetLowBatteryDevices(lowBattery);
        }
        catch
        {
            // Non-critical; don't fail the poll if device parse fails.
        }
        var samples = poll.Rooms;
        var excluded = poll.ExcludedReadings.Count;
        state.SetLastRooms(samples.Select(s => s.Name).ToList());
        state.SetLastPollRoomStats(samples.Count, excluded);
        if (samples.Count == 0)
        {
            log.LogWarning(
                "hub poll stored 0 room samples ({Excluded} excluded); trends need valid room temperatures in domain JSON",
                excluded);
        }

        store.InsertSystem(ts, poll.HeatingRelayOn, poll.HeatingActive);

        foreach (var ex in poll.ExcludedReadings)
            store.InsertDataQualityEvent(ts, ex.Room, ex.Source, ex.Reason, ex.RawValue);

        foreach (var s in samples)
            store.InsertRoom(ts, s.Name, s.TempC, s.SetpointC, s.HeatDemand, s.PercentageDemand, s.CurrentSetpointC, s.ScheduledSetpointC);

        foreach (var s in samples)
        {
            var latch = state.GetLatchesForRoom(s.Name);
            await alerts.ProcessSampleAsync(options, s.Name, s.TempC, latch, ct).ConfigureAwait(false);
        }

        state.PruneLatchesNotIn(samples.Select(s => s.Name).ToList());

        if (options.OpenMeteoLat is { } lat && options.OpenMeteoLon is { } lon)
        {
            try
            {
                var t = await outdoor.GetCurrentTempCAsync(lat, lon, ct).ConfigureAwait(false);
                if (t is { } outdoorC)
                    store.InsertOutdoor(ts, outdoorC);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "outdoor fetch failed");
            }
        }

        _pollCount++;
        if (_pollCount % 6 == 0)
            store.Prune(options.RetentionDays);

        try
        {
            await TimedAwayExpiry.TryExpireDueSessionAsync(store, hub, options, log, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[timed_away] event=error phase=poll_expire");
        }

        state.SetPollSuccess();
        apiRoomsNamesCache.Invalidate();
    }
}
