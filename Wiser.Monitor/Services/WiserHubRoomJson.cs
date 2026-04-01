using System.Text.Json;

namespace Wiser.Monitor.Services;

/// <summary>Shared helpers for parsing Wiser <c>/data/domain/</c> room objects (property name variants).</summary>
internal static class WiserHubRoomJson
{
    internal static bool TryGetPropertyIgnoreCase(JsonElement obj, string[] candidateNames, out JsonElement value)
    {
        foreach (var p in obj.EnumerateObject())
        {
            foreach (var name in candidateNames)
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
