namespace Wiser.Monitor.Services;

public sealed record FloorplanRoomPin(
    string Room,
    double XPercent,
    double YPercent);

public sealed record FloorplanConfig(
    string? ImageFileName,
    string? ImageContentType,
    long UpdatedTs,
    IReadOnlyList<FloorplanRoomPin> Pins)
{
    public static FloorplanConfig Default =>
        new(
            ImageFileName: null,
            ImageContentType: null,
            UpdatedTs: 0,
            Pins: []);
}
