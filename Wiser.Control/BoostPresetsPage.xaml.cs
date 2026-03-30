using System.Collections.ObjectModel;
using Microsoft.Maui.Controls.Shapes;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class BoostPresetsPage : ContentPage
{
	private readonly ObservableCollection<BoostPresetEditorItem> _items = [];

	public BoostPresetsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		LoadPresets();
	}

	private void LoadPresets()
	{
		_items.Clear();
		foreach (var p in BoostPresetStore.Load())
		{
			_items.Add(new BoostPresetEditorItem
			{
				Name = p.Name,
				TemperatureText = p.TemperatureC.ToString("0.#"),
				MinutesText = p.Minutes.ToString(),
			});
		}

		if (_items.Count == 0)
			_items.Add(new BoostPresetEditorItem { Name = "Boost", TemperatureText = "21", MinutesText = "30" });

		BuildPresetRows();
	}

	private void BuildPresetRows()
	{
		PresetHost.Children.Clear();
		for (var i = 0; i < _items.Count; i++)
		{
			var item = _items[i];
			PresetHost.Children.Add(BuildRow(item, i + 1));
		}

		var add = new Button { Text = "Add preset", Style = (Style)Application.Current!.Resources["OutlineButtonStyle"] };
		add.Clicked += (_, _) =>
		{
			_items.Add(new BoostPresetEditorItem { Name = $"Preset {_items.Count + 1}", TemperatureText = "21", MinutesText = "30" });
			BuildPresetRows();
		};
		PresetHost.Children.Add(add);

		var save = new Button { Text = "Save presets" };
		save.Clicked += OnSaveClicked;
		PresetHost.Children.Add(save);
	}

	private View BuildRow(BoostPresetEditorItem item, int idx)
	{
		var title = new Label
		{
			Text = $"Preset {idx}",
			FontSize = 12,
			FontAttributes = FontAttributes.Bold,
		};
		ThemePresetTitle(title);

		var name = new Entry { Placeholder = "Name", Text = item.Name };
		name.TextChanged += (_, e) => item.Name = e.NewTextValue ?? "";

		var temp = new Entry { Placeholder = "Temp °C", Keyboard = Keyboard.Numeric, Text = item.TemperatureText };
		temp.TextChanged += (_, e) => item.TemperatureText = e.NewTextValue ?? "";

		var mins = new Entry { Placeholder = "Minutes", Keyboard = Keyboard.Numeric, Text = item.MinutesText };
		mins.TextChanged += (_, e) => item.MinutesText = e.NewTextValue ?? "";

		var remove = new Button
		{
			Text = "Remove",
			Style = (Style)Application.Current!.Resources["OutlineButtonStyle"],
			IsEnabled = _items.Count > 1,
		};
		remove.Clicked += (_, _) =>
		{
			_items.Remove(item);
			BuildPresetRows();
		};

		var grid = new Grid
		{
			ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) },
			ColumnSpacing = 8,
		};
		Grid.SetColumn(temp, 0);
		Grid.SetColumn(mins, 1);
		grid.Children.Add(temp);
		grid.Children.Add(mins);

		var border = new Border
		{
			Padding = new Thickness(14, 12),
			StrokeThickness = 1,
			StrokeShape = new RoundRectangle { CornerRadius = 14 },
			Content = new VerticalStackLayout
			{
				Spacing = 8,
				Children =
				{
					title,
					name,
					grid,
					remove,
				},
			},
		};
		ThemePresetCard(border);
		return border;
	}

	private static void ThemePresetCard(Border border)
	{
		var r = Application.Current!.Resources;
		border.SetAppThemeColor(Border.BackgroundColorProperty,
			(Color)r["CardBackgroundLight"],
			(Color)r["CardBackgroundDark"]);
		border.SetAppTheme(Border.StrokeProperty,
			new SolidColorBrush((Color)r["CardStrokeLight"]),
			new SolidColorBrush((Color)r["CardStrokeDark"]));
	}

	private static void ThemePresetTitle(Label label)
	{
		var r = Application.Current!.Resources;
		label.SetAppThemeColor(Label.TextColorProperty,
			(Color)r["SubtleTextLight"],
			(Color)r["SubtleTextDark"]);
	}

	private async void OnSaveClicked(object? sender, EventArgs e)
	{
		var list = new List<BoostPreset>();
		foreach (var item in _items)
		{
			if (!double.TryParse(item.TemperatureText, out var temp))
				temp = 21;
			if (!int.TryParse(item.MinutesText, out var mins))
				mins = 30;

			list.Add(new BoostPreset
			{
				Name = string.IsNullOrWhiteSpace(item.Name) ? "Boost" : item.Name,
				TemperatureC = temp,
				Minutes = mins,
			});
		}

		BoostPresetStore.Save(list);
		await DisplayAlert("Boost presets", "Saved.", "OK");
	}
}

public sealed class BoostPresetEditorItem
{
	public string Name { get; set; } = "";
	public string TemperatureText { get; set; } = "";
	public string MinutesText { get; set; } = "";
}
