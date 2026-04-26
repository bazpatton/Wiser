using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wiser.Monitor.Services;

public sealed class WiserHubFetch(HttpClient http)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const int TempMinimumC = 5;
    private const int TempMaximumC = 30;
    private const int TempOffC = (int)WiserSetpointDisplay.HubOffSentinelC;

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

    /// <summary>
    /// Clears a timed <c>RequestOverride</c> (boost / “heat until”, etc.) and sets the room back to <c>Auto</c> so the schedule applies again.
    /// Use when the hub still has an override while mode reads Auto (choosing Auto in the menu is a no-op).
    /// </summary>
    public async Task PatchRoomCancelTimedOverrideAsync(MonitorOptions options, int roomId, CancellationToken ct)
    {
        if (roomId <= 0)
            throw new ArgumentOutOfRangeException(nameof(roomId));

        await PatchRoomAsync(options, roomId, new
        {
            RequestOverride = new { Type = "None", DurationMinutes = 0, SetPoint = 0, Originator = "App" },
        }, ct).ConfigureAwait(false);
        await PatchRoomAsync(options, roomId, new { Mode = "Auto" }, ct).ConfigureAwait(false);
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

    /// <summary>Whole-house away / home — PATCH <c>System/RequestOverride</c> with numeric <c>type</c> / <c>setPoint</c> (matches the Control app).</summary>
    public async Task PatchSystemHomeAwayAsync(MonitorOptions options, string mode, double? awayTemperatureC, CancellationToken ct)
    {
        mode = mode.Trim().ToUpperInvariant();
        object body = mode switch
        {
            "AWAY" when awayTemperatureC is null => throw new ArgumentException("Away temperature required.", nameof(awayTemperatureC)),
            "AWAY" when !IsValidSystemAwayTemperature(awayTemperatureC.Value) => throw new ArgumentOutOfRangeException(nameof(awayTemperatureC)),
            "AWAY" => new { type = 2, setPoint = ToHubTenths(awayTemperatureC!.Value) },
            "HOME" => new { type = 0, setPoint = 0 },
            _ => throw new ArgumentException("Mode must be HOME or AWAY.", nameof(mode)),
        };

        await PatchHubDomainPathAsync(options, "System/RequestOverride", body, ct).ConfigureAwait(false);
    }

    private static int ToHubTenths(double celsius) =>
        (int)Math.Round(celsius * 10, MidpointRounding.AwayFromZero);

    private static bool IsValidSystemAwayTemperature(double celsius) =>
        Math.Abs(celsius - TempOffC) < 0.001 || (celsius >= TempMinimumC && celsius <= TempMaximumC);

    private async Task PatchRoomAsync(MonitorOptions options, int roomId, object body, CancellationToken ct) =>
        await PatchHubDomainPathAsync(options, $"Room/{roomId}", body, ct).ConfigureAwait(false);

    private async Task PatchHubDomainPathAsync(MonitorOptions options, string domainPath, object body, CancellationToken ct)
    {
        var url = $"http://{options.WiserIp}/data/domain/{domainPath.TrimStart('/')}";
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

    /// <summary>
    /// Hub rejects or chokes on these when PATCHing (read-only / derived). Matches aioWiserHeatAPI <c>_remove_schedule_elements</c>.
    /// </summary>
    private static readonly HashSet<string> SchedulePatchHubReadOnlyProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "CurrentSetpoint",
        "CurrentState",
        "Description",
        "CurrentLevel",
        "Name",
        "Next",
        "Type",
        "WrittenTo",
    };

    /// <summary>Reads <c>Type</c> before stripping (needed for v2 URL). Returns null if absent.</summary>
    private static string? ReadScheduleTypeFromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }

        if (root.TryGetProperty("type", out t) && t.ValueKind == JsonValueKind.String)
        {
            var s = t.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                return s.Trim();
        }

        return null;
    }

    /// <summary>
    /// Build PATCH body: same day/program fields as domain export, without metadata the hub parses from the stream incorrectly.
    /// </summary>
    private static bool TryBuildSchedulePatchPayload(byte[] utf8JsonBody, out string? scheduleType, out byte[] payloadUtf8)
    {
        scheduleType = null;
        payloadUtf8 = utf8JsonBody;
        try
        {
            using var doc = JsonDocument.Parse(utf8JsonBody);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            scheduleType = ReadScheduleTypeFromElement(root);

            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (SchedulePatchHubReadOnlyProps.Contains(prop.Name))
                        continue;
                    prop.WriteTo(w);
                }

                w.WriteEndObject();
            }

            payloadUtf8 = buffer.WrittenSpan.ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// PATCH one schedule (body is usually a hub <c>Schedule</c> object from export). Strips read-only fields like aioWiserHeatAPI, then tries v2 and domain URLs.
    /// </summary>
    public async Task PatchScheduleRawAsync(MonitorOptions options, int scheduleId, byte[] utf8JsonBody, CancellationToken ct)
    {
        if (scheduleId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scheduleId));
        if (utf8JsonBody is null || utf8JsonBody.Length == 0)
            throw new ArgumentException("Body required", nameof(utf8JsonBody));

        if (!TryBuildSchedulePatchPayload(utf8JsonBody, out var scheduleType, out var patchBody))
            patchBody = utf8JsonBody;

        // aioWiserHeatAPI (current): PATCH /data/v2/schedules/{Type}/{id}. Legacy wiserheatingapi: /data/domain/Schedule/{id}.
        var urls = new List<string>(5);
        if (!string.IsNullOrEmpty(scheduleType))
        {
            var encType = Uri.EscapeDataString(scheduleType);
            urls.Add($"http://{options.WiserIp}/data/v2/schedules/{encType}/{scheduleId}");
            urls.Add($"http://{options.WiserIp}/data/v2/schedules/{encType}/{scheduleId}/");
        }

        urls.Add($"http://{options.WiserIp}/data/domain/Schedule/{scheduleId}");
        urls.Add($"http://{options.WiserIp}/data/domain/Schedule/{scheduleId}/");

        Exception? last = null;
        for (var i = 0; i < urls.Count; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Patch, urls[i]);
            req.Headers.TryAddWithoutValidation("SECRET", options.WiserSecret);
            req.Content = new ByteArrayContent(patchBody);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return;

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var ex = new HttpRequestException($"Wiser hub returned {code} for schedule {scheduleId} ({urls[i]}): {text}");
            if (code == 404 && i < urls.Count - 1)
            {
                last = ex;
                continue;
            }

            throw ex;
        }

        throw last ?? new HttpRequestException($"Schedule PATCH failed for id {scheduleId}.");
    }

    /// <summary>
    /// Applies schedules from Monitor export JSON, a raw <c>Schedule</c> array, or a single schedule object.
    /// When <paramref name="dryRun"/> is true, validates and builds PATCH bodies but does not contact the hub.
    /// </summary>
    public async Task<ScheduleApplyResult> ApplySchedulesFromImportAsync(
        MonitorOptions options,
        JsonDocument import,
        bool dryRun,
        CancellationToken ct)
    {
        var items = ScheduleImport.EnumerateSchedulesToApply(import.RootElement);
        if (items.Count == 0)
        {
            return new ScheduleApplyResult(
                false,
                dryRun,
                [],
                null,
                null,
                "No schedule objects found. Use export JSON, or { \"schedules\": [ { ... } ] }, or a root array of schedules.");
        }

        var applied = new List<int>();
        var summaries = new List<SchedulePatchSummary>();
        foreach (var sched in items)
        {
            var id = ScheduleImport.GetScheduleId(sched);
            if (id <= 0)
                return new ScheduleApplyResult(false, dryRun, applied, summaries, null, "Each schedule must include a positive numeric \"id\".");

            byte[] body;
            try
            {
                body = Utf8NoBom.GetBytes(sched.GetRawText());
            }
            catch (Exception ex)
            {
                return new ScheduleApplyResult(false, dryRun, applied, summaries, id, ex.Message);
            }

            if (body.Length == 0)
                return new ScheduleApplyResult(false, dryRun, applied, summaries, id, "Schedule JSON serialized to an empty body.");

            summaries.Add(new SchedulePatchSummary(id, body.Length));

            if (!dryRun)
            {
                try
                {
                    await PatchScheduleRawAsync(options, id, body, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new ScheduleApplyResult(false, dryRun, applied, summaries, id, ex.Message);
                }
            }

            applied.Add(id);
        }

        return new ScheduleApplyResult(true, dryRun, applied, summaries, null, null);
    }
}
