using System.Collections.ObjectModel;
using System.Globalization;
using Wiser.Control.Models;
using Wiser.Control.Services;
using Wiser.Control.ViewModels;

namespace Wiser.Control;

public partial class MainPage : ContentPage
{
	/// <summary>Virtual filter chip id — not a user-defined room group.</summary>
	private const string HeatDemandFilterId = "__heat_demand";

	private WiserHubClient? _client;
	private WiserDomainPayload? _snapshot;
	private bool _refreshInProgress;
	private bool _actionInProgress;

	private readonly List<RoomRowViewModel> _allRooms = [];
	private readonly ObservableCollection<RoomRowViewModel> _visibleRooms = [];
	private Dictionary<int, int> _roomToChannel = new();
	private List<RoomGroup> _roomGroups = [];
	private string? _selectedGroupId;
	private DateTime _lastHubFetch;

	public MainPage()
	{
		InitializeComponent();
		RoomsCollection.ItemsSource = _visibleRooms;
		UpdateFilterChipStyles();
		UpdateUndoButtonState();
		UpdateNotificationSummary();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_client is not null)
		{
			ReloadGroupsAndRefreshFilter();
			UpdateUndoButtonState();
			UpdateNotificationSummary();
			return;
		}

		WiserConnection conn;
		try
		{
			conn = await WiserKeys.LoadFromAppPackageAsync();
		}
		catch (Exception ex)
		{
			StatusLabel.Text = $"Could not load wiserkeys.params: {ex.Message}";
			return;
		}

