using System.Globalization;
using Wiser.Monitor;
using Wiser.Monitor.Services;

namespace Wiser.Monitor.Workers;

public sealed class GasMeterReminderWorker(
    MonitorOptions options,
    TemperatureStore store,
    NtfyClient ntfy,
    ILogger<GasMeterReminderWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));

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
            log.LogDebug(ex, "gas meter reminder initial tick failed");
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
                log.LogDebug(ex, "gas meter reminder tick failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.NtfyTopic))
            return;

        var settings = store.GetGasMeterReminderSettings();
        if (!settings.Enabled || settings.Times.Count == 0)
            return;

        var zone = TimeZoneResolver.Resolve(options.TimeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);

        foreach (var t in settings.Times)
        {
            if (nowLocal.Hour != t.Hours || nowLocal.Minute != t.Minutes)
                continue;

            var minuteKey = (int)t.TotalMinutes;
            var lastSent = store.GetGasMeterReminderLastSent();
            if (lastSent.TryGetValue(minuteKey, out var last) && last == today)
                continue;

            try
            {
                await ntfy
                    .SendAsync(
                        options.NtfyTopic,
                        "Gas meter reading",
                        "Time to record your gas meter reading. Open Wiser Monitor — Gas meter.",
                        ct,
                        tags: "fuelpump",
                        priority: "high")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "ntfy gas meter reminder failed");
                return;
            }

            store.SetGasMeterReminderLastSent(minuteKey, today);
            log.LogInformation(
                "Gas meter reminder sent for local time {Time} ({Zone})",
                $"{t.Hours:D2}:{t.Minutes:D2}",
                zone.Id);
        }
    }
}
