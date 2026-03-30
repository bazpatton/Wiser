using System.Text.Json;
using Wiser.Control.Models;

namespace Wiser.Control.Services;

/// <summary>Persists user-defined room display order (hub room ids).</summary>
public static class RoomOrderStore
{
	private const string Key = "room_order_ids_json";

	public static List<int> LoadOrder()
	{
		var s = Preferences.Get(Key, string.Empty);
		if (string.IsNullOrWhiteSpace(s))
			return [];

		try
		{
			return JsonSerializer.Deserialize<List<int>>(s) ?? [];
		}
		catch
		{
			return [];
		}
	}

	public static void SaveOrder(IReadOnlyList<int> roomIds) =>
		Preferences.Set(Key, JsonSerializer.Serialize(roomIds.ToList()));

	/// <summary>
	/// Applies saved order, then appends any new hub rooms (alphabetically by name).
	/// </summary>
	public static List<int> OrderRoomIds(IReadOnlyDictionary<int, WiserRoom> byId)
	{
		if (byId.Count == 0)
			return [];

		var saved = LoadOrder();
		var ids = new List<int>();
		foreach (var id in saved)
		{
			if (byId.ContainsKey(id) && !ids.Contains(id))
				ids.Add(id);
		}

		foreach (var id in byId.Keys
			         .Where(id => !ids.Contains(id))
			         .OrderBy(id => byId[id].Name, StringComparer.OrdinalIgnoreCase))
			ids.Add(id);

		return ids;
	}

	/// <summary>
	/// Merges a new ordering of the visible subset back into the full global order.
	/// </summary>
	public static List<int> MergeVisibleReorder(IReadOnlyList<int> globalOrder, IReadOnlyList<int> visibleNewOrder)
	{
		if (visibleNewOrder.Count == 0)
			return globalOrder.ToList();

		var visibleSet = visibleNewOrder.ToHashSet();
		var result = new List<int>(globalOrder.Count);
		var consumed = false;

		foreach (var id in globalOrder)
		{
			if (visibleSet.Contains(id))
			{
				if (!consumed)
				{
					foreach (var v in visibleNewOrder)
						result.Add(v);
					consumed = true;
				}
			}
			else
				result.Add(id);
		}

		if (!consumed)
			result.AddRange(visibleNewOrder);

		return result;
	}
}
