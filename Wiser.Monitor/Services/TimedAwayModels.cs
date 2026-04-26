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
}
