namespace Wiser.Control.Services;

/// <summary>Persisted room temperature alert preferences.</summary>
public static class AlertSettingsStore
{
	private const string KeyEnabled = "room_alerts_enabled";
	private const string KeyThresholdC = "room_alert_threshold_c";

	public const double ThresholdMinC = 10;
	public const double ThresholdMaxC = 30;
	public const double DefaultThresholdC = 20;

	public static bool RoomAlertsEnabled
	{
		get => Preferences.Get(KeyEnabled, true);
		set => Preferences.Set(KeyEnabled, value);
	}

	public static double AlertThresholdC
	{
		get
		{
			var v = Preferences.Get(KeyThresholdC, DefaultThresholdC);
			return Math.Clamp(Math.Round(v, 1), ThresholdMinC, ThresholdMaxC);
		}
		set => Preferences.Set(KeyThresholdC, Math.Clamp(Math.Round(value, 1), ThresholdMinC, ThresholdMaxC));
	}
}
