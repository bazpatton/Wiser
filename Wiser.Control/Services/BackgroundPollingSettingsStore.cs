namespace Wiser.Control.Services;

public static class BackgroundPollingSettingsStore
{
	private const string KeyIntervalMinutes = "background_poll_interval_minutes";
	private static readonly int[] Allowed = [30, 60, 120];

	public static IReadOnlyList<int> AllowedIntervals => Allowed;

	public static int IntervalMinutes
	{
		get
		{
			var v = Preferences.Get(KeyIntervalMinutes, 30);
			return Allowed.Contains(v) ? v : 30;
		}
		set
		{
			var next = Allowed.Contains(value) ? value : 30;
			Preferences.Set(KeyIntervalMinutes, next);
		}
	}
}
