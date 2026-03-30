using Microsoft.Data.Sqlite;
using Wiser.Monitor;

namespace Wiser.Monitor.Services;

public sealed class TemperatureStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public TemperatureStore(MonitorOptions options)
    {
        var dir = Path.GetFullPath(options.DataDir);
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "wiser_monitor.sqlite3");
        InitDb();
    }

    private void InitDb()
    {
        lock (_gate)
        {
            using var c = Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS room_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        room TEXT NOT NULL,
                        temp_c REAL NOT NULL,
                        setpoint_c REAL,
                        heat_demand INTEGER NOT NULL DEFAULT 0,
                        percentage_demand INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_room_readings_room_ts ON room_readings(room, ts);
                    CREATE INDEX IF NOT EXISTS idx_room_readings_ts ON room_readings(ts);

                    CREATE TABLE IF NOT EXISTS outdoor_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        temp_c REAL NOT NULL,
                        source TEXT NOT NULL DEFAULT 'open-meteo'
                    );
                    CREATE INDEX IF NOT EXISTS idx_outdoor_ts ON outdoor_readings(ts);

                    CREATE TABLE IF NOT EXISTS system_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        heating_relay_on INTEGER NOT NULL,
                        heating_active INTEGER NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_system_readings_ts ON system_readings(ts);
                    """;
                cmd.ExecuteNonQuery();
            }

            EnsurePercentageDemandColumn(c);
        }
    }

    private static void EnsurePercentageDemandColumn(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(room_readings);";
        var hasPct = false;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (r.GetString(1) == "percentage_demand")
                    hasPct = true;
            }
        }

        if (hasPct)
            return;
        using var alter = c.CreateCommand();
        alter.CommandText = "ALTER TABLE room_readings ADD COLUMN percentage_demand INTEGER;";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        return c;
    }

    public void InsertRoom(long ts, string room, double tempC, double? setpointC, int heatDemand, int? percentageDemand)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO room_readings (ts, room, temp_c, setpoint_c, heat_demand, percentage_demand)
                VALUES ($ts, $room, $temp, $sp, $hd, $pct)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$temp", tempC);
            cmd.Parameters.AddWithValue("$sp", setpointC ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$hd", heatDemand);
            cmd.Parameters.AddWithValue("$pct", percentageDemand ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertOutdoor(long ts, double tempC, string source = "open-meteo")
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO outdoor_readings (ts, temp_c, source) VALUES ($ts, $t, $s)";
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$t", tempC);
            cmd.Parameters.AddWithValue("$s", source);
            cmd.ExecuteNonQuery();
        }
    }

    public void Prune(int retentionDays)
    {
        if (retentionDays <= 0)
            return;
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - retentionDays * 86400L;
        lock (_gate)
        {
            using var c = Open();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM room_readings WHERE ts < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM outdoor_readings WHERE ts < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM system_readings WHERE ts < $c";
                cmd.Parameters.AddWithValue("$c", cutoff);
                cmd.ExecuteNonQuery();
            }
        }
    }

    public void InsertSystem(long ts, bool heatingRelayOn, bool heatingActive)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO system_readings (ts, heating_relay_on, heating_active)
                VALUES ($ts, $r, $a)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$r", heatingRelayOn ? 1 : 0);
            cmd.Parameters.AddWithValue("$a", heatingActive ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SystemSeriesRow> SeriesSystem(long sinceTs)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT ts, heating_relay_on, heating_active FROM system_readings
                WHERE ts >= $since ORDER BY ts ASC
                """;
            cmd.Parameters.AddWithValue("$since", sinceTs);
            using var r = cmd.ExecuteReader();
            var list = new List<SystemSeriesRow>();
            while (r.Read())
            {
                list.Add(new SystemSeriesRow(
                    r.GetInt64(0),
                    r.GetInt32(1),
                    r.GetInt32(2)));
            }
            return list;
        }
    }

    /// <summary>
    /// Per UTC calendar day: simple HDD (15.5 °C base − mean outdoor) and heating-time proxy from poll counts × interval.
    /// </summary>
    public IReadOnlyList<DailySummaryRow> GetDailySummaries(int days, int pollIntervalSec)
    {
        days = Math.Clamp(days, 1, 366);
        var intervalMin = pollIntervalSec / 60.0;
        const double hddBaseC = 15.5;
        var list = new List<DailySummaryRow>();
        var today = DateTime.UtcNow.Date;

        lock (_gate)
        {
            using var c = Open();
            for (var i = days - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var start = new DateTimeOffset(day, TimeSpan.Zero).ToUnixTimeSeconds();
                var end = new DateTimeOffset(day.AddDays(1), TimeSpan.Zero).ToUnixTimeSeconds();

                double? avgOutdoor = null;
                var outdoorSamples = 0;
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        """
                        SELECT AVG(temp_c), COUNT(*) FROM outdoor_readings
                        WHERE ts >= $s AND ts < $e
                        """;
                    cmd.Parameters.AddWithValue("$s", start);
                    cmd.Parameters.AddWithValue("$e", end);
                    using var r = cmd.ExecuteReader();
                    if (r.Read() && !r.IsDBNull(0))
                    {
                        avgOutdoor = r.GetDouble(0);
                        outdoorSamples = r.GetInt32(1);
                    }
                }

                double? hdd = null;
                if (avgOutdoor is { } ao)
                    hdd = Math.Max(0, hddBaseC - ao);

                var activeCount = 0;
                var relayCount = 0;
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText =
                        """
                        SELECT
                          SUM(CASE WHEN heating_active = 1 THEN 1 ELSE 0 END),
                          SUM(CASE WHEN heating_relay_on = 1 THEN 1 ELSE 0 END)
                        FROM system_readings WHERE ts >= $s AND ts < $e
                        """;
                    cmd.Parameters.AddWithValue("$s", start);
                    cmd.Parameters.AddWithValue("$e", end);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        if (!r.IsDBNull(0))
                            activeCount = (int)r.GetInt64(0);
                        if (!r.IsDBNull(1))
                            relayCount = (int)r.GetInt64(1);
                    }
                }

                list.Add(new DailySummaryRow(
                    day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    hdd,
                    avgOutdoor,
                    activeCount * intervalMin,
                    relayCount * intervalMin,
                    outdoorSamples));
            }
        }

        return list;
    }

    public IReadOnlyList<string> ListRooms()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT room FROM room_readings ORDER BY room COLLATE NOCASE";
            using var r = cmd.ExecuteReader();
            var names = new List<string>();
            while (r.Read())
                names.Add(r.GetString(0));
            return names;
        }
    }

    public Dictionary<string, LatestDto> LatestByRoom()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT r.room, r.temp_c, r.setpoint_c, r.heat_demand, r.ts, r.percentage_demand
                FROM room_readings r
                INNER JOIN (
                    SELECT room, MAX(ts) AS max_ts FROM room_readings GROUP BY room
                ) x ON r.room = x.room AND r.ts = x.max_ts
                """;
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, LatestDto>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                map[r.GetString(0)] = new LatestDto(
                    r.GetDouble(1),
                    r.IsDBNull(2) ? null : r.GetDouble(2),
                    r.GetInt32(3),
                    r.GetInt64(4),
                    r.IsDBNull(5) ? null : r.GetInt32(5));
            }
            return map;
        }
    }

    public IReadOnlyList<RoomSeriesRow> SeriesRoom(string room, long sinceTs)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT ts, temp_c, setpoint_c, heat_demand, percentage_demand FROM room_readings
                WHERE room = $room AND ts >= $since ORDER BY ts ASC
                """;
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$since", sinceTs);
            using var r = cmd.ExecuteReader();
            var list = new List<RoomSeriesRow>();
            while (r.Read())
            {
                list.Add(new RoomSeriesRow(
                    r.GetInt64(0),
                    r.GetDouble(1),
                    r.IsDBNull(2) ? null : r.GetDouble(2),
                    r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetInt32(4)));
            }
            return list;
        }
    }

    public IReadOnlyList<OutdoorSeriesRow> SeriesOutdoor(long sinceTs)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT ts, temp_c FROM outdoor_readings WHERE ts >= $since ORDER BY ts ASC";
            cmd.Parameters.AddWithValue("$since", sinceTs);
            using var r = cmd.ExecuteReader();
            var list = new List<OutdoorSeriesRow>();
            while (r.Read())
                list.Add(new OutdoorSeriesRow(r.GetInt64(0), r.GetDouble(1)));
            return list;
        }
    }
}

public sealed record LatestDto(double TempC, double? SetpointC, int HeatDemand, long Ts, int? PercentageDemand)
{
    /// <summary>Hub reports demand via valve % and/or TRV output (see <see cref="HeatDemand"/>).</summary>
    public bool CallingForHeat => HeatDemand != 0;
}

public sealed record RoomSeriesRow(long Ts, double TempC, double? SetpointC, int HeatDemand, int? PercentageDemand)
{
    public bool CallingForHeat => HeatDemand != 0;
}
public sealed record OutdoorSeriesRow(long Ts, double TempC);

public sealed record SystemSeriesRow(long Ts, int HeatingRelayOn, int HeatingActive);

public sealed record DailySummaryRow(
    string Date,
    double? Hdd,
    double? AvgOutdoorC,
    double HeatingActiveEstimateMin,
    double HeatingRelayEstimateMin,
    int OutdoorSamples);
