using System.Text.Json;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using MudBlazor.Services;
using Wiser.Monitor;
using Wiser.Monitor.Components;
using Wiser.Monitor.Services;
using Wiser.Monitor.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

MonitorOptions monitorOptions;
try
{
    monitorOptions = MonitorOptions.FromConfiguration(builder.Configuration);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var valErrors = monitorOptions.Validate();
var hubConfigurationErrors = new List<string>();
if (string.IsNullOrWhiteSpace(monitorOptions.WiserIp) || monitorOptions.WiserIp == "192.168.x.x")
    hubConfigurationErrors.Add("Set WISER_IP to your hub LAN address.");
if (string.IsNullOrWhiteSpace(monitorOptions.WiserSecret) || monitorOptions.WiserSecret == "your-secret-here")
    hubConfigurationErrors.Add("Set WISER_SECRET to your hub SECRET.");
var hubConfigured = hubConfigurationErrors.Count == 0;
if (!hubConfigured)
{
    foreach (var err in hubConfigurationErrors)
        Console.Error.WriteLine($"Configuration warning: {err}");
    Console.Error.WriteLine("Starting in degraded mode: live hub polling and control endpoints are disabled.");
}
foreach (var err in valErrors.Except(hubConfigurationErrors))
    Console.Error.WriteLine($"Configuration warning: {err}");

builder.Services.AddSingleton(monitorOptions);
builder.Services.AddSingleton<MonitorState>();
builder.Services.AddSingleton<TemperatureStore>();
builder.Services.AddSingleton<GasReceiptOcrService>();
builder.Services.AddHttpClient<WiserHubFetch>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<OutdoorWeatherClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<NtfyClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<RoomAlertService>();
builder.Services.AddScoped<RoomsLiveDataCache>();
builder.Services.AddSingleton<ApiRoomsNamesCache>();
if (hubConfigured)
    builder.Services.AddHostedService<WiserPollWorker>();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/health", (MonitorState state, MonitorOptions o) =>
{
    var (err, ok) = state.Snapshot();
    var serviceOk = hubConfigured && ok is not null && string.IsNullOrWhiteSpace(err);
    return Results.Json(new
    {
        ok = serviceOk,
        hub_configured = hubConfigured,
        configuration_errors = hubConfigured ? new List<string>() : hubConfigurationErrors,
        last_ok_ts = ok,
        last_error = err,
        interval_sec = o.IntervalSec,
        alert_rooms = "all",
        alerts_enabled = o.AlertsEnabled,
        outdoor_enabled = o.OpenMeteoLat is not null,
    });
});

