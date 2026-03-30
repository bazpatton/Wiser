using System.Text.Json;

namespace Wiser.Control.Services;

public enum UndoKind
{
	None = 0,
	RoomState = 1,
}

public sealed class ActionHistoryEntry
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
	public string Title { get; set; } = "";
	public string Details { get; set; } = "";
	public UndoKind UndoKind { get; set; }
	public int? RoomId { get; set; }
	public string? PrevMode { get; set; }
	public int? PrevSetPointTenths { get; set; }
}

public static class ActionHistoryStore
{
	private const string Key = "action_history_v1";
	private const int MaxEntries = 120;

	public static List<ActionHistoryEntry> Load()
	{
		var raw = Preferences.Get(Key, string.Empty);
		if (string.IsNullOrWhiteSpace(raw))
			return [];

		try
		{
			var entries = JsonSerializer.Deserialize<List<ActionHistoryEntry>>(raw) ?? [];
			return entries
				.OrderByDescending(x => x.At)
				.Take(MaxEntries)
				.ToList();
		}
		catch
		{
			return [];
		}
	}

	public static void Append(ActionHistoryEntry entry)
	{
		var list = Load();
		list.Insert(0, entry);
		Save(list);
	}

	public static void Remove(string id)
	{
		var list = Load();
		list.RemoveAll(x => x.Id == id);
		Save(list);
	}

	public static void Clear() => Preferences.Set(Key, string.Empty);

	private static void Save(IReadOnlyList<ActionHistoryEntry> entries)
	{
		var trimmed = entries.Take(MaxEntries).ToList();
		Preferences.Set(Key, JsonSerializer.Serialize(trimmed));
	}
}
