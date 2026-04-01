using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class WiserHubFetch(HttpClient http)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const int TempMinimumC = 5;
    private const int TempMaximumC = 30;
    private const int TempOffC = -20;

    private static readonly JsonSerializerOptions PatchJson = new()
    {
        PropertyNamingPolicy = null,
    };

    public async Task<DomainPollResult> FetchDomainAsync(MonitorOptions options, CancellationToken ct)
    {
        using var doc = await FetchDomainDocumentAsync(options, ct).ConfigureAwait(false);
        return WiserDomainParser.ParseDomain(doc);
    }

    public async Task<JsonDocument> FetchDomainDocumentAsync(MonitorOptions options, CancellationToken ct)
    {
        var url = $"http://{options.WiserIp}/data/domain/";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("SECRET", options.WiserSecret);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    /// <summary>
    /// GET a sub-path under <c>/data/domain/</c> (e.g. <c>Device/</c>, <c>RoomStat/</c>).
    /// Tries with and without a trailing slash when the first attempt returns 404.
    /// </summary>
    public async Task<HubDomainSliceProbe> ProbeDomainSliceAsync(MonitorOptions options, string relativePath, CancellationToken ct)
    {
        var basePart = relativePath.Trim().TrimStart('/');
        var attempts = new List<string>();
        if (!string.IsNullOrEmpty(basePart))
            attempts.Add(basePart);
        if (!string.IsNullOrEmpty(basePart) && !basePart.EndsWith('/'))
            attempts.Add(basePart + "/");
        if (!string.IsNullOrEmpty(basePart) && basePart.EndsWith('/'))
            attempts.Add(basePart.TrimEnd('/'));
        attempts = attempts.Distinct(StringComparer.Ordinal).ToList();

        HubDomainSliceProbe? lastProbe = null;
        foreach (var p in attempts)
        {
            var url = $"http://{options.WiserIp}/data/domain/{p}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("SECRET", options.WiserSecret);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var probe = HubDomainSliceProbeBuilder.FromResponse(relativePath, url, code, bytes);
            if (code is >= 200 and <= 299)
                return probe;

            lastProbe = probe;
            if (code != 404)
                return probe;
        }

        return lastProbe
            ?? new HubDomainSliceProbe(relativePath, $"http://{options.WiserIp}/data/domain/", 0, "no attempt", "None", null, null, 0);
    }

    /// <summary>Wiser boost / timed manual — same JSON as Wiser.Control <c>SetRoomModeAsync(..., "boost", ...)</c>.</summary>
    public async Task PatchRoomBoostAsync(MonitorOptions options, int roomId, double temperatureC, int minutes, CancellationToken ct)
    {
        if (roomId <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomId));
        temperatureC = Math.Clamp(Math.Round(temperatureC, 1), TempMinimumC, TempMaximumC);
        minutes = Math.Clamp(minutes, 5, 240);

        var setPoint = (int)Math.Round(temperatureC * 10, MidpointRounding.AwayFromZero);
        var body = new
        {
            RequestOverride = new
            {
                Type = "Manual",
                DurationMinutes = minutes,
                SetPoint = setPoint,
                Originator = "App",
            },
        };

        await PatchRoomAsync(options, roomId, body, ct).ConfigureAwait(false);
    }

    public async Task PatchRoomModeAsync(MonitorOptions options, int roomId, string mode, double? manualTemperatureC, CancellationToken ct)
    {
        if (roomId <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomId));
        if (string.IsNullOrWhiteSpace(mode))
            throw new ArgumentException("mode is required", nameof(mode));

        var m = mode.Trim().ToLowerInvariant();
        switch (m)
        {
            case "auto":
                await PatchRoomAsync(options, roomId, new
                {
                    RequestOverride = new { Type = "None", DurationMinutes = 0, SetPoint = 0, Originator = "App" },
                }, ct).ConfigureAwait(false);
                await PatchRoomAsync(options, roomId, new { Mode = "Auto" }, ct).ConfigureAwait(false);
                break;
            case "manual":
            {
                var t = manualTemperatureC ?? 20;
                t = Math.Clamp(Math.Round(t, 1), TempMinimumC, TempMaximumC);
                await PatchRoomAsync(options, roomId, new { Mode = "Manual" }, ct).ConfigureAwait(false);
                await PatchRoomAsync(options, roomId, new
                {
                    RequestOverride = new { Type = "Manual", SetPoint = (int)Math.Round(t * 10, MidpointRounding.AwayFromZero) },
                }, ct).ConfigureAwait(false);
                break;
            }
            case "off":
                await PatchRoomAsync(options, roomId, new { Mode = "Manual" }, ct).ConfigureAwait(false);
                await PatchRoomAsync(options, roomId, new
                {
                    RequestOverride = new { Type = "Manual", SetPoint = TempOffC * 10 },
                }, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("mode must be auto, manual, or off", nameof(mode));
        }
    }

    private async Task PatchRoomAsync(MonitorOptions options, int roomId, object body, CancellationToken ct)
    {
        var url = $"http://{options.WiserIp}/data/domain/Room/{roomId}";
        using var req = new HttpRequestMessage(HttpMethod.Patch, url);
        req.Headers.TryAddWithoutValidation("SECRET", options.WiserSecret);
        var json = JsonSerializer.Serialize(body, PatchJson);
        var bytes = Utf8NoBom.GetBytes(json);
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Wiser hub returned {(int)resp.StatusCode}: {text}");
        }
    }
}
