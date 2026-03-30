using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Wiser.Monitor;
using Wiser.Monitor.Services;
using Wiser.Monitor.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

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
foreach (var err in valErrors)
    Console.Error.WriteLine(err);
if (valErrors.Count > 0)
    return 1;

builder.Services.AddSingleton(monitorOptions);
builder.Services.AddSingleton<MonitorState>();
builder.Services.AddSingleton<TemperatureStore>();
builder.Services.AddHttpClient<WiserHubFetch>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<OutdoorWeatherClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<NtfyClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<RoomAlertService>();
builder.Services.AddHostedService<WiserPollWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", (MonitorState state, MonitorOptions o) =>
{
    var (err, ok) = state.Snapshot();
    return Results.Json(new
    {
        ok = true,
        last_ok_ts = ok,
        last_error = err,
        interval_sec = o.IntervalSec,
        alert_rooms = "all",
        alerts_enabled = o.AlertsEnabled,
        outdoor_enabled = o.OpenMeteoLat is not null,
    });
});

app.MapGet("/api/rooms", (TemperatureStore store, MonitorState state) =>
{
    var names = new HashSet<string>(store.ListRooms(), StringComparer.OrdinalIgnoreCase);
    foreach (var n in state.GetLastRooms())
        names.Add(n);
    return Results.Json(new { rooms = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() });
});

app.MapGet("/api/latest", (TemperatureStore store) =>
    Results.Json(new { rooms = store.LatestByRoom(), system = store.GetLatestSystem() }));

app.MapGet("/api/daily-summary", (int? days, TemperatureStore store, MonitorOptions o) =>
{
    var d = Math.Clamp(days ?? 14, 1, 90);
    var rows = store.GetDailySummaries(d, Math.Max(10, o.IntervalSec));
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
            r.HeatDemand,
            r.PercentageDemand,
            aligned[i])).ToList();
    }
    else
    {
        roomSeries = rowsRaw
            .Select(r => new SeriesRoomRowDto(r.Ts, r.TempC, r.SetpointC, r.HeatDemand, r.PercentageDemand, null))
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

app.MapFallbackToFile("index.html");

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

internal sealed record SeriesRoomRowDto(
    long Ts,
    double TempC,
    double? SetpointC,
    int HeatDemand,
    int? PercentageDemand,
    double? OutdoorC)
{
    public bool CallingForHeat => HeatDemand != 0;
}
