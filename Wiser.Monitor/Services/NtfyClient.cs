namespace Wiser.Monitor.Services;

public sealed class NtfyClient(HttpClient http)
{
    public Task SendAsync(string topic, string title, string message, CancellationToken ct)
        => SendAsync(topic, title, message, ct, tags: null, priority: null);

    public async Task SendAsync(
        string topic,
        string title,
        string message,
        CancellationToken ct,
        string? tags,
        string? priority)
    {
        var url = $"https://ntfy.sh/{Uri.EscapeDataString(topic)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Title", title);
        req.Headers.TryAddWithoutValidation("Priority", priority ?? "high");
        req.Headers.TryAddWithoutValidation("Tags", tags ?? "thermometer");
        req.Content = new StringContent(message, System.Text.Encoding.UTF8);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}