app.MapGet("/api/ocr-health", async (GasReceiptOcrService ocr, CancellationToken ct) =>
{
    var (ok, detail) = await ocr.CheckReadinessAsync(ct).ConfigureAwait(false);
    return ok
        ? Results.Json(new { ok = true, detail })
        : Results.Json(new { ok = false, detail }, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/rooms", (TemperatureStore store, MonitorState state, ApiRoomsNamesCache roomsNamesCache) =>
    Results.Json(new { rooms = roomsNamesCache.GetOrCreate(store, state) }));

app.MapGet("/api/latest", (TemperatureStore store) =>
    Results.Json(new { rooms = store.LatestByRoom(), system = store.GetLatestSystem() }));

app.MapGet("/api/hub-live-rooms", async (WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var doc = await hub.FetchDomainDocumentAsync(o, ct).ConfigureAwait(false);
    var overview = HubLiveRoomsParser.ParseOverview(doc, BoostPresets.Default);
    return Results.Json(new
    {
        rooms = overview.Rooms,
        heating_relay_on = overview.HeatingRelayOn,
        heating_active = overview.HeatingActive,
        boost_presets = overview.BoostPresets,
    });
});

app.MapGet("/api/hub-devices", async (WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var doc = await hub.FetchDomainDocumentAsync(o, ct).ConfigureAwait(false);
    var devices = HubDeviceParser.ParseDevices(doc);
    return Results.Json(new { devices });
});

/// <summary>
/// Fetches <c>/data/domain/</c> once plus each listed sub-resource; compares array lengths to the full document when names match.
/// Use locally to see whether sub-URLs add fields beyond the aggregated poll (they usually mirror one top-level key).
/// </summary>
app.MapGet("/api/hub-domain-slices", async (WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var full = await hub.FetchDomainDocumentAsync(o, ct).ConfigureAwait(false);
    var fullRoot = full.RootElement;
    var fullKeys = new List<string>();
    var fullArrayLens = new Dictionary<string, int>(StringComparer.Ordinal);
    if (fullRoot.ValueKind == JsonValueKind.Object)
    {
        foreach (var p in fullRoot.EnumerateObject())
        {
            fullKeys.Add(p.Name);
            if (p.Value.ValueKind == JsonValueKind.Array)
                fullArrayLens[p.Name] = p.Value.GetArrayLength();
        }
    }

    string[] slicePaths =
    [
        "Device/",
        "HeatingChannel/",
        "Room/",
        "RoomStat/",
        "Schedule/",
        "System/",
        "SmartValve/",
    ];

    var probes = new List<HubDomainSliceProbe>();
    foreach (var p in slicePaths)
        probes.Add(await hub.ProbeDomainSliceAsync(o, p, ct).ConfigureAwait(false));

    var comparisons = new List<object>();
    foreach (var probe in probes)
    {
        if (probe.ObjectKeys is null || probe.ArrayElementCounts is null || probe.ObjectKeys.Count != 1)
        {
            comparisons.Add(new
            {
                path = probe.RequestedPath,
                matches_full_domain_slice = (bool?)null,
                note = probe.ObjectKeys is null ? "not an object root or unmapped" : "multi-key root (unexpected for slice)",
            });
            continue;
        }

        var onlyKey = probe.ObjectKeys[0];
        if (!probe.ArrayElementCounts.TryGetValue(onlyKey, out var sliceLen))
        {
            comparisons.Add(new
            {
                path = probe.RequestedPath,
                slice_key = onlyKey,
                matches_full_domain_slice = (bool?)null,
                note = "single key but not an array property",
            });
            continue;
        }

        var matches = fullArrayLens.TryGetValue(onlyKey, out var fullLen) && fullLen == sliceLen;
        comparisons.Add(new
        {
            path = probe.RequestedPath,
            slice_key = onlyKey,
            slice_array_length = sliceLen,
            full_domain_array_length = fullArrayLens.TryGetValue(onlyKey, out var fl) ? fl : (int?)null,
            matches_full_domain_slice = matches,
        });
    }

    return Results.Json(new
    {
        note =
            "Sub-paths usually return the same arrays as GET /data/domain/ for that key. Schedules/RoomStat may still be worth "
            + "pulling separately for bandwidth. Compare matches_full_domain_slice and re-fetch if you add delta logic later.",
        full_domain_top_level_keys = fullKeys,
        full_domain_array_lengths = fullArrayLens,
        slices = probes,
        slice_vs_full_domain = comparisons,
    });
});

app.MapPost("/api/room/boost", async (BoostRoomRequest body, WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (body.room_id <= 0)
        return Results.BadRequest(new { error = "invalid room_id" });
    try
    {
        await hub.PatchRoomBoostAsync(o, body.room_id, body.temperature_c, body.minutes, ct).ConfigureAwait(false);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/room/cancel-override", async (CancelRoomOverrideRequest body, WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (body.room_id <= 0)
        return Results.BadRequest(new { error = "invalid room_id" });
    try
    {
        await hub.PatchRoomCancelTimedOverrideAsync(o, body.room_id, ct).ConfigureAwait(false);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/room/mode", async (RoomModeRequest body, WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (body.room_id <= 0)
        return Results.BadRequest(new { error = "invalid room_id" });
    if (string.IsNullOrWhiteSpace(body.mode))
        return Results.BadRequest(new { error = "mode is required" });
    try
    {
        await hub.PatchRoomModeAsync(o, body.room_id, body.mode, body.temperature_c, ct).ConfigureAwait(false);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/schedules/apply", async (
    HttpRequest req,
    WiserHubFetch hub,
    MonitorOptions o,
    CancellationToken ct,
    [FromQuery(Name = "dry_run")] bool dryRun = false) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    JsonDocument doc;
    try
    {
        doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct).ConfigureAwait(false);
    }
    catch (JsonException jex)
    {
        return Results.BadRequest(new { error = "invalid_json", detail = jex.Message });
    }

    using (doc)
    {
        var result = await hub.ApplySchedulesFromImportAsync(o, doc, dryRun, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Results.Json(
                new
                {
                    ok = false,
                    dry_run = result.DryRun,
                    applied = result.AppliedScheduleIds,
                    summaries = result.Summaries,
                    failed_schedule_id = result.FailedScheduleId,
                    error = result.ErrorMessage,
                },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Json(new
        {
            ok = true,
            dry_run = result.DryRun,
            applied = result.AppliedScheduleIds,
            summaries = result.Summaries,
        });
    }
}).DisableAntiforgery();

app.MapGet("/api/daily-summary", (int? days, TemperatureStore store, MonitorOptions o) =>
{
    var d = Math.Clamp(days ?? 14, 1, 90);
    var zone = TimeZoneResolver.Resolve(o.TimeZoneId);
    var rows = store.GetDailySummaries(d, Math.Max(10, o.IntervalSec), zone);
    return Results.Json(new
    {
        days = rows,
        hdd_base_c = 15.5,
        poll_interval_sec = o.IntervalSec,
        note =
            "Without a smart meter, use heating_active_estimate_min and heating_relay_estimate_min as proxies (poll_count × interval). "
            + "HDD uses mean outdoor samples that day (configure OPEN_METEO_LAT/LON). Compare weeks after normalising for HDD.",
    });
});

app.MapGet("/api/system-series", (int? hours, TemperatureStore store) =>
{
    var h = Math.Clamp(hours ?? 48, 1, 24 * 30);
    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - h * 3600L;
    var rows = store.SeriesSystem(since);
    return Results.Json(new { hours = h, system_series = rows });
});

app.MapGet("/api/data-quality-summary", (int? hours, TemperatureStore store) =>
{
    var h = Math.Clamp(hours ?? 24, 1, 24 * 30);
    var summary = store.GetDataQualitySummary(h);
    return Results.Json(summary);
});

app.MapPost("/api/gas-meter/scan", async (IFormFile file, GasReceiptOcrService ocr, CancellationToken ct) =>
{
    if (file is null || file.Length <= 0)
        return Results.BadRequest(new { error = "file is required" });
    if (file.Length > 10_000_000)
        return Results.BadRequest(new { error = "file exceeds 10MB limit" });

    await using var stream = file.OpenReadStream();
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms, ct).ConfigureAwait(false);

    try
    {
        var result = await ocr.ScanAsync(ms.ToArray(), file.FileName, ct).ConfigureAwait(false);
        return Results.Json(new
        {
            vol_credit = result.VolCredit,
            amount_gbp = result.AmountGbp,
            entry_date = result.EntryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            confidence = result.Confidence,
            raw_text = result.RawText,
            raw_json = result.RawJson,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
}).DisableAntiforgery();

app.MapGet("/api/gas-meter", (TemperatureStore store) =>
{
    return Results.Json(new { rows = store.ListGasMeterReceipts() });
});

app.MapPost("/api/gas-meter", (GasMeterCreateRequest body, TemperatureStore store) =>
{
    if (!TryParseEntryDate(body.entry_date, out var entryDate))
        return Results.BadRequest(new { error = "entry_date must be dd/MM/yy, dd/MM/yyyy, or yyyy-MM-dd" });
    if (body.vol_credit <= 0)
        return Results.BadRequest(new { error = "vol_credit must be greater than 0" });
    if (body.amount_gbp <= 0)
        return Results.BadRequest(new { error = "amount_gbp must be greater than 0" });

    var id = store.CreateGasMeterReceipt(entryDate, body.vol_credit, body.amount_gbp, body.ocr_raw_json, body.source_image_path);
    var row = store.GetGasMeterReceipt(id);
    return Results.Json(new { ok = true, row });
});

app.MapPut("/api/gas-meter/{id:int}", (int id, GasMeterUpdateRequest body, TemperatureStore store) =>
{
    if (!TryParseEntryDate(body.entry_date, out var entryDate))
        return Results.BadRequest(new { error = "entry_date must be dd/MM/yy, dd/MM/yyyy, or yyyy-MM-dd" });
    if (body.vol_credit <= 0)
        return Results.BadRequest(new { error = "vol_credit must be greater than 0" });
    if (body.amount_gbp <= 0)
        return Results.BadRequest(new { error = "amount_gbp must be greater than 0" });

    var ok = store.UpdateGasMeterReceipt(id, entryDate, body.vol_credit, body.amount_gbp);
    if (!ok)
        return Results.NotFound(new { error = "not found" });
    return Results.Json(new { ok = true, row = store.GetGasMeterReceipt(id) });
});

app.MapDelete("/api/gas-meter/{id:int}", (int id, TemperatureStore store) =>
{
    var ok = store.DeleteGasMeterReceipt(id);
    return ok ? Results.Json(new { ok = true }) : Results.NotFound(new { error = "not found" });
});

app.MapGet("/api/export/daily-summary.csv", (int? days, TemperatureStore store, MonitorOptions o) =>
{
    var d = Math.Clamp(days ?? 14, 1, 366);
    var zone = TimeZoneResolver.Resolve(o.TimeZoneId);
    var rows = store.GetDailySummaries(d, Math.Max(10, o.IntervalSec), zone)
        .OrderByDescending(static x => x.Date, StringComparer.Ordinal);

    var csv = new StringBuilder();
    csv.AppendLine("date_utc,hdd,avg_outdoor_c,heat_demand_est_min,boiler_relay_est_min,outdoor_samples");
    foreach (var row in rows)
    {
        csv.Append(row.Date).Append(',')
            .Append(FmtCsvOpt(row.Hdd)).Append(',')
            .Append(FmtCsvOpt(row.AvgOutdoorC)).Append(',')
            .Append(FmtCsv(row.HeatingActiveEstimateMin)).Append(',')
            .Append(FmtCsv(row.HeatingRelayEstimateMin)).Append(',')
            .Append(row.OutdoorSamples.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"daily-summary-{d}d.csv");
});

app.MapGet("/api/export/room-trends.csv", (int? hours, TemperatureStore store, MonitorOptions o) =>
{
    var h = Math.Clamp(hours ?? 24, 1, 24 * 14);
    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - h * 3600L;
    var activity = store.GetRoomActivityMap();
    var (ignoreStart, ignoreEnd) = store.GetTrendIgnoreWindow();
    var zone = TimeZoneResolver.Resolve(o.TimeZoneId);
    var rooms = store.ListRooms().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    var csv = new StringBuilder();
    csv.AppendLine("room,current_temp_c,avg_temp_c,recommended_min_c,recommended_max_c,status,trend_delta_c,is_active,ignore_window_start,ignore_window_end,sample_count_used");

    foreach (var room in rooms)
    {
        var rows = store.SeriesRoom(room, since);
        if (rows.Count == 0)
            continue;

        var current = rows[^1].TempC;
        var isActive = !activity.TryGetValue(room, out var activeFlag) || activeFlag;
        var (rangeMin, rangeMax) = GetRecommendedTempRange(room, isActive);
        var filtered = rows.Where(x => !IsWithinIgnoreWindow(x.Ts, ignoreStart, ignoreEnd, zone)).ToList();
        if (filtered.Count == 0)
        {
            csv.Append(EscapeCsv(room)).Append(',')
                .Append(FmtCsv(current)).Append(',')
                .Append(',')
                .Append(FmtCsv(rangeMin)).Append(',')
                .Append(FmtCsv(rangeMax)).Append(',')
                .Append("No trend data").Append(',')
                .Append(',')
                .Append(isActive ? "true" : "false").Append(',')
                .Append(ignoreStart.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).Append(',')
                .Append(ignoreEnd.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).Append(',')
                .Append('0')
                .AppendLine();
            continue;
        }

        var avg = filtered.Average(static x => x.TempC);
        var trend = filtered[^1].TempC - filtered[0].TempC;
        var status = avg < rangeMin
            ? "Too cold"
            : avg > rangeMax
                ? "Too hot"
                : "In range";

        csv.Append(EscapeCsv(room)).Append(',')
            .Append(FmtCsv(current)).Append(',')
            .Append(FmtCsv(avg)).Append(',')
            .Append(FmtCsv(rangeMin)).Append(',')
            .Append(FmtCsv(rangeMax)).Append(',')
            .Append(EscapeCsv(status)).Append(',')
            .Append(FmtCsv(trend)).Append(',')
            .Append(isActive ? "true" : "false").Append(',')
            .Append(ignoreStart.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).Append(',')
            .Append(ignoreEnd.ToString(@"hh\:mm", CultureInfo.InvariantCulture)).Append(',')
            .Append(filtered.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine();
    }

    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"room-trends-{h}h.csv");
});

app.MapGet("/api/export/schedules.json", async (WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var doc = await hub.FetchDomainDocumentAsync(o, ct).ConfigureAwait(false);
    var bytes = ScheduleExport.BuildSchedulesJsonBytes(doc);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    return Results.File(bytes, "application/json; charset=utf-8", $"wiser-schedules-{stamp}.json");
});

app.MapGet("/api/export/schedules.csv", async (WiserHubFetch hub, MonitorOptions o, CancellationToken ct) =>
{
    if (!hubConfigured)
    {
        return Results.Json(
            new { error = "Hub is not configured. Set WISER_IP and WISER_SECRET.", configuration_errors = hubConfigurationErrors },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var doc = await hub.FetchDomainDocumentAsync(o, ct).ConfigureAwait(false);
    var csv = ScheduleExport.BuildSchedulesCsv(doc);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"wiser-schedules-{stamp}.csv");
});

app.MapGet("/api/series", (
    string room,
    int hours,
    [FromQuery(Name = "include_outdoor")] bool includeOutdoor,
    TemperatureStore store,
    MonitorOptions o) =>
{
    if (string.IsNullOrWhiteSpace(room))
        return Results.BadRequest(new { error = "room is required" });
    hours = Math.Clamp(hours, 1, 24 * 14);
    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hours * 3600L;
    var rowsRaw = store.SeriesRoom(room.Trim(), since);
    var outdoorRows = o.OpenMeteoLat is not null ? store.SeriesOutdoor(since) : [];

    IReadOnlyList<SeriesRoomRowDto> roomSeries;
    if (includeOutdoor && outdoorRows.Count > 0 && rowsRaw.Count > 0)
    {
        var stamps = rowsRaw.Select(r => r.Ts).ToArray();
        var aligned = AlignOutdoor(stamps, outdoorRows);
        roomSeries = rowsRaw.Select((r, i) => new SeriesRoomRowDto(
            r.Ts,
            r.TempC,
            r.SetpointC,
            r.CurrentSetpointC,
            r.ScheduledSetpointC,
            r.HeatDemand,
            r.PercentageDemand,
            aligned[i])).ToList();
    }
    else
    {
        roomSeries = rowsRaw
            .Select(r => new SeriesRoomRowDto(r.Ts, r.TempC, r.SetpointC, r.CurrentSetpointC, r.ScheduledSetpointC, r.HeatDemand, r.PercentageDemand, null))
            .ToList();
    }

    return Results.Json(new
    {
        room = room.Trim(),
        hours,
        room_series = roomSeries,
        outdoor_series = outdoorRows,
        outdoor_configured = o.OpenMeteoLat is not null,
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

static double?[] AlignOutdoor(long[] timestamps, IReadOnlyList<OutdoorSeriesRow> outdoor)
{
    if (outdoor.Count == 0)
        return new double?[timestamps.Length];
    var sorted = outdoor.OrderBy(x => x.Ts).ToList();
    var result = new double?[timestamps.Length];
    var j = 0;
    double? last = null;
    for (var i = 0; i < timestamps.Length; i++)
    {
        var t = timestamps[i];
        while (j < sorted.Count && sorted[j].Ts <= t)
        {
            last = sorted[j].TempC;
            j++;
        }
        result[i] = last;
    }
    return result;
}

static string FmtCsv(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
static string FmtCsvOpt(double? value) => value is null || double.IsNaN(value.Value) ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

static string EscapeCsv(string value)
{
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    return value;
}

static (double Min, double Max) GetRecommendedTempRange(string roomName, bool isActive)
{
    var r = roomName.ToLowerInvariant();
    var baseRange = r switch
    {
        _ when r.Contains("bath") => (21.0, 23.0),
        _ when r.Contains("bed") => (17.0, 19.0),
        _ when r.Contains("kitchen") => (18.0, 20.0),
        _ when r.Contains("hall") || r.Contains("landing") => (16.0, 18.0),
        _ when r.Contains("living") || r.Contains("lounge") => (20.0, 22.0),
        _ => (19.0, 21.0),
    };

    if (isActive)
        return baseRange;

    const double inactiveOffsetC = 2.0;
    return (baseRange.Item1 - inactiveOffsetC, baseRange.Item2 - inactiveOffsetC);
}

static bool IsWithinIgnoreWindow(long timestampUtc, TimeSpan ignoreStart, TimeSpan ignoreEnd, TimeZoneInfo zone)
{
    if (ignoreStart == ignoreEnd)
        return false;

    var localTod = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(timestampUtc), zone).TimeOfDay;
    if (ignoreStart < ignoreEnd)
        return localTod >= ignoreStart && localTod < ignoreEnd;

    return localTod >= ignoreStart || localTod < ignoreEnd;
}

static bool TryParseEntryDate(string? text, out DateOnly date)
{
    if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        return true;
    if (DateOnly.TryParseExact(text, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        return true;
    if (DateOnly.TryParseExact(text, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        return true;
    return false;
}

internal sealed record SeriesRoomRowDto(
    long Ts,
    double TempC,
    double? SetpointC,
    double? CurrentSetpointC,
    double? ScheduledSetpointC,
    int HeatDemand,
    int? PercentageDemand,
    double? OutdoorC)
{
    public bool CallingForHeat => HeatDemand != 0;
}

internal static class BoostPresets
{
    public static readonly IReadOnlyList<BoostPresetInfo> Default =
    [
        new BoostPresetInfo("Quick", 21, 30),
        new BoostPresetInfo("Comfort", 22, 60),
        new BoostPresetInfo("Gentle", 20, 45),
    ];
}

internal sealed record BoostRoomRequest(int room_id, double temperature_c, int minutes);
internal sealed record CancelRoomOverrideRequest(int room_id);
internal sealed record RoomModeRequest(int room_id, string mode, double? temperature_c);
internal sealed record GasMeterCreateRequest(int vol_credit, decimal amount_gbp, string entry_date, string? ocr_raw_json, string? source_image_path);
internal sealed record GasMeterUpdateRequest(int vol_credit, decimal amount_gbp, string entry_date);