		try
		{
			_client = new WiserHubClient(conn);
			await ReloadFromHubAsync();
			UpdateUndoButtonState();
		}
		catch (Exception ex)
		{
			StatusLabel.Text = $"Could not reach the hub at {conn.HubIp} (Wi-Fi, IP, or secret): {ex.Message}";
		}
	}

	private async void OnRefreshClicked(object? sender, EventArgs e) => await RefreshAsync();

	private async void OnPullToRefresh(object? sender, EventArgs e)
	{
		try
		{
			await RefreshAsync();
		}
		finally
		{
			if (sender is RefreshView refreshView)
				refreshView.IsRefreshing = false;
		}
	}

	private async void OnSettingsClicked(object? sender, EventArgs e) =>
		await Shell.Current.GoToAsync(nameof(SettingsPage));

	private async Task RefreshAsync()
	{
		if (_client is null || _refreshInProgress)
			return;

		_refreshInProgress = true;
		RefreshButton.IsEnabled = false;
		StatusLabel.Text = "Refreshing...";

		try
		{
			await ReloadFromHubAsync();
		}
		catch (Exception ex)
		{
			StatusLabel.Text = ex.Message;
		}
		finally
		{
			_refreshInProgress = false;
			RefreshButton.IsEnabled = true;
		}
	}

	private async Task ReloadFromHubAsync()
	{
		if (_client is null)
			return;

		_snapshot = await _client.RefreshDomainAsync();
		TemperatureTrendStore.AppendFromDomain(_snapshot, DateTimeOffset.UtcNow);
		WidgetStatusStore.SaveFromDomain(_snapshot, DateTimeOffset.Now);
		WidgetSyncService.NotifyChanged();
		UpdateStatusLabels();

		_roomToChannel = BuildRoomToChannelMap(_snapshot.HeatingChannel);
		RebuildAllRoomsFromSnapshot();

		_lastHubFetch = DateTime.Now;
		await WarmRoomNotifier.OnRoomsRefreshedAsync(_snapshot.Room);
		UpdateFilterChipStyles();
		RefreshRoomsStatusLine();
		UpdateUndoButtonState();
		UpdateNotificationSummary();
	}

	private static Dictionary<int, int> BuildRoomToChannelMap(List<WiserHeatingChannel>? channels)
	{
		var map = new Dictionary<int, int>();
		if (channels is null)
			return map;

		foreach (var ch in channels)
		{
			if (ch.RoomIds is null)
				continue;
			foreach (var roomId in ch.RoomIds)
				map[roomId] = ch.Id;
		}

		return map;
	}

	private void RebuildAllRoomsFromSnapshot()
	{
		var hubRooms = _snapshot?.Room;
		_allRooms.Clear();
		if (hubRooms is null || hubRooms.Count == 0)
		{
			_visibleRooms.Clear();
			return;
		}

		var byId = hubRooms.ToDictionary(r => r.Id);
		var orderedIds = RoomOrderStore.OrderRoomIds(byId);
		foreach (var id in orderedIds)
			_allRooms.Add(new RoomRowViewModel(byId[id], _snapshot));

		RoomGroupsStore.EnsureMigratedFromChannels(_snapshot?.HeatingChannel);
		_roomGroups = RoomGroupsStore.Load();
		EnsureSelectedGroupExists();
		ApplyRoomFilter();
	}

	private List<RoomRowViewModel> GetFilteredRooms()
	{
		if (_allRooms.Count == 0)
			return [];

		if (string.IsNullOrWhiteSpace(_selectedGroupId))
			return _allRooms.ToList();

		if (string.Equals(_selectedGroupId, HeatDemandFilterId, StringComparison.Ordinal))
			return _allRooms.Where(r => r.RadiatorOn == true).ToList();

		var group = _roomGroups.FirstOrDefault(g => g.Id == _selectedGroupId);
		if (group is null || group.RoomIds.Count == 0)
			return [];

		var roomIdSet = group.RoomIds.ToHashSet();
		return _allRooms.Where(r => roomIdSet.Contains(r.RoomId)).ToList();
	}

	private void ApplyRoomFilter()
	{
		_visibleRooms.Clear();
		foreach (var r in GetFilteredRooms())
			_visibleRooms.Add(r);
	}

	private void RefreshRoomsStatusLine()
	{
		var visible = GetFilteredRooms().Count;
		var extra = !string.IsNullOrWhiteSpace(_selectedGroupId) && visible < _allRooms.Count
			? $" ({_allRooms.Count} total)"
			: "";

		var timePart = _lastHubFetch != default
			? $"Last updated {_lastHubFetch:t}"
			: "Not loaded";

		StatusLabel.Text = $"{timePart} - {visible} room(s){extra}.";
	}

	private void OnFilterChipClicked(object? sender, EventArgs e)
	{
		if (sender is not Button button)
			return;

		var nextGroupId = button.CommandParameter as string;
		if (string.Equals(_selectedGroupId, nextGroupId, StringComparison.Ordinal))
			return;

		_selectedGroupId = nextGroupId;
		ApplyRoomFilter();
		UpdateFilterChipStyles();
		RefreshRoomsStatusLine();
	}

	private void UpdateFilterChipStyles()
	{
		var outline = Application.Current?.Resources.TryGetValue("OutlineButtonStyle", out var s) == true
			? s as Style
			: null;

		FilterChipsHost.Children.Clear();

		var allButton = BuildFilterChip("All", null, string.IsNullOrWhiteSpace(_selectedGroupId), outline);
		FilterChipsHost.Children.Add(allButton);

		var heatDemandSelected = string.Equals(_selectedGroupId, HeatDemandFilterId, StringComparison.Ordinal);
		FilterChipsHost.Children.Add(BuildFilterChip("Calling for heat", HeatDemandFilterId, heatDemandSelected, outline));

		foreach (var group in _roomGroups)
		{
			var selected = string.Equals(_selectedGroupId, group.Id, StringComparison.Ordinal);
			var chip = BuildFilterChip(group.Name, group.Id, selected, outline);
			FilterChipsHost.Children.Add(chip);
		}
	}

	private static void SetChipStyle(Button button, bool selected, Style? outlineStyle) =>
		button.Style = selected ? null : outlineStyle;

	private Button BuildFilterChip(string text, string? groupId, bool selected, Style? outlineStyle)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 12,
			Padding = new Thickness(10, 8),
			MinimumHeightRequest = 40,
			CommandParameter = groupId,
		};
		SetChipStyle(button, selected, outlineStyle);
		button.Clicked += OnFilterChipClicked;
		return button;
	}

	private void ReloadGroupsAndRefreshFilter()
	{
		_roomGroups = RoomGroupsStore.Load();
		EnsureSelectedGroupExists();
		ApplyRoomFilter();
		UpdateFilterChipStyles();
		RefreshRoomsStatusLine();
		UpdateNotificationSummary();
	}

	private void EnsureSelectedGroupExists()
	{
		if (string.IsNullOrWhiteSpace(_selectedGroupId))
			return;

		var exists = string.Equals(_selectedGroupId, HeatDemandFilterId, StringComparison.Ordinal)
			|| _roomGroups.Any(g => g.Id == _selectedGroupId);
		if (!exists)
			_selectedGroupId = null;
	}

	private void UpdateStatusLabels()
	{
		if (_snapshot is null)
		{
			HeatingDemandLabel.IsVisible = false;
			return;
		}

		var relayOn = _snapshot.HeatingChannel?.Any(c => string.Equals(c.HeatingRelayState, "On", StringComparison.OrdinalIgnoreCase)) == true;
		var anyDemand = _snapshot.Room?.Any(r => r.IsDemandingHeat()) == true;

		HeatingLabel.Text = _snapshot.IsHeatingActive() ? "Heating: On" : "Heating: Off";
		UpdateHeatingDemandSummary(relayOn, anyDemand);
	}

	/// <summary>
	/// Hub domain does not expose individual TRV devices here; rooms use <see cref="WiserRoom.IsDemandingHeat"/>.
	/// </summary>
	private void UpdateHeatingDemandSummary(bool heatingRelayOn, bool anyRoomDemand)
	{
		if (!heatingRelayOn && !anyRoomDemand)
		{
			HeatingDemandLabel.IsVisible = false;
			HeatingDemandLabel.Text = "";
			return;
		}

		var rooms = _snapshot?.Room;
		if (rooms is null || rooms.Count == 0)
		{
			HeatingDemandLabel.IsVisible = false;
			return;
		}

		var demanding = rooms
			.Where(r => r.IsDemandingHeat())
			.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
			.Select(r =>
			{
				var name = string.IsNullOrWhiteSpace(r.Name) ? $"Room {r.Id}" : r.Name!;
				return r.PercentageDemand is > 0 and var pct ? $"{name} ({pct}%)" : name;
			})
			.ToList();

		HeatingDemandLabel.IsVisible = true;
		if (demanding.Count == 0)
		{
			HeatingDemandLabel.Text = "No rooms calling for heat (relay on — e.g. pump overrun or hot water).";
			return;
		}

		const int maxListed = 8;
		if (demanding.Count <= maxListed)
			HeatingDemandLabel.Text = "Calling for heat: " + string.Join(", ", demanding);
		else
			HeatingDemandLabel.Text = "Calling for heat: " + string.Join(", ", demanding.Take(maxListed)) + $" (+{demanding.Count - maxListed} more)";
	}

	private void UpdateNotificationSummary()
	{
		if (!AlertSettingsStore.RoomAlertsEnabled)
		{
			NotificationSummaryLabel.Text = "Alerts are off.";
			NotificationMetaLabel.Text = "Enable in Settings to resume warm-room notifications.";
			return;
		}

		var threshold = AlertSettingsStore.AlertThresholdC;
		var quietText = NotificationPrefsStore.GetQuietStatusSummary(DateTimeOffset.Now);
		var cooldownText = BuildCooldownText();
		var groupsText = BuildAlertGroupsText();

		NotificationSummaryLabel.Text = $"Threshold {threshold:0.#} C - {groupsText}";
		NotificationMetaLabel.Text = $"{quietText}. {cooldownText}.";
	}

	private static string BuildCooldownText()
	{
		var mins = NotificationPrefsStore.CooldownMinutes;
		if (mins <= 0)
			return "No cooldown";

		var last = NotificationPrefsStore.LastNotificationAt;
		if (last is null)
			return $"Cooldown {mins}m";

		var left = TimeSpan.FromMinutes(mins) - (DateTimeOffset.Now - last.Value.ToLocalTime());
		if (left <= TimeSpan.Zero)
			return $"Cooldown {mins}m (ready)";
		return $"Cooldown active ({Math.Ceiling(left.TotalMinutes):0}m left)";
	}

	private string BuildAlertGroupsText()
	{
		var selectedIds = NotificationPrefsStore.AlertTargetGroupIds;
		if (selectedIds.Count == 0)
			return "all rooms";

		var selectedNames = _roomGroups
			.Where(g => selectedIds.Contains(g.Id, StringComparer.Ordinal))
			.Select(g => g.Name)
			.ToList();
		if (selectedNames.Count == 0)
			return "all rooms";
		if (selectedNames.Count <= 2)
			return string.Join(", ", selectedNames);
		return $"{selectedNames.Count} groups";
	}

	private RoomRowViewModel? RowFromSender(object? sender) =>
		sender is BindableObject b ? b.BindingContext as RoomRowViewModel : null;

	private async void OnApplyTempClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null)
			return;

		var target = Math.Round(row.TargetSlider, 1);
		var history = BuildRoomHistory(row.RoomId, $"{row.Name}: set temperature", $"Set to {target:0.#} °C.");
		if (await RunRoomSafeAsync(row, async () =>
		{
			await _client.SetRoomTemperatureAsync(row.RoomId, target);
			await ReloadFromHubAsync();
		}))
			AppendHistory(history);
	}

	private async void OnQuickSetTempClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null)
			return;

		if (sender is not Button button || button.CommandParameter is null)
			return;

		if (!double.TryParse(Convert.ToString(button.CommandParameter, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var targetC))
			return;

		row.TargetSlider = targetC;
		var history = BuildRoomHistory(row.RoomId, $"{row.Name}: quick set", $"Quick set to {targetC:0.#} °C.");
		if (await RunRoomSafeAsync(row, async () =>
		{
			await _client.SetRoomTemperatureAsync(row.RoomId, Math.Round(targetC, 1));
			await ReloadFromHubAsync();
		}))
			AppendHistory(history);
	}

	private async void OnRoomModeMenuClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null || _snapshot is null)
			return;

		var presets = BoostPresetStore.Load();
		var presetOptions = presets.Select(p => $"Boost: {p.Name} ({p.Minutes}m @ {p.TemperatureC:0.#}°)").ToList();

		var actionOptions = new List<string> { "Auto", "Manual", "Off" };
		actionOptions.AddRange(presetOptions);
		actionOptions.Add("Boost (custom)");

		var choice = await DisplayActionSheet(
			$"{row.Name}: mode",
			"Cancel",
			null,
			actionOptions.ToArray());

		if (choice is null || choice == "Cancel")
			return;

		if (choice == "Boost (custom)")
		{
			await PromptAndApplyBoostAsync(row);
			return;
		}

		var matchedPreset = presets.FirstOrDefault(p => choice == $"Boost: {p.Name} ({p.Minutes}m @ {p.TemperatureC:0.#}°)");
		if (matchedPreset is not null)
		{
			var history = BuildRoomHistory(row.RoomId, $"{row.Name}: boost preset", $"{matchedPreset.Name} for {matchedPreset.Minutes} minutes at {matchedPreset.TemperatureC:0.#} °C.");
			if (await RunRoomSafeAsync(row, async () =>
			{
				await _client.SetRoomModeAsync(_snapshot, row.RoomId, "boost", matchedPreset.TemperatureC, matchedPreset.Minutes);
				await ReloadFromHubAsync();
			}))
				AppendHistory(history);
			return;
		}

		var mode = choice.ToLowerInvariant();
		var modeHistory = BuildRoomHistory(row.RoomId, $"{row.Name}: mode", $"Mode set to {choice}.");
		if (await RunRoomSafeAsync(row, async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, mode);
			await ReloadFromHubAsync();
		}))
			AppendHistory(modeHistory);
	}

	private async Task PromptAndApplyBoostAsync(RoomRowViewModel row)
	{
		if (_client is null || _snapshot is null)
			return;

		var tempStr = await DisplayPromptAsync("Boost", "Temperature (°C)", initialValue: "21", keyboard: Keyboard.Numeric);
		if (string.IsNullOrWhiteSpace(tempStr) || !double.TryParse(tempStr, out var temp))
			return;

		var minsStr = await DisplayPromptAsync("Boost", "Minutes", initialValue: "30", keyboard: Keyboard.Numeric);
		if (string.IsNullOrWhiteSpace(minsStr) || !int.TryParse(minsStr, out var mins))
			return;

		var history = BuildRoomHistory(row.RoomId, $"{row.Name}: custom boost", $"{mins} minutes at {temp:0.#} °C.");
		if (await RunRoomSafeAsync(row, async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, "boost", temp, mins);
			await ReloadFromHubAsync();
		}))
			AppendHistory(history);
	}

	private ActionHistoryEntry BuildRoomHistory(int roomId, string title, string details)
	{
		var state = CaptureRoomState(roomId);
		return new ActionHistoryEntry
		{
			Title = title,
			Details = details,
			UndoKind = state is null ? UndoKind.None : UndoKind.RoomState,
			RoomId = roomId,
			PrevMode = state?.Mode,
			PrevSetPointTenths = state?.SetPointTenths,
		};
	}

	private RoomSnapshotState? CaptureRoomState(int roomId)
	{
		var room = _snapshot?.Room?.FirstOrDefault(r => r.Id == roomId);
		if (room is null)
			return null;

		return new RoomSnapshotState(
			room.Mode ?? "Manual",
			room.CurrentSetPoint ?? room.ScheduledSetPoint ?? (WiserHubClient.TempMinimumC * 10));
	}

	private async Task<bool> RunRoomSafeAsync(RoomRowViewModel row, Func<Task> work)
	{
		if (row.IsActionBusy || _actionInProgress)
			return false;

		row.IsActionBusy = true;
		try
		{
			return await RunSafeAsync(work);
		}
		finally
		{
			row.IsActionBusy = false;
		}
	}

	private async void OnRoomAutoClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null || _snapshot is null)
			return;

		await RunSafeAsync(async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, "auto");
			await ReloadFromHubAsync();
		});
	}

	private async void OnRoomManualClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null || _snapshot is null)
			return;

		await RunSafeAsync(async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, "manual");
			await ReloadFromHubAsync();
		});
	}

	private async void OnRoomOffClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null || _snapshot is null)
			return;

		await RunSafeAsync(async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, "off");
			await ReloadFromHubAsync();
		});
	}

	private async void OnRoomBoostClicked(object? sender, EventArgs e)
	{
		var row = RowFromSender(sender);
		if (row is null || _client is null || _snapshot is null)
			return;

		var tempStr = await DisplayPromptAsync("Boost", "Temperature (°C)", initialValue: "21", keyboard: Keyboard.Numeric);
		if (string.IsNullOrWhiteSpace(tempStr) || !double.TryParse(tempStr, out var temp))
			return;

		var minsStr = await DisplayPromptAsync("Boost", "Minutes", initialValue: "30", keyboard: Keyboard.Numeric);
		if (string.IsNullOrWhiteSpace(minsStr) || !int.TryParse(minsStr, out var mins))
			return;

		await RunSafeAsync(async () =>
		{
			await _client.SetRoomModeAsync(_snapshot, row.RoomId, "boost", temp, mins);
			await ReloadFromHubAsync();
		});
	}

	private async void OnHomeClicked(object? sender, EventArgs e)
	{
		if (_client is null)
			return;

		if (await RunSafeAsync(async () =>
		{
			await _client.SetHomeAwayAsync("HOME", null);
			await ReloadFromHubAsync();
		}))
		{
			AppendHistory(new ActionHistoryEntry
			{
				Title = "System: Home mode",
				Details = "Switched system override to HOME.",
				UndoKind = UndoKind.None,
			});
		}
	}

	private async void OnAwayClicked(object? sender, EventArgs e)
	{
		if (_client is null)
			return;

		var tempStr = await DisplayPromptAsync("Away", "Away setpoint (°C), or -20 for off", initialValue: "10", keyboard: Keyboard.Numeric);
		if (string.IsNullOrWhiteSpace(tempStr) || !double.TryParse(tempStr, out var temp))
			return;

		if (await RunSafeAsync(async () =>
		{
			await _client.SetHomeAwayAsync("AWAY", temp);
			await ReloadFromHubAsync();
		}))
		{
			AppendHistory(new ActionHistoryEntry
			{
				Title = "System: Away mode",
				Details = $"Switched to AWAY at {temp:0.#} °C.",
				UndoKind = UndoKind.None,
			});
		}
	}

	private async void OnUndoLastClicked(object? sender, EventArgs e)
	{
		if (_client is null)
			return;

		var entry = ActionHistoryStore.Load().FirstOrDefault(x => x.UndoKind != UndoKind.None);
		if (entry is null)
		{
			UpdateUndoButtonState();
			return;
		}

		var success = await RunSafeAsync(async () =>
		{
			_snapshot ??= await _client.RefreshDomainAsync();
			var applied = await ActionUndoService.TryUndoAsync(_client, _snapshot, entry);
			if (!applied)
				throw new InvalidOperationException("This action can no longer be undone.");

			ActionHistoryStore.Remove(entry.Id);
			AppendHistory(new ActionHistoryEntry
			{
				Title = $"Undo: {entry.Title}",
				Details = "Previous room state restored from header action.",
				UndoKind = UndoKind.None,
			});
			await ReloadFromHubAsync();
		});

		if (success)
			await DisplayAlert("Undo", "Last action undone.", "OK");
	}

	private void AppendHistory(ActionHistoryEntry entry)
	{
		ActionHistoryStore.Append(entry);
		UpdateUndoButtonState();
	}

	private void UpdateUndoButtonState()
	{
		var hasUndoable = ActionHistoryStore.Load().Any(x => x.UndoKind != UndoKind.None);
		UndoLastButton.IsEnabled = hasUndoable && !_actionInProgress;
	}

	private async Task<bool> RunSafeAsync(Func<Task> work)
	{
		if (_actionInProgress)
			return false;

		_actionInProgress = true;
		UpdateUndoButtonState();
		try
		{
			await work();
			return true;
		}
		catch (Exception ex)
		{
			await DisplayAlert("Wiser", ex.Message, "OK");
			return false;
		}
		finally
		{
			_actionInProgress = false;
			UpdateUndoButtonState();
		}
	}

	private sealed record RoomSnapshotState(string Mode, int SetPointTenths);
}
