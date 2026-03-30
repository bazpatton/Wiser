using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class WiserHubFetch(HttpClient http)
{
    public async Task<DomainPollResult> FetchDomainAsync(MonitorOptions options, CancellationToken ct)
    {
        var url = $"http://{options.WiserIp}/data/domain/";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("SECRET", options.WiserSecret);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return WiserDomainParser.ParseDomain(doc);
    }
}
