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
            catch (HttpRequestException ex)
            {
                state.SetPollFailure(ex.Message);
                log.LogWarning(ex, "hub poll failed");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
            {
                state.SetPollFailure(ex.Message);
                log.LogWarning(ex, "hub poll error");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var poll = await hub.FetchDomainAsync(options, ct).ConfigureAwait(false);
        var samples = poll.Rooms;
        state.SetLastRooms(samples.Select(s => s.Name).ToList());

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
                log.LogDebug(ex, "outdoor fetch failed");
            }
        }

        _pollCount++;
        if (_pollCount % 6 == 0)
            store.Prune(options.RetentionDays);

        state.SetPollSuccess();
        apiRoomsNamesCache.Invalidate();
    }
}
