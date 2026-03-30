using System.Text.Json;
using Wiser.Control.Models;

namespace Wiser.Control.Services;

public sealed class RoomGroup
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public string Name { get; set; } = "Group";
	public List<int> RoomIds { get; set; } = [];
}

public static class RoomGroupsStore
{
	private const string KeyGroups = "room_groups_v1";
	private const string KeyMigrated = "room_groups_migrated_v1";

	public static List<RoomGroup> Load()
	{
		var raw = Preferences.Get(KeyGroups, string.Empty);
		if (string.IsNullOrWhiteSpace(raw))
			return [];

		try
		{
			var groups = JsonSerializer.Deserialize<List<RoomGroup>>(raw) ?? [];
			return Normalize(groups);
		}
		catch
		{
			return [];
		}
	}

	public static void Save(IReadOnlyList<RoomGroup> groups)
	{
		var normalized = Normalize(groups);
		Preferences.Set(KeyGroups, JsonSerializer.Serialize(normalized));
	}

	public static bool EnsureMigratedFromChannels(List<WiserHeatingChannel>? channels)
	{
		if (Preferences.Get(KeyMigrated, false))
			return false;

		if (Load().Count > 0)
		{
			Preferences.Set(KeyMigrated, true);
			return false;
		}

		var defaults = new List<RoomGroup>();
		foreach (var ch in channels ?? [])
		{
			if (ch.RoomIds is null || ch.RoomIds.Count == 0)
				continue;

			var name = ch.Id switch
			{
				1 => "Downstairs",
				3 => "Upstairs",
				_ => $"Channel {ch.Id}",
			};

			defaults.Add(new RoomGroup
			{
				Id = $"channel_{ch.Id}",
				Name = name,
				RoomIds = ch.RoomIds.Distinct().ToList(),
			});
		}

		if (defaults.Count > 0)
			Save(defaults);

		Preferences.Set(KeyMigrated, true);
		return defaults.Count > 0;
	}

	private static List<RoomGroup> Normalize(IReadOnlyList<RoomGroup> groups)
	{
		var result = new List<RoomGroup>();
		var seenIds = new HashSet<string>(StringComparer.Ordinal);

		foreach (var g in groups)
		{
			var id = string.IsNullOrWhiteSpace(g.Id) ? Guid.NewGuid().ToString("N") : g.Id.Trim();
			if (!seenIds.Add(id))
				continue;

			var name = string.IsNullOrWhiteSpace(g.Name) ? "Group" : g.Name.Trim();
			var roomIds = (g.RoomIds ?? []).Distinct().ToList();
			result.Add(new RoomGroup { Id = id, Name = name, RoomIds = roomIds });
		}

		return result;
	}
}
