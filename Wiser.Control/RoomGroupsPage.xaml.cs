using Wiser.Control.Models;
using Wiser.Control.Services;
using Microsoft.Maui.Controls.Shapes;

namespace Wiser.Control;

public partial class RoomGroupsPage : ContentPage
{
	private readonly List<RoomInfoItem> _rooms = [];
	private readonly List<RoomGroupEditorItem> _groups = [];

	public RoomGroupsPage()
	{
		InitializeComponent();
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await LoadAsync();
	}

	private async Task LoadAsync()
	{
		HintLabel.Text = "Loading rooms...";
		try
		{
			var conn = await WiserKeys.LoadFromAppPackageAsync();
			using var client = new WiserHubClient(conn);
			var payload = await client.RefreshDomainAsync();

			_rooms.Clear();
			foreach (var room in (payload.Room ?? []).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
				_rooms.Add(new RoomInfoItem(room.Id, room.Name ?? $"Room {room.Id}"));

			_groups.Clear();
			var existing = RoomGroupsStore.Load();
			if (existing.Count == 0)
			{
				_groups.Add(new RoomGroupEditorItem
				{
					Id = Guid.NewGuid().ToString("N"),
					Name = "Group 1",
					SelectedRoomIds = [],
				});
			}
			else
			{
				foreach (var g in existing)
					_groups.Add(new RoomGroupEditorItem { Id = g.Id, Name = g.Name, SelectedRoomIds = g.RoomIds.ToHashSet() });
			}

			BuildGroupRows();
			HintLabel.Text = "Manage groups, then tap Save groups.";
		}
		catch (Exception ex)
		{
			HintLabel.Text = $"Could not load rooms: {ex.Message}";
		}
	}

	private void BuildGroupRows()
	{
		GroupHost.Children.Clear();
		for (var i = 0; i < _groups.Count; i++)
		{
			GroupHost.Children.Add(BuildGroupCard(_groups[i], i + 1));
		}

		var addButton = new Button
		{
			Text = "Add group",
			Style = (Style)Application.Current!.Resources["OutlineButtonStyle"],
		};
		addButton.Clicked += (_, _) =>
		{
			_groups.Add(new RoomGroupEditorItem
			{
				Id = Guid.NewGuid().ToString("N"),
				Name = $"Group {_groups.Count + 1}",
				SelectedRoomIds = [],
			});
			BuildGroupRows();
		};
		GroupHost.Children.Add(addButton);

		var saveButton = new Button { Text = "Save groups" };
		saveButton.Clicked += OnSaveClicked;
		GroupHost.Children.Add(saveButton);
	}

	private View BuildGroupCard(RoomGroupEditorItem editor, int index)
	{
		var nameEntry = new Entry
		{
			Text = editor.Name,
			Placeholder = $"Group {index}",
			TextColor = PrimaryTextColor(),
			PlaceholderColor = SubtleTextColor(),
		};
		nameEntry.TextChanged += (_, e) => editor.Name = e.NewTextValue ?? "";
		var selectedSummary = new Label
		{
			Text = $"{editor.SelectedRoomIds.Count} selected",
			FontSize = 12,
			TextColor = SubtleTextColor(),
		};

		var removeButton = new Button
		{
			Text = "Delete group",
			Style = (Style)Application.Current!.Resources["OutlineButtonStyle"],
			IsEnabled = _groups.Count > 1,
		};
		removeButton.Clicked += (_, _) =>
		{
			_groups.Remove(editor);
			BuildGroupRows();
		};

		var roomChecks = new VerticalStackLayout
		{
			Spacing = 6,
			IsVisible = editor.IsExpanded,
		};
		foreach (var room in _rooms)
		{
			var check = new CheckBox { IsChecked = editor.SelectedRoomIds.Contains(room.Id) };
			check.CheckedChanged += (_, args) =>
			{
				if (args.Value)
					editor.SelectedRoomIds.Add(room.Id);
				else
					editor.SelectedRoomIds.Remove(room.Id);

				selectedSummary.Text = $"{editor.SelectedRoomIds.Count} selected";
			};

			roomChecks.Children.Add(new HorizontalStackLayout
			{
				Spacing = 8,
				Children =
				{
					check,
					new Label
					{
						Text = room.Name,
						VerticalOptions = LayoutOptions.Center,
						FontSize = 14,
						TextColor = PrimaryTextColor(),
					},
				},
			});
		}

		var toggleRoomsButton = new Button
		{
			Text = editor.IsExpanded
				? $"Hide rooms ({editor.SelectedRoomIds.Count}/{_rooms.Count})"
				: $"Show rooms ({editor.SelectedRoomIds.Count}/{_rooms.Count})",
			Style = (Style)Application.Current!.Resources["OutlineButtonStyle"],
		};
		toggleRoomsButton.Clicked += (_, _) =>
		{
			editor.IsExpanded = !editor.IsExpanded;
			BuildGroupRows();
		};

		return new Border
		{
			Padding = new Thickness(14, 12),
			BackgroundColor = CardBackgroundColor(),
			Stroke = CardStrokeColor(),
			StrokeThickness = 1,
			StrokeShape = new RoundRectangle { CornerRadius = 14 },
			Content = new VerticalStackLayout
			{
				Spacing = 10,
				Children =
				{
					new Label
					{
						Text = $"Group {index}",
						FontSize = 12,
						FontAttributes = FontAttributes.Bold,
						TextColor = SubtleTextColor(),
					},
					nameEntry,
					new Label
					{
						Text = "Rooms",
						FontSize = 12,
						FontAttributes = FontAttributes.Bold,
						TextColor = SubtleTextColor(),
					},
					selectedSummary,
					toggleRoomsButton,
					roomChecks,
					removeButton,
				},
			},
		};
	}

	private static bool IsDarkTheme() =>
		Application.Current?.RequestedTheme == AppTheme.Dark;

	private static Color CardBackgroundColor() =>
		(Color)Application.Current!.Resources[IsDarkTheme() ? "CardBackgroundDark" : "CardBackgroundLight"];

	private static Color CardStrokeColor() =>
		(Color)Application.Current!.Resources[IsDarkTheme() ? "CardStrokeDark" : "CardStrokeLight"];

	private static Color PrimaryTextColor() =>
		(Color)Application.Current!.Resources[IsDarkTheme() ? "White" : "MidnightBlue"];

	private static Color SubtleTextColor() =>
		(Color)Application.Current!.Resources[IsDarkTheme() ? "SubtleTextDark" : "SubtleTextLight"];

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		var result = new List<RoomGroup>();
		foreach (var g in _groups)
		{
			result.Add(new RoomGroup
			{
				Id = g.Id,
				Name = string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name.Trim(),
				RoomIds = g.SelectedRoomIds.ToList(),
			});
		}

		RoomGroupsStore.Save(result);
		await DisplayAlert("Room groups", "Saved.", "OK");
	}
}

internal sealed record RoomInfoItem(int Id, string Name);

internal sealed class RoomGroupEditorItem
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string Name { get; set; } = "";
	public HashSet<int> SelectedRoomIds { get; set; } = [];
	public bool IsExpanded { get; set; }
}
