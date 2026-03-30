using System.Collections.ObjectModel;
using Wiser.Control.Models;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class ReorderRoomsPage : ContentPage
{
	private bool _loaded;
	private readonly ObservableCollection<ReorderRoomItem> _rooms = [];

	public ReorderRoomsPage()
	{
		InitializeComponent();
		RoomOrderCollection.ItemsSource = _rooms;
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_loaded)
			return;

		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync();
			using var client = new WiserHubClient(conn);
			var domain = await client.RefreshDomainAsync();
			LoadRooms(domain.Room);
			_loaded = true;
			ReorderHintLabel.Text = "Long-press and drag to set order. Saves automatically.";
		}
		catch (Exception ex)
		{
			ReorderHintLabel.Text = $"Could not load rooms: {ex.Message}";
		}
	}

	private void LoadRooms(List<WiserRoom>? rooms)
	{
		_rooms.Clear();
		if (rooms is null || rooms.Count == 0)
		{
			ReorderHintLabel.Text = "No rooms available.";
			return;
		}

		var byId = rooms.ToDictionary(r => r.Id);
		foreach (var id in RoomOrderStore.OrderRoomIds(byId))
		{
			var room = byId[id];
			_rooms.Add(new ReorderRoomItem(room.Id, room.Name ?? $"Room {room.Id}"));
		}
	}

	private void OnReorderCompleted(object? sender, EventArgs e)
	{
		if (_rooms.Count == 0)
			return;

		RoomOrderStore.SaveOrder(_rooms.Select(r => r.RoomId).ToList());
		ReorderHintLabel.Text = "Room order saved.";
	}
}

public sealed record ReorderRoomItem(int RoomId, string Name);
