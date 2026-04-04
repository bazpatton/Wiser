namespace Wiser.Monitor.Services;

/// <summary>Stable slug and anchor id for room deep links (<c>/rooms/{slug}</c> and <c>id="room-{slug}"</c>).</summary>
public static class RoomLinkHelper
{
    public static string PathSlug(string roomName)
    {
        var chars = roomName.Trim().ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    public static string AnchorId(string roomName) => $"room-{PathSlug(roomName)}";

    /// <summary>App-relative path, e.g. <c>/rooms/living-room</c>.</summary>
    public static string RoomsPagePath(string roomName) => $"/rooms/{PathSlug(roomName)}";
}
