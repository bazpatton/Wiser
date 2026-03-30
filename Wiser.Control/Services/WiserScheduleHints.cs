using Wiser.Control.Models;

namespace Wiser.Control.Services;

/// <summary>Resolves “next schedule change” time for compact room-row hints (shared rules with Schedules page).</summary>
public static class WiserScheduleHints
{
	private static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

	public static string ResolveCurrentDay(WiserDomainPayload? payload)
	{
		var raw = payload?.System?.LocalDateAndTime?.Day;
		if (!string.IsNullOrWhiteSpace(raw) && DayNames.Contains(raw))
			return raw;

		return DateTime.Now.DayOfWeek switch
		{
			DayOfWeek.Monday => "Monday",
			DayOfWeek.Tuesday => "Tuesday",
			DayOfWeek.Wednesday => "Wednesday",
			DayOfWeek.Thursday => "Thursday",
			DayOfWeek.Friday => "Friday",
			DayOfWeek.Saturday => "Saturday",
			_ => "Sunday",
		};
	}

	public static int ResolveCurrentSeconds(WiserDomainPayload? payload)
	{
		var raw = payload?.System?.LocalDateAndTime?.Time ?? 0;
		return NormalizeSeconds((int)raw);
	}

	/// <summary>
	/// Hub <c>Time</c> values are usually minutes-from-midnight (0–1440, e.g. 1260 = 21:00).
	/// Some firmware uses compact HHMM for values &gt; 1440 only (e.g. 2100 = 21:00); treating that as seconds yields 00:35.
	/// </summary>
	public static int NormalizeSeconds(int raw)
	{
		if (raw < 0)
			return 0;

		if (raw is > 1440 and <= 2359)
		{
			var h = raw / 100;
			var m = raw % 100;
			if (h < 24 && m < 60)
				return h * 3600 + m * 60;
		}

		if (raw <= 1440)
			return raw * 60;

		if (raw > 86400)
			return raw % 86400;

		return raw;
	}

	public static string FormatTimeLabel(int rawScheduleTime)
	{
		var seconds = NormalizeSeconds(rawScheduleTime);
		var ts = TimeSpan.FromSeconds(seconds);
		return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}";
	}

	/// <summary>e.g. "Schedule until 18:00" or "Schedule until Tue 07:30".</summary>
	public static string GetScheduleUntilShort(WiserSchedule schedule, string currentDay, int currentSeconds)
	{
		for (var offset = 0; offset < 7; offset++)
		{
			var day = DayNames[(Array.IndexOf(DayNames, currentDay) + offset + 7) % 7];
			var points = OrderedPoints(schedule.GetDay(day));
			if (points.Count == 0)
				continue;

			var candidate = offset == 0
				? points.FirstOrDefault(p => NormalizeSeconds(p.Time) > currentSeconds)
				: points.FirstOrDefault();
			if (candidate is null)
				continue;

			var timePart = FormatTimeLabel(candidate.Time);
			if (offset == 0)
				return $"Schedule until {timePart}";
			return $"Schedule until {day[..3]} {timePart}";
		}

		return "";
	}

	private static List<WiserScheduleSetPoint> OrderedPoints(WiserScheduleDay? day) =>
		(day?.SetPoints ?? [])
			.OrderBy(p => NormalizeSeconds(p.Time))
			.ToList();

}
