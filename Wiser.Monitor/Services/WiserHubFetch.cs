using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class WiserHubFetch(HttpClient http)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

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

    /// <summary>Wiser boost / timed manual — same JSON as Wiser.Control <c>SetRoomModeAsync(..., "boost", ...)</c>.</summary>
    public async Task PatchRoomBoostAsync(MonitorOptions options, int roomId, double temperatureC, int minutes, CancellationToken ct)
    {
        const int tempMin = 5;
        const int tempMax = 30;
        if (roomId <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomId));
        temperatureC = Math.Clamp(Math.Round(temperatureC, 1), tempMin, tempMax);
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
