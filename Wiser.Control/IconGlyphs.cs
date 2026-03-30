namespace Wiser.Control;

/// <summary>
/// Fluent: <see href="https://github.com/microsoft/fluentui-system-icons">Fluent UI System Icons</see> (FluentSystemIcons-Filled.ttf), aligned with Segoe Fluent icon code chart.
/// Font Awesome: solid glyphs (fa-solid-900.ttf), <see href="https://fontawesome.com/license/free">Font Awesome Free license</see>.
/// </summary>
public static class IconGlyphs
{
	// Fluent System Icons (filled) — common Windows/Segoe Fluent codepoints
	public const string FluentSettings = "\uE713";
	public const string FluentRefresh = "\uE72C";
	public const string FluentHome = "\uE80F";

	// Font Awesome 6 Free — solid
	public const string FaDoorOpen = "\uF52B";
	public const string FaUndo = "\uF2EA";
	public const string FaCheck = "\uF00C";
	public const string FaSliders = "\uF1DE";

	/// <summary>Radiator / demand: calling for heat.</summary>
	public const string FaFire = "\uF06D";

	/// <summary>Radiator idle: not demanding heat.</summary>
	public const string FaTemperatureLow = "\uF76B";

	/// <summary>Radiator state unknown from hub.</summary>
	public const string FaTemperatureHalf = "\uF2C9";
}
