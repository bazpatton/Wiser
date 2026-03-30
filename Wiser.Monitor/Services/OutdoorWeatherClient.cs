using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class OutdoorWeatherClient(HttpClient http)
{
    public async Task<double?> GetCurrentTempCAsync(double lat, double lon, CancellationToken ct)
    {
        var uri = new Uri(
            $"https://api.open-meteo.com/v1/forecast?latitude={Uri.EscapeDataString(lat.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
            $"&longitude={Uri.EscapeDataString(lon.ToString(System.Globalization.CultureInfo.InvariantCulture))}" +
            "&current=temperature_2m");

        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("current", out var cur) ||
            !cur.TryGetProperty("temperature_2m", out var t))
            return null;
        return t.GetDouble();
    }
}
