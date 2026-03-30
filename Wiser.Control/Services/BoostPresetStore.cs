using System.Text.Json;

namespace Wiser.Control.Services;

public sealed class BoostPreset
{
	public string Name { get; set; } = "";
	public double TemperatureC { get; set; }
	public int Minutes { get; set; }
}

public static class BoostPresetStore
{
	private const string Key = "boost_presets_v1";

	public static IReadOnlyList<BoostPreset> Load()
	{
		var raw = Preferences.Get(Key, string.Empty);
		if (string.IsNullOrWhiteSpace(raw))
			return DefaultPresets();

		try
		{
			var list = JsonSerializer.Deserialize<List<BoostPreset>>(raw);
			if (list is null || list.Count == 0)
				return DefaultPresets();
			return Normalize(list);
		}
		catch
		{
			return DefaultPresets();
		}
	}

	public static void Save(IReadOnlyList<BoostPreset> presets)
	{
		var normalized = Normalize(presets);
		Preferences.Set(Key, JsonSerializer.Serialize(normalized));
	}

	private static List<BoostPreset> DefaultPresets() =>
	[
		new() { Name = "Quick", TemperatureC = 21, Minutes = 30 },
		new() { Name = "Comfort", TemperatureC = 22, Minutes = 60 },
		new() { Name = "Gentle", TemperatureC = 20, Minutes = 45 },
	];

	private static List<BoostPreset> Normalize(IReadOnlyList<BoostPreset> presets)
	{
		var normalized = new List<BoostPreset>();
		foreach (var p in presets)
		{
			var name = string.IsNullOrWhiteSpace(p.Name) ? "Boost" : p.Name.Trim();
			var temp = Math.Clamp(Math.Round(p.TemperatureC, 1), WiserHubClient.TempMinimumC, WiserHubClient.TempMaximumC);
			var minutes = Math.Clamp(p.Minutes, 5, 240);
			normalized.Add(new BoostPreset { Name = name, TemperatureC = temp, Minutes = minutes });
		}

		if (normalized.Count == 0)
			return DefaultPresets();

		return normalized;
	}
}
