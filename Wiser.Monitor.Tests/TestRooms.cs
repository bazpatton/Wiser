namespace Wiser.Monitor.Tests;

/// <summary>Builders for hub room records used across tests.</summary>
internal static class TestRooms
{
    public static HubLiveRoom Room(
        int id = 1,
        string name = "Living room",
        double tempC = 20.6,
        double? setpointC = 21,
        bool callingHeat = true,
        long? overrideTimeoutUnix = null,
        long? nextChangeUnix = null) =>
        new(
            id,
            name,
            "Auto",
            tempC,
            setpointC,
            callingHeat ? 1 : 0,
            callingHeat ? "on" : "off",
            callingHeat ? 30 : 0,
            overrideTimeoutUnix,
            null,
            null,
            nextChangeUnix);
}
