using System.Collections.ObjectModel;
using Wiser.Control.Models;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class SchedulesPage : ContentPage
{
	private static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
	private readonly ObservableCollection<RoomScheduleRow> _rows = [];
	private bool _loading;

	public SchedulesPage()
	{
		InitializeComponent();
		SchedulesCollection.ItemsSource = _rows;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loading)
			return;

		_loading = true;
		try
		{
			await LoadAsync();
		}
		finally
		{
			_loading = false;
		}
	}

	private async Task LoadAsync()
	{
		_rows.Clear();
		HintLabel.Text = "Loading room schedules...";

		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync();
			using var client = new WiserHubClient(conn);
			var payload = await client.RefreshDomainAsync();
			BuildRows(payload);
		}
		catch (Exception ex)
		{
			HintLabel.Text = $"Could not load schedules: {ex.Message}";
		}
	}

	private void BuildRows(WiserDomainPayload payload)
	{
		var rooms = payload.Room ?? [];
		var schedules = payload.Schedule ?? [];
		var todayName = ResolveCurrentDay(payload);
		var currentSeconds = ResolveCurrentSeconds(payload);

		foreach (var room in rooms.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
		{
			var roomName = string.IsNullOrWhiteSpace(room.Name) ? $"Room {room.Id}" : room.Name!;
			var schedule = schedules.FirstOrDefault(s => s.Id == room.ScheduleId);
			if (schedule is null)
			{
				_rows.Add(new RoomScheduleRow(roomName, "No schedule linked.", "No timeline available."));
				continue;
			}

			var nextChange = BuildNextChangeText(schedule, todayName, currentSeconds);
			var timeline = BuildTodayTimeline(schedule, todayName);
			_rows.Add(new RoomScheduleRow(roomName, nextChange, timeline));
		}

		HintLabel.Text = _rows.Count == 0
			? "No rooms found."
			: $"{_rows.Count} room schedule(s)";
	}

	private static string BuildTodayTimeline(WiserSchedule schedule, string dayName)
	{
		var points = OrderedPoints(schedule.GetDay(dayName));
		if (points.Count == 0)
			return "Today: no setpoints configured.";

		var entries = points
			.Select(p => $"{WiserScheduleHints.FormatTimeLabel(p.Time)} {FormatDegrees(p.DegreesC)}")
			.ToList();
		return $"Today: {string.Join("  -  ", entries)}";
	}

	private static string BuildNextChangeText(WiserSchedule schedule, string currentDay, int currentSeconds)
	{
		for (var offset = 0; offset < 7; offset++)
		{
			var day = DayNames[(Array.IndexOf(DayNames, currentDay) + offset + 7) % 7];
			var points = OrderedPoints(schedule.GetDay(day));
			if (points.Count == 0)
				continue;

			var candidate = offset == 0
				? points.FirstOrDefault(p => WiserScheduleHints.NormalizeSeconds(p.Time) > currentSeconds)
				: points.FirstOrDefault();
			if (candidate is null)
				continue;

			var when = offset == 0 ? "today" : $"on {day}";
			return $"Next change {when} at {WiserScheduleHints.FormatTimeLabel(candidate.Time)} to {FormatDegrees(candidate.DegreesC)}.";
		}

		return "No upcoming setpoint found.";
	}

	private static List<WiserScheduleSetPoint> OrderedPoints(WiserScheduleDay? day) =>
		(day?.SetPoints ?? [])
			.OrderBy(p => WiserScheduleHints.NormalizeSeconds(p.Time))
			.ToList();

	private static string ResolveCurrentDay(WiserDomainPayload payload)
	{
		var raw = payload.System?.LocalDateAndTime?.Day;
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

	private static int ResolveCurrentSeconds(WiserDomainPayload payload) =>
		WiserScheduleHints.ResolveCurrentSeconds(payload);

	private static string FormatDegrees(int tenths) =>
		$"{WiserHubClient.FromWiserTemp(tenths):0.#} C";
}

public sealed record RoomScheduleRow(string RoomName, string NextChangeText, string TimelineTodayText);
