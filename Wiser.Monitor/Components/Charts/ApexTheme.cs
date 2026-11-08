using ApexCharts;

namespace Wiser.Monitor.Components.Charts;

/// <summary>Shared ApexCharts look &amp; feel. Text, grid and tooltip colours come from app.css (<c>.wiser-apex</c>) so they follow the MudBlazor theme.</summary>
public static class ApexTheme
{
    /// <summary>
    /// Categorical series colours in fixed order (dataviz reference palette, dark steps). Validated for
    /// CVD separation and ≥3:1 contrast on both the light (#ffffff) and dark (#1e293b) chart surfaces
    /// (slot 6 green is 2.96:1 on dark; legend + shared tooltip name every series).
    /// </summary>
    public static readonly string[] Categorical =
    [
        "#3987e5", // blue
        "#d95926", // orange
        "#199e70", // aqua
        "#c98500", // yellow
        "#d55181", // magenta
        "#008300", // green
        "#9085e9", // violet
        "#e66767", // red
    ];

    public const string Blue = "#3987e5";
    public const string Orange = "#d95926";
    public const string Aqua = "#199e70";
    public const string Red = "#e66767";
    public const string Neutral = "#94a3b8";

    /// <summary>Colour for the n-th entity; beyond 8 the hues repeat and the caller adds a dash pattern as the second encoding.</summary>
    public static string ColorAt(int index) => Categorical[index % Categorical.Length];

    /// <summary>Dash length for the n-th entity: solid for the first 8, dashed after.</summary>
    public static int DashAt(int index) => index < Categorical.Length ? 0 : 5;

    /// <summary>Chart ids/groups end up in DOM ids and JS lookups: keep them to [a-z0-9-].</summary>
    public static string SafeId(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public static string TooltipFormatter(string unit, int decimals) =>
        "function(value) { if (value === null || value === undefined || isNaN(value)) return '—';" +
        $" return Number(value).toFixed({decimals}) + '{unit}'; }}";

    /// <summary>Axis ticks use a compact unit: 20°, 50%, but 120 min.</summary>
    public static string AxisUnit(string unit) => unit.Trim() switch
    {
        "°C" => "°",
        "%" => "%",
        _ => unit,
    };

    public static string AxisFormatter(string unit, int decimals) =>
        $"function(value) {{ return Number(value).toFixed({decimals}) + '{unit}'; }}";

    /// <summary>Options shared by every chart: transparent background, no animation, shared crosshair tooltip, zoom toolbar.</summary>
    public static ApexChartOptions<T> Base<T>(string id, string? group, bool timeAxis, bool zoom) where T : class => new()
    {
        Chart = new Chart
        {
            Id = id,
            Group = group,
            Background = "transparent",
            FontFamily = "inherit",
            ForeColor = Neutral, // readable on both themes even before app.css applies
            Animations = new Animations { Enabled = false },
            Toolbar = new Toolbar
            {
                Show = zoom,
                Tools = new Tools { Download = false, Pan = false, Selection = false, Zoom = zoom, Zoomin = zoom, Zoomout = zoom, Reset = zoom },
            },
            Zoom = new Zoom { Enabled = zoom, AutoScaleYaxis = false, AllowMouseWheelZoom = zoom },
        },
        Xaxis = timeAxis
            ? new XAxis
            {
                Type = XAxisType.Datetime,
                Labels = new XAxisLabels
                {
                    DatetimeUTC = true,
                    HideOverlappingLabels = true,
                    DatetimeFormatter = new DatetimeFormatter { Hour = "HH:mm", Day = "ddd d MMM" },
                },
                Tooltip = new XAxisTooltip { Enabled = false },
            }
            : new XAxis { Tooltip = new XAxisTooltip { Enabled = false } },
        Tooltip = new Tooltip
        {
            Shared = true,
            Intersect = false,
            X = timeAxis ? new TooltipX { Format = "ddd d MMM, HH:mm" } : new TooltipX(),
        },
        Grid = new Grid { StrokeDashArray = 3, Padding = new Padding { Left = 8, Right = 8 } },
        Markers = new Markers { Size = 0, Hover = new MarkersHover { Size = 4 } },
        Legend = new Legend { Position = LegendPosition.Top, HorizontalAlign = Align.Left },
        DataLabels = new DataLabels { Enabled = false },
        // Phones: the zoom toolbar would sit on top of a wrapped legend, so move the legend below.
        Responsive =
        [
            new Responsive<T>
            {
                Breakpoint = 600,
                Options = new ApexChartOptions<T>
                {
                    Legend = new Legend { Position = LegendPosition.Bottom, HorizontalAlign = Align.Left },
                },
            },
        ],
    };
}

/// <summary>
/// Shared by the charts in one zoom group. While zoomed, periodic data refreshes are held back so the
/// view isn't reset under the user; a user-driven change (room, window) still redraws immediately.
/// </summary>
public sealed class ChartZoomState
{
    public bool Zoomed { get; set; }
}

/// <summary>One line on a <see cref="TimeLinesChart"/>.</summary>
public sealed record TimeLine(
    string Name,
    IReadOnlyList<Wiser.Monitor.Services.ChartPoint> Points,
    string Color,
    Curve Curve = Curve.Smooth,
    double Width = 2,
    int Dash = 0,
    bool Area = false);

/// <summary>One bar/point on a <see cref="CategoryChart"/>.</summary>
public sealed record CategoryPoint(string Label, decimal? Value);
