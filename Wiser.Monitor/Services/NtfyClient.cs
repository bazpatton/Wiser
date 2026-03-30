namespace Wiser.Monitor.Services;

public sealed class NtfyClient(HttpClient http)
{
    public async Task SendAsync(string topic, string title, string message, CancellationToken ct)
    {
        var url = $"https://ntfy.sh/{Uri.EscapeDataString(topic)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Title", title);
        req.Headers.TryAddWithoutValidation("Priority", "high");
        req.Headers.TryAddWithoutValidation("Tags", "thermometer");
        req.Content = new StringContent(message, System.Text.Encoding.UTF8);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}
