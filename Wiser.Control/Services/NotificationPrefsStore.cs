namespace Wiser.Control.Services;

public static class NotificationPrefsStore
{
	private const string KeyQuietHoursEnabled = "notif_quiet_enabled";
	private const string KeyQuietStartMinutes = "notif_quiet_start_mins";
	private const string KeyQuietEndMinutes = "notif_quiet_end_mins";
	private const string KeyCooldownMinutes = "notif_cooldown_mins";
	private const string KeyNotifyOnceUntilBelow = "notif_once_until_below";
	private const string KeyLastNotificationUnixSeconds = "notif_last_sent_unix_seconds";
	private const string KeyAlertTargetGroupIds = "notif_target_group_ids_csv";

	public static bool QuietHoursEnabled
	{
		get => Preferences.Get(KeyQuietHoursEnabled, false);
		set => Preferences.Set(KeyQuietHoursEnabled, value);
	}

	public static int QuietStartMinutes
	{
		get => Math.Clamp(Preferences.Get(KeyQuietStartMinutes, 22 * 60), 0, 1439);
		set => Preferences.Set(KeyQuietStartMinutes, Math.Clamp(value, 0, 1439));
	}

	public static int QuietEndMinutes
	{
		get => Math.Clamp(Preferences.Get(KeyQuietEndMinutes, 7 * 60), 0, 1439);
		set => Preferences.Set(KeyQuietEndMinutes, Math.Clamp(value, 0, 1439));
	}

	public static int CooldownMinutes
	{
		get => Math.Clamp(Preferences.Get(KeyCooldownMinutes, 0), 0, 24 * 60);
		set => Preferences.Set(KeyCooldownMinutes, Math.Clamp(value, 0, 24 * 60));
	}

	public static bool NotifyOnceUntilBelow
	{
		get => Preferences.Get(KeyNotifyOnceUntilBelow, true);
		set => Preferences.Set(KeyNotifyOnceUntilBelow, value);
	}

	public static DateTimeOffset? LastNotificationAt
	{
		get
		{
			var unix = Preferences.Get(KeyLastNotificationUnixSeconds, 0L);
			if (unix <= 0)
				return null;
			try
			{
				return DateTimeOffset.FromUnixTimeSeconds(unix);
			}
			catch
			{
				return null;
			}
		}
		set
		{
			if (value is null)
			{
				Preferences.Set(KeyLastNotificationUnixSeconds, 0L);
				return;
			}

			Preferences.Set(KeyLastNotificationUnixSeconds, value.Value.ToUnixTimeSeconds());
		}
	}

	public static List<string> AlertTargetGroupIds
	{
		get
		{
			var raw = Preferences.Get(KeyAlertTargetGroupIds, "");
			if (string.IsNullOrWhiteSpace(raw))
				return [];

			return raw
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.Distinct(StringComparer.Ordinal)
				.ToList();
		}
		set
		{
			var ids = (value ?? [])
				.Where(x => !string.IsNullOrWhiteSpace(x))
				.Select(x => x.Trim())
				.Distinct(StringComparer.Ordinal)
				.ToList();
			Preferences.Set(KeyAlertTargetGroupIds, string.Join(",", ids));
		}
	}

	public static bool IsQuietNow(DateTimeOffset nowLocal)
	{
		if (!QuietHoursEnabled)
			return false;

		var minute = nowLocal.Hour * 60 + nowLocal.Minute;
		var start = QuietStartMinutes;
		var end = QuietEndMinutes;

		// Same start/end means 24h quiet window.
		if (start == end)
			return true;

		// Normal window in same day.
		if (start < end)
			return minute >= start && minute < end;

		// Overnight window (e.g. 22:00 -> 07:00).
		return minute >= start || minute < end;
	}

	public static string GetQuietStatusSummary(DateTimeOffset nowLocal)
	{
		if (!QuietHoursEnabled)
			return "Quiet hours off";

		var nowMinute = nowLocal.Hour * 60 + nowLocal.Minute;
		var start = QuietStartMinutes;
		var end = QuietEndMinutes;
		var quietNow = IsQuietNow(nowLocal);
		var boundary = quietNow
			? end
			: start;
		var boundaryText = $"{boundary / 60:00}:{boundary % 60:00}";
		return quietNow
			? $"Quiet now until {boundaryText}"
			: $"Next quiet period starts {boundaryText}";
	}
}
