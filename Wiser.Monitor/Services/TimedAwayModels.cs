using System.Text.Json.Serialization;

namespace Wiser.Monitor.Services;

public enum TimedAwaySource
{
    Manual,
    Smart,
}

public sealed record TimedAwaySessionRow(
    Guid SessionId,
    long EndsAtUnix,
    double AwayLimitC,
    TimedAwaySource Source,
    long CreatedAtUnix,
    long? ExtensionPromptAtUnix,
    long? CancelledAtUnix,
    long? CompletedAtUnix,
    int SmartProfileVersion);

/// <summary>Last smart-away evaluation snapshot for Settings/API transparency.</summary>
public sealed record TimedAwayDiagnostics(
    [property: JsonPropertyName("evaluated_at_unix")] long EvaluatedAtUnix,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("skip_reason")] string? SkipReason,
    [property: JsonPropertyName("min_forecast_c")] double? MinForecastC,
    [property: JsonPropertyName("indoor_avg_c")] double? IndoorAvgC,
    [property: JsonPropertyName("heating_idle_poll_count")] int? HeatingIdlePollCount,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("ends_at_unix")] long? EndsAtUnix,
    [property: JsonPropertyName("error_message")] string? ErrorMessage);

public sealed record TimedAwayPolicy(
    [property: JsonPropertyName("smart_auto_enabled")] bool SmartAutoEnabled,
    [property: JsonPropertyName("away_limit_c")] double AwayLimitC,
    [property: JsonPropertyName("smart_default_duration_minutes")] int SmartDefaultDurationMinutes,
    [property: JsonPropertyName("extension_lead_minutes")] int ExtensionLeadMinutes,
    [property: JsonPropertyName("forecast_horizon_hours")] int ForecastHorizonHours,
    [property: JsonPropertyName("min_forecast_temp_c_trigger")] double MinForecastTempCTrigger,
    [property: JsonPropertyName("heating_idle_polls_required")] int HeatingIdlePollsRequired,
    [property: JsonPropertyName("indoor_min_avg_temp_c")] double IndoorMinAvgTempC,
    [property: JsonPropertyName("smart_cooldown_hours")] int SmartCooldownHours)
{
    public static TimedAwayPolicy Default { get; } = new(
        SmartAutoEnabled: false,
        AwayLimitC: 10.0,
        SmartDefaultDurationMinutes: 240,
        ExtensionLeadMinutes: 30,
        ForecastHorizonHours: 6,
        MinForecastTempCTrigger: 8.0,
        HeatingIdlePollsRequired: 3,
        IndoorMinAvgTempC: 18.0,
        SmartCooldownHours: 12);

    /// <summary>Clamp fields to the same bounds as the Settings UI / hub limits.</summary>
    public static TimedAwayPolicy Normalize(TimedAwayPolicy p)
    {
        static double ClampAwayLimit(double c) =>
            WiserSetpointDisplay.IsHubOffSentinel(c)
                ? c
                : Math.Clamp(Math.Round(c, 1), 5.0, 30.0);

        return p with
        {
            AwayLimitC = ClampAwayLimit(p.AwayLimitC),
            SmartDefaultDurationMinutes = Math.Clamp(p.SmartDefaultDurationMinutes, 30, 10080),
            ExtensionLeadMinutes = Math.Clamp(p.ExtensionLeadMinutes, 5, 1440),
            ForecastHorizonHours = Math.Clamp(p.ForecastHorizonHours, 1, 48),
            MinForecastTempCTrigger = Math.Clamp(Math.Round(p.MinForecastTempCTrigger, 1), -30.0, 40.0),
            HeatingIdlePollsRequired = Math.Clamp(p.HeatingIdlePollsRequired, 1, 50),
            IndoorMinAvgTempC = Math.Clamp(Math.Round(p.IndoorMinAvgTempC, 1), 5.0, 30.0),
            SmartCooldownHours = Math.Clamp(p.SmartCooldownHours, 0, 168),
        };
    }
}
