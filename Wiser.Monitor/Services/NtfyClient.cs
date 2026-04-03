namespace Wiser.Monitor.Services;

public sealed class NtfyClient(HttpClient http, TemperatureStore store)
{
    public Task SendAsync(string topic, string title, string message, CancellationToken ct, string kind = "alert")
        => SendAsync(topic, title, message, ct, tags: null, priority: null, kind);

    public async Task SendAsync(
        string topic,
        string title,
        string message,
        CancellationToken ct,
        string? tags,
        string? priority,
        string kind = "alert")
    {
        var url = $"https://ntfy.sh/{Uri.EscapeDataString(topic)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Title", title);
        req.Headers.TryAddWithoutValidation("Priority", priority ?? "high");
        req.Headers.TryAddWithoutValidation("Tags", tags ?? "thermometer");
        req.Content = new StringContent(message, System.Text.Encoding.UTF8);

        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var sentTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        store.RecordNtfyNotification(sentTs, kind, title, message);
    }
}
