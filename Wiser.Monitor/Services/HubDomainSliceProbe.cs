using System.Text;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed record HubDomainSliceProbe(
    string RequestedPath,
    string EffectiveUrl,
    int StatusCode,
    string? Error,
    string RootKind,
    IReadOnlyList<string>? ObjectKeys,
    IReadOnlyDictionary<string, int>? ArrayElementCounts,
    int PayloadBytes);

public static class HubDomainSliceProbeBuilder
{
    public static HubDomainSliceProbe FromResponse(
        string requestedPath,
        string effectiveUrl,
        int statusCode,
        ReadOnlyMemory<byte> body)
    {
        if (statusCode < 200 || statusCode > 299)
        {
            var snippet = TruncateForError(body);
            return new HubDomainSliceProbe(
                requestedPath,
                effectiveUrl,
                statusCode,
                snippet,
                "None",
                null,
                null,
                body.Length);
        }

        if (body.Length == 0)
        {
            return new HubDomainSliceProbe(
                requestedPath,
                effectiveUrl,
                statusCode,
                "empty body",
                "None",
                null,
                null,
                0);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return Summarize(requestedPath, effectiveUrl, statusCode, doc.RootElement, body.Length);
        }
        catch (JsonException ex)
        {
            return new HubDomainSliceProbe(
                requestedPath,
                effectiveUrl,
                statusCode,
                ex.Message,
                "InvalidJson",
                null,
                null,
                body.Length);
        }
    }

    private static HubDomainSliceProbe Summarize(
        string requestedPath,
        string effectiveUrl,
        int statusCode,
        JsonElement root,
        int payloadBytes)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var keys = new List<string>();
                var arrays = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var p in root.EnumerateObject())
                {
                    keys.Add(p.Name);
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        arrays[p.Name] = p.Value.GetArrayLength();
                }

                return new HubDomainSliceProbe(
                    requestedPath,
                    effectiveUrl,
                    statusCode,
                    null,
                    "Object",
                    keys,
                    arrays,
                    payloadBytes);
            }
            case JsonValueKind.Array:
                return new HubDomainSliceProbe(
                    requestedPath,
                    effectiveUrl,
                    statusCode,
                    null,
                    "Array",
                    null,
                    new Dictionary<string, int>(StringComparer.Ordinal) { ["_"] = root.GetArrayLength() },
                    payloadBytes);
            default:
                return new HubDomainSliceProbe(
                    requestedPath,
                    effectiveUrl,
                    statusCode,
                    null,
                    root.ValueKind.ToString(),
                    null,
                    null,
                    payloadBytes);
        }
    }

    private static string? TruncateForError(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
            return null;
        var span = body.Span;
        var take = Math.Min(span.Length, 240);
        return Encoding.UTF8.GetString(span[..take]);
    }
}
