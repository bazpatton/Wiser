using Wiser.Control.Models;

namespace Wiser.Control.Services;

public static class ActionUndoService
{
	public static async Task<bool> TryUndoAsync(WiserHubClient client, WiserDomainPayload snapshot, ActionHistoryEntry entry)
	{
		if (entry.UndoKind != UndoKind.RoomState || entry.RoomId is null)
			return false;

		var roomId = entry.RoomId.Value;
		var prevMode = entry.PrevMode ?? "Manual";
		var prevSetPointTenths = entry.PrevSetPointTenths ?? (WiserHubClient.TempMinimumC * 10);

		if (string.Equals(prevMode, "Auto", StringComparison.OrdinalIgnoreCase))
		{
			await client.SetRoomModeAsync(snapshot, roomId, "auto");
			return true;
		}

		var prevC = WiserHubClient.FromWiserTemp(prevSetPointTenths);
		if (prevC <= WiserHubClient.TempOffC + 0.1)
		{
			await client.SetRoomModeAsync(snapshot, roomId, "off");
			return true;
		}

		await client.SetRoomTemperatureAsync(roomId, prevC);
		return true;
	}
}
