using System.Collections.Generic;
using System.Text.Json;
using Plugin.LocalNotification;
using Plugin.LocalNotification.AndroidOption;
using Wiser.Control.Models;

namespace Wiser.Control.Services;

/// <summary>
/// Fires a local notification when a room crosses above the configured alert temperature (edge-triggered per refresh).
/// Invoked from main UI refresh on all platforms and from <see cref="BackgroundHubTemperatureRunner"/> (Android inexact alarm; Windows timer while the process runs).
/// </summary>
internal static class WarmRoomNotifier
{
	private const string AndroidChannelId = "wiser_warm";
	private const string KeyOverThresholdByRoom = "warm_room_over_threshold_state_v1";

	private static readonly Dictionary<int, bool> WasOverThreshold = new();
	private static readonly SemaphoreSlim StateGate = new(1, 1);
	private static bool _loadedState;
#if !WINDOWS
	private static int _notificationIdSeq = 7000;
#endif

	public static Task OnRoomsRefreshedAsync(IReadOnlyList<WiserRoom>? rooms) =>
		OnRoomsRefreshedCoreAsync(rooms, requestPermissionIfNeeded: true);

	public static Task OnRoomsRefreshedFromBackgroundAsync(IReadOnlyList<WiserRoom>? rooms) =>
		OnRoomsRefreshedCoreAsync(rooms, requestPermissionIfNeeded: false);

	private static async Task OnRoomsRefreshedCoreAsync(IReadOnlyList<WiserRoom>? rooms, bool requestPermissionIfNeeded)
	{
		if (rooms is null)
		{
			await ReplaceStateAsync([]).ConfigureAwait(false);
			return;
		}

		var threshold = AlertSettingsStore.AlertThresholdC;
		var targetRoomIds = BuildTargetRoomIdSet();
		var newlyWarm = new List<(string Name, double C)>();
		var overThresholdNow = new List<(string Name, double C)>();

		await StateGate.WaitAsync().ConfigureAwait(false);
		try
		{
			EnsureStateLoaded();

			if (rooms.Count == 0)
			{
				WasOverThreshold.Clear();
				SaveState();
				return;
			}

			var seenIds = new HashSet<int>();

			foreach (var room in rooms)
			{
				seenIds.Add(room.Id);
				if (targetRoomIds is not null && !targetRoomIds.Contains(room.Id))
				{
					WasOverThreshold[room.Id] = false;
					continue;
				}

				if (!TryGetDisplayableTempC(room, out var c))
				{
					WasOverThreshold[room.Id] = false;
					continue;
				}

				var over = c > threshold;
				var was = WasOverThreshold.GetValueOrDefault(room.Id, false);
				if (over && !was)
					newlyWarm.Add((room.Name ?? $"Room {room.Id}", c));
				if (over)
					overThresholdNow.Add((room.Name ?? $"Room {room.Id}", c));
				WasOverThreshold[room.Id] = over;
			}

			foreach (var key in WasOverThreshold.Keys.ToArray())
			{
				if (!seenIds.Contains(key))
					WasOverThreshold.Remove(key);
			}

			SaveState();
		}
		finally
		{
			StateGate.Release();
		}

		var notifyOnce = NotificationPrefsStore.NotifyOnceUntilBelow;
		var candidates = notifyOnce ? newlyWarm : overThresholdNow;
		if (candidates.Count == 0)
			return;

		if (!AlertSettingsStore.RoomAlertsEnabled)
			return;

		var now = DateTimeOffset.Now;
		if (NotificationPrefsStore.IsQuietNow(now))
			return;

		var cooldownMinutes = NotificationPrefsStore.CooldownMinutes;
		var lastSent = NotificationPrefsStore.LastNotificationAt;
		if (cooldownMinutes > 0 && lastSent is not null && now - lastSent.Value < TimeSpan.FromMinutes(cooldownMinutes))
			return;

		var lines = string.Join("; ", candidates.Select(x => $"{x.Name} {x.C:0.#} °C"));
		var title = $"Room over {threshold:0.#} °C";

		try
		{
#if WINDOWS
			await MainThread.InvokeOnMainThreadAsync(() =>
			{
				WindowsNotificationHelper.ShowToast(title, lines);
			}).ConfigureAwait(false);
			NotificationPrefsStore.LastNotificationAt = now;
#else
			var perm = new NotificationPermission();
			var enabled = await LocalNotificationCenter.Current.AreNotificationsEnabled(perm).ConfigureAwait(false);
			if (!enabled && requestPermissionIfNeeded)
				await LocalNotificationCenter.Current.RequestNotificationPermission(new NotificationPermission { AskPermission = true }).ConfigureAwait(false);

			enabled = await LocalNotificationCenter.Current.AreNotificationsEnabled(perm).ConfigureAwait(false);
			if (!enabled)
				return;

			var id = Interlocked.Increment(ref _notificationIdSeq);
			var request = new NotificationRequest
			{
				NotificationId = id,
				Title = title,
				Description = lines,
				Android =
				{
					ChannelId = AndroidChannelId,
					Priority = AndroidPriority.High,
				},
			};

			await LocalNotificationCenter.Current.Show(request).ConfigureAwait(false);
			NotificationPrefsStore.LastNotificationAt = now;
#endif
		}
		catch
		{
			// Notifications are optional; never break hub refresh.
		}
	}

	private static HashSet<int>? BuildTargetRoomIdSet()
	{
		var groupIds = NotificationPrefsStore.AlertTargetGroupIds;
		if (groupIds.Count == 0)
			return null;

		var groups = RoomGroupsStore.Load();
		var selected = groups
			.Where(g => groupIds.Contains(g.Id, StringComparer.Ordinal))
			.SelectMany(g => g.RoomIds)
			.ToHashSet();
		return selected.Count == 0 ? null : selected;
	}

	private static async Task ReplaceStateAsync(Dictionary<int, bool> next)
	{
		await StateGate.WaitAsync().ConfigureAwait(false);
		try
		{
			WasOverThreshold.Clear();
			foreach (var kv in next)
				WasOverThreshold[kv.Key] = kv.Value;
			SaveState();
		}
		finally
		{
			StateGate.Release();
		}
	}

	private static void EnsureStateLoaded()
	{
		if (_loadedState)
			return;

		try
		{
			var raw = Preferences.Get(KeyOverThresholdByRoom, string.Empty);
			if (!string.IsNullOrWhiteSpace(raw))
			{
				var saved = JsonSerializer.Deserialize<Dictionary<int, bool>>(raw);
				if (saved is not null)
				{
					WasOverThreshold.Clear();
					foreach (var kv in saved)
						WasOverThreshold[kv.Key] = kv.Value;
				}
			}
		}
		catch
		{
			WasOverThreshold.Clear();
		}
		finally
		{
			_loadedState = true;
		}
	}

	private static void SaveState()
	{
		try
		{
			var json = JsonSerializer.Serialize(WasOverThreshold);
			Preferences.Set(KeyOverThresholdByRoom, json);
		}
		catch
		{
			// Best effort only.
		}
	}

	private static bool TryGetDisplayableTempC(WiserRoom room, out double c)
	{
		c = 0;
		var tenths = room.CalculatedTemperature ?? room.DisplayedTemperature;
		if (tenths is null)
			return false;

		const int tempErrorSentinel = 2000;
		if (tenths.Value >= tempErrorSentinel)
			return false;

		c = WiserHubClient.FromWiserTemp(tenths.Value);
		if (c < WiserHubClient.TempOffC + 1)
			return false;

		return true;
	}
}
