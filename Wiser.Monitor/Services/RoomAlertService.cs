using Wiser.Monitor;

namespace Wiser.Monitor.Services;

public sealed class RoomAlertService(NtfyClient ntfy, ILogger<RoomAlertService> log)
{
    public async Task ProcessSampleAsync(
        MonitorOptions options,
        string observedRoom,
        double tempC,
        AlertLatches latches,
        CancellationToken ct)
    {
        if (!options.AlertsEnabled)
            return;

        var topic = options.NtfyTopic;

        if (options.UseHighAlert)
        {
            if (tempC > options.TempAlertAboveC)
            {
                if (!latches.LatchedHigh)
                {
                    try
                    {
                        await ntfy.SendAsync(
                            topic,
                            "Temperature high",
                            $"{observedRoom} is {tempC:F1} °C — above {options.TempAlertAboveC:F1} °C",
                            ct,
                            kind: "temp_high").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "ntfy high alert failed");
                    }
                    latches.LatchedHigh = true;
                }
            }
            else if (tempC <= options.TempAlertAboveC)
                latches.LatchedHigh = false;
        }

        if (options.UseLowAlert && options.TempAlertBelowC is { } below)
        {
            if (tempC < below)
            {
                if (!latches.LatchedLow)
                {
                    try
                    {
                        await ntfy.SendAsync(
                            topic,
                            "Temperature low",
                            $"{observedRoom} is {tempC:F1} °C — below {below:F1} °C",
                            ct,
                            kind: "temp_low").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "ntfy low alert failed");
                    }
                    latches.LatchedLow = true;
                }
            }
            else if (tempC >= below)
                latches.LatchedLow = false;
        }
    }
}
