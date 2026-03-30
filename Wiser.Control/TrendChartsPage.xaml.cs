using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Wiser.Control.Services;

namespace Wiser.Control;

public partial class TrendChartsPage : ContentPage
{
	public TrendChartsPage()
	{
		InitializeComponent();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		BuildCards();
	}

	private void BuildCards()
	{
		CardsHost.Children.Clear();
		var all = TemperatureTrendStore.LoadLast24HourSeries();
		if (all.Count == 0)
		{
			HintLabel.Text = "No trend data yet. Refresh from the main screen to capture samples.";
			return;
		}

		HintLabel.Text = "Measured (orange) vs target (blue), last 24 hours.";
		foreach (var series in all)
			CardsHost.Children.Add(BuildCard(series));
	}

	private static View BuildCard(RoomTrendSeries series)
	{
		var latestText = series.LatestSampleUtc is null
			? "No recent sample"
			: $"Last sample {series.LatestSampleUtc.Value.ToLocalTime():t}";
		var valueText = $"Now {Fmt(series.LatestMeasuredC)} / Target {Fmt(series.LatestTargetC)}";

		var nameLabel = new Label
		{
			Text = series.RoomName,
			FontSize = 16,
			FontAttributes = FontAttributes.Bold,
			TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Color.FromArgb("#2C2C2C"),
		};

		var metaLabel = new Label
		{
			Text = $"{latestText} - {valueText}",
			FontSize = 12,
			TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#B8B3AD") : Color.FromArgb("#5C5650"),
		};

		var graph = new GraphicsView
		{
			HeightRequest = 120,
			Margin = new Thickness(0, 8, 0, 4),
			Drawable = new RoomTrendDrawable(series.Buckets),
		};

		var legend = new HorizontalStackLayout
		{
			Spacing = 10,
			Children =
			{
				LegendPill("Measured", Color.FromArgb("#D3542C")),
				LegendPill("Target", Color.FromArgb("#4A7FC4")),
			}
		};

		return new Border
		{
			Padding = new Thickness(14, 12),
			BackgroundColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#26272C") : Colors.White,
			Stroke = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#3D3E45") : Color.FromArgb("#E5DED6"),
			StrokeThickness = 1,
			StrokeShape = new RoundRectangle { CornerRadius = 14 },
			Content = new VerticalStackLayout
			{
				Spacing = 0,
				Children =
				{
					nameLabel,
					metaLabel,
					graph,
					legend,
				}
			}
		};
	}

	private static View LegendPill(string text, Color color) =>
		new HorizontalStackLayout
		{
			Spacing = 5,
			Children =
			{
				new BoxView
				{
					WidthRequest = 12,
					HeightRequest = 12,
					CornerRadius = 6,
					Color = color,
					VerticalOptions = LayoutOptions.Center
				},
				new Label
				{
					Text = text,
					FontSize = 12,
					TextColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#B8B3AD") : Color.FromArgb("#5C5650")
				}
			}
		};

	private static string Fmt(double? temp) => temp is null ? "--" : $"{temp.Value:0.#} C";
}

internal sealed class RoomTrendDrawable(IReadOnlyList<TrendBucket> buckets) : IDrawable
{
	private readonly IReadOnlyList<TrendBucket> _buckets = buckets;

	public void Draw(ICanvas canvas, RectF dirtyRect)
	{
		canvas.SaveState();
		try
		{
			var left = 8f;
			var right = dirtyRect.Width - 8f;
			var top = 6f;
			var bottom = dirtyRect.Height - 6f;
			if (right <= left || bottom <= top)
				return;

			canvas.StrokeColor = Color.FromArgb("#D9D5CF");
			canvas.StrokeSize = 1;
			for (var i = 0; i <= 4; i++)
			{
				var y = top + ((bottom - top) * i / 4f);
				canvas.DrawLine(left, y, right, y);
			}

			var values = _buckets
				.SelectMany(b => new[] { b.MeasuredC, b.TargetC })
				.Where(v => v.HasValue)
				.Select(v => (float)v!.Value)
				.ToList();
			if (values.Count == 0 || _buckets.Count < 2)
				return;

			var minV = values.Min();
			var maxV = values.Max();
			if (maxV - minV < 0.8f)
			{
				maxV += 0.4f;
				minV -= 0.4f;
			}

			DrawLine(canvas, left, right, top, bottom, minV, maxV, b => b.MeasuredC, Color.FromArgb("#D3542C"));
			DrawLine(canvas, left, right, top, bottom, minV, maxV, b => b.TargetC, Color.FromArgb("#4A7FC4"));
		}
		finally
		{
			canvas.RestoreState();
		}
	}

	private void DrawLine(ICanvas canvas, float left, float right, float top, float bottom, float minV, float maxV, Func<TrendBucket, double?> selector, Color color)
	{
		var points = new List<PointF>();
		for (var i = 0; i < _buckets.Count; i++)
		{
			var v = selector(_buckets[i]);
			if (!v.HasValue)
				continue;

			var x = left + (right - left) * i / (_buckets.Count - 1f);
			var norm = ((float)v.Value - minV) / (maxV - minV);
			var y = bottom - norm * (bottom - top);
			points.Add(new PointF(x, y));
		}

		if (points.Count < 2)
			return;

		canvas.StrokeColor = color;
		canvas.StrokeSize = 2;
		for (var i = 1; i < points.Count; i++)
			canvas.DrawLine(points[i - 1], points[i]);
	}
}
