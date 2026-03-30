using System.Diagnostics;
using Microsoft.Maui.Graphics;
using Plugin.LocalNotification;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class SettingsPage : ContentPage
{
	private bool _suppressThresholdEvent;
	private bool _suppressAdvancedEvents;

	public SettingsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_suppressThresholdEvent = true;
		AlertsSwitch.IsToggled = AlertSettingsStore.RoomAlertsEnabled;
		var t = AlertSettingsStore.AlertThresholdC;
		ThresholdSlider.Value = t;
		UpdateThresholdLabel(t);
		UpdateThresholdUiEnabled(AlertsSwitch.IsToggled);
		_suppressThresholdEvent = false;

		_suppressAdvancedEvents = true;
		QuietHoursSwitch.IsToggled = NotificationPrefsStore.QuietHoursEnabled;
		QuietStartTime.Time = TimeSpan.FromMinutes(NotificationPrefsStore.QuietStartMinutes);
		QuietEndTime.Time = TimeSpan.FromMinutes(NotificationPrefsStore.QuietEndMinutes);
		CooldownEntry.Text = NotificationPrefsStore.CooldownMinutes.ToString();
		NotifyOnceSwitch.IsToggled = NotificationPrefsStore.NotifyOnceUntilBelow;
		var allowed = BackgroundPollingSettingsStore.AllowedIntervals.ToList();
		BackgroundIntervalPicker.ItemsSource = allowed.Select(x => $"{x} minutes").ToList();
		var idx = allowed.IndexOf(BackgroundPollingSettingsStore.IntervalMinutes);
		BackgroundIntervalPicker.SelectedIndex = idx < 0 ? 0 : idx;
		BuildAlertGroupTargetingRows();
		UpdateQuietHoursUi();
		UpdateNotificationDashboard();
		BackgroundPollStatusLabel.Text = BackgroundPollDiagnosticsStore.GetStatusSummaryForDisplay();
		_suppressAdvancedEvents = false;
	}

	private void OnAlertsToggled(object? sender, ToggledEventArgs e)
	{
		AlertSettingsStore.RoomAlertsEnabled = e.Value;
		UpdateThresholdUiEnabled(e.Value);
		UpdateNotificationDashboard();
	}

	private void OnThresholdChanged(object? sender, ValueChangedEventArgs e)
	{
		if (_suppressThresholdEvent)
			return;

		var rounded = Math.Round(e.NewValue, 1);
		AlertSettingsStore.AlertThresholdC = rounded;
		UpdateThresholdLabel(rounded);
		UpdateNotificationDashboard();
	}

	private void UpdateThresholdLabel(double c) =>
		ThresholdValueLabel.Text = $"{c:0.#} °C";

	private void UpdateThresholdUiEnabled(bool alertsOn)
	{
		ThresholdSlider.IsEnabled = alertsOn;
		ThresholdCaptionLabel.Opacity = alertsOn ? 1 : 0.45;
		ThresholdValueLabel.Opacity = alertsOn ? 1 : 0.45;
	}

	private async void OnTestNotificationClicked(object? sender, EventArgs e)
	{
		try
		{
#if WINDOWS
			await MainThread.InvokeOnMainThreadAsync(() =>
			{
				WindowsNotificationHelper.ShowToast(
					"Wiser test alert",
					$"Alerts enabled. Threshold {AlertSettingsStore.AlertThresholdC:0.#} °C");
			});
			await DisplayAlert("Notifications", "Test notification sent.", "OK");
#else
			var perm = new NotificationPermission();
			if (!await LocalNotificationCenter.Current.AreNotificationsEnabled(perm))
				await LocalNotificationCenter.Current.RequestNotificationPermission(new NotificationPermission { AskPermission = true });

			if (!await LocalNotificationCenter.Current.AreNotificationsEnabled(perm))
			{
				await DisplayAlert("Notifications", "Notification permission is disabled.", "OK");
				return;
			}

			await LocalNotificationCenter.Current.Show(new NotificationRequest
			{
				NotificationId = 9001,
				Title = "Wiser test alert",
				Description = $"Alerts enabled. Threshold {AlertSettingsStore.AlertThresholdC:0.#} °C",
				Android = { ChannelId = "wiser_warm" },
			});

			await DisplayAlert("Notifications", "Test notification sent.", "OK");
#endif
		}
		catch (Exception ex)
		{
			await DisplayAlert("Notifications", ex.Message, "OK");
		}
	}

	private void OnQuietHoursToggled(object? sender, ToggledEventArgs e)
	{
		if (_suppressAdvancedEvents)
			return;

		NotificationPrefsStore.QuietHoursEnabled = e.Value;
		UpdateQuietHoursUi();
		UpdateNotificationDashboard();
	}

	private void OnQuietTimeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (_suppressAdvancedEvents || e.PropertyName != nameof(TimePicker.Time))
			return;

		NotificationPrefsStore.QuietStartMinutes = (int)QuietStartTime.Time.TotalMinutes;
		NotificationPrefsStore.QuietEndMinutes = (int)QuietEndTime.Time.TotalMinutes;
		UpdateQuietHoursUi();
		UpdateNotificationDashboard();
	}

	private void OnCooldownChanged(object? sender, TextChangedEventArgs e)
	{
		if (_suppressAdvancedEvents)
			return;

		if (!int.TryParse(e.NewTextValue, out var minutes))
			minutes = 0;

		NotificationPrefsStore.CooldownMinutes = minutes;
		UpdateNotificationDashboard();
	}

	private void OnNotifyOnceToggled(object? sender, ToggledEventArgs e)
	{
		if (_suppressAdvancedEvents)
			return;

		NotificationPrefsStore.NotifyOnceUntilBelow = e.Value;
		UpdateNotificationDashboard();
	}

	private void UpdateQuietHoursUi()
	{
		QuietHoursGrid.IsEnabled = QuietHoursSwitch.IsToggled;
		QuietHoursGrid.Opacity = QuietHoursSwitch.IsToggled ? 1 : 0.5;
		QuietPreviewLabel.Text = NotificationPrefsStore.GetQuietStatusSummary(DateTimeOffset.Now);
	}

	private void UpdateNotificationDashboard()
	{
		var alertsOn = AlertSettingsStore.RoomAlertsEnabled;
		var quietNow = NotificationPrefsStore.IsQuietNow(DateTimeOffset.Now);
		var groups = RoomGroupsStore.Load();
		var selected = NotificationPrefsStore.AlertTargetGroupIds;
		var activeGroupCount = groups.Count(g => selected.Contains(g.Id, StringComparer.Ordinal));

		NotificationDashboardSummaryLabel.Text = alertsOn
			? $"Threshold {AlertSettingsStore.AlertThresholdC:0.#} C, cooldown {NotificationPrefsStore.CooldownMinutes}m, {(NotificationPrefsStore.NotifyOnceUntilBelow ? "notify once" : "repeat while above")}."
			: "Alerts are disabled. Enable alerts to resume warm-room notifications.";

		AlertsChipLabel.Text = alertsOn ? "Alerts on" : "Alerts off";
		QuietChipLabel.Text = quietNow ? "Quiet now" : "Quiet open";
		TargetChipLabel.Text = activeGroupCount == 0 ? "Target: all rooms" : $"Target: {activeGroupCount} group(s)";

		SetChipVisual(AlertsChipBorder, AlertsChipLabel, alertsOn ? "#EAF8EE" : "#FFEFEF", alertsOn ? "#2D7A43" : "#A14141");
		SetChipVisual(QuietChipBorder, QuietChipLabel, quietNow ? "#F6F0FF" : "#EFF8FF", quietNow ? "#6C4CB3" : "#2E6B9A");
		SetChipVisual(TargetChipBorder, TargetChipLabel, "#FFF5E8", "#8A5A2A");
	}

	private static void SetChipVisual(Border border, Label label, string bgHex, string textHex)
	{
		var bg = Color.FromArgb(bgHex);
		var fg = Color.FromArgb(textHex);
		border.BackgroundColor = bg;
		border.Stroke = fg.WithAlpha(0.25f);
		label.TextColor = fg;
	}

	private void BuildAlertGroupTargetingRows()
	{
		AlertGroupChecksHost.Children.Clear();
		var groups = RoomGroupsStore.Load();
		var selected = NotificationPrefsStore.AlertTargetGroupIds.ToHashSet(StringComparer.Ordinal);

		if (groups.Count == 0)
		{
			AlertGroupHintLabel.Text = "No custom groups yet. Alerts will monitor all rooms.";
			return;
		}

		foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
		{
			var checkbox = new CheckBox
			{
				IsChecked = selected.Contains(group.Id),
				ClassId = group.Id,
				VerticalOptions = LayoutOptions.Center
			};
			checkbox.CheckedChanged += OnAlertGroupCheckedChanged;

			var label = new Label
			{
				Text = $"{group.Name} ({group.RoomIds.Count} room{(group.RoomIds.Count == 1 ? "" : "s")})",
				FontSize = 13,
				VerticalOptions = LayoutOptions.Center,
				TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#2C2C2C"),
			};

			AlertGroupChecksHost.Children.Add(new HorizontalStackLayout
			{
				Spacing = 8,
				Children = { checkbox, label }
			});
		}

		RefreshAlertGroupHint();
		UpdateNotificationDashboard();
	}

	private void OnAlertGroupCheckedChanged(object? sender, CheckedChangedEventArgs e)
	{
		if (_suppressAdvancedEvents)
			return;

		if (sender is not CheckBox box || string.IsNullOrWhiteSpace(box.ClassId))
			return;

		var ids = NotificationPrefsStore.AlertTargetGroupIds;
		if (e.Value)
		{
			if (!ids.Contains(box.ClassId, StringComparer.Ordinal))
				ids.Add(box.ClassId);
		}
		else
		{
			ids.RemoveAll(x => string.Equals(x, box.ClassId, StringComparison.Ordinal));
		}

		NotificationPrefsStore.AlertTargetGroupIds = ids;
		RefreshAlertGroupHint();
		UpdateNotificationDashboard();
	}

	private void RefreshAlertGroupHint()
	{
		var selectedCount = NotificationPrefsStore.AlertTargetGroupIds.Count;
		AlertGroupHintLabel.Text = selectedCount == 0
			? "Monitoring: all rooms"
			: $"Monitoring: {selectedCount} selected group(s)";
	}

	private async void OnRunDiagnosticsClicked(object? sender, EventArgs e)
	{
		DiagnosticsLabel.Text = "Running...";

		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync();

			var sw = Stopwatch.StartNew();
			using var client = new WiserHubClient(conn);
			var payload = await client.RefreshDomainAsync();
			sw.Stop();

			var roomCount = payload.Room?.Count ?? 0;
			DiagnosticsLabel.Text = $"Hub reachable at {conn.HubIp}. Auth OK. {roomCount} room(s). Roundtrip {sw.ElapsedMilliseconds} ms.";
		}
		catch (Exception ex)
		{
			DiagnosticsLabel.Text = $"Diagnostic failed: {ex.Message}";
		}
	}

	private async void OnManageRoomGroupsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(RoomGroupsPage));

	private async void OnEditBoostPresetsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(BoostPresetsPage));

	private async void OnOpenHistoryClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(ActionHistoryPage));

	private async void OnOpenTrendChartsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(TrendChartsPage));

	private async void OnOpenSchedulesClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(SchedulesPage));

	private void OnBackgroundIntervalChanged(object? sender, EventArgs e)
	{
		if (_suppressAdvancedEvents || BackgroundIntervalPicker.SelectedIndex < 0)
			return;

		var minutes = BackgroundPollingSettingsStore.AllowedIntervals[BackgroundIntervalPicker.SelectedIndex];
		BackgroundPollingSettingsStore.IntervalMinutes = minutes;
#if ANDROID
		BackgroundAlertScheduler.EnsureScheduled(Android.App.Application.Context!);
#elif WINDOWS
		WindowsBackgroundHubPoller.Restart();
#endif
	}

	private async void OnReorderRoomsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(ReorderRoomsPage));
}
