using Wiser.Control.Models;

namespace Wiser.Control.Services;

public sealed class WidgetStatusSnapshot
{
	public string Title { get; set; } = "Wiser";
	public string Subtitle { get; set; } = "No data yet";
	public string Meta { get; set; } = "Open app to sync";
}

public static class WidgetStatusStore
{
	private const string KeyTitle = "widget_status_title";
	private const string KeySubtitle = "widget_status_subtitle";
	private const string KeyMeta = "widget_status_meta";

	public static void SaveFromDomain(WiserDomainPayload payload, DateTimeOffset fetchedAtLocal)
	{
		var rooms = payload.Room?.Count ?? 0;
		var title = payload.IsHeatingActive() ? "Heating: On" : "Heating: Off";
		var subtitle = $"{rooms} room(s)";
		var meta = $"Updated {fetchedAtLocal:t}";

		Preferences.Set(KeyTitle, title);
		Preferences.Set(KeySubtitle, subtitle);
		Preferences.Set(KeyMeta, meta);
	}

	public static void SaveError(string message)
	{
		Preferences.Set(KeyTitle, "Wiser");
		Preferences.Set(KeySubtitle, "Hub unavailable");
		Preferences.Set(KeyMeta, message.Length > 48 ? message[..48] : message);
	}

	public static WidgetStatusSnapshot Load() => new()
	{
		Title = Preferences.Get(KeyTitle, "Wiser"),
		Subtitle = Preferences.Get(KeySubtitle, "No data yet"),
		Meta = Preferences.Get(KeyMeta, "Open app to sync"),
	};
}
