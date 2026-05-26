namespace Wiser.Monitor.Services;

public sealed record RoomSampleCount(string Room, int SamplesSince);

public sealed record StorageDiagnostics(
    string DatabasePath,
    long DatabaseBytes,
    int DistinctRooms,
    int TotalRoomReadings,
    int RoomReadingsSince,
    long? OldestReadingUnix,
    long? NewestReadingUnix,
    IReadOnlyList<RoomSampleCount> TopRoomSamplesSince);
