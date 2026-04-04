using Microsoft.Data.Sqlite;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Wiser.Monitor;

namespace Wiser.Monitor.Services;

public sealed class TemperatureStore
{
    /// <summary>Upper bound for uploaded restore files (bytes).</summary>
    public const long MaxDatabaseRestoreBytes = 512L * 1024 * 1024;

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
                        current_setpoint_c REAL,
                        scheduled_setpoint_c REAL,
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

                    CREATE TABLE IF NOT EXISTS room_settings (
                        room TEXT PRIMARY KEY,
                        is_active INTEGER NOT NULL DEFAULT 1,
                        updated_ts INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS app_settings (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL,
                        updated_ts INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS gas_meter_receipts (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        entry_date TEXT NOT NULL,
                        vol_credit INTEGER NOT NULL,
                        amount_gbp REAL NOT NULL,
                        created_ts INTEGER NOT NULL,
                        updated_ts INTEGER NOT NULL,
                        ocr_raw_json TEXT,
                        source_image_path TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_gas_meter_receipts_entry_date ON gas_meter_receipts(entry_date DESC, id DESC);

                    CREATE TABLE IF NOT EXISTS gas_meter_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        reading_value INTEGER NOT NULL,
                        read_ts INTEGER NOT NULL,
                        created_ts INTEGER NOT NULL,
                        updated_ts INTEGER NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_gas_meter_readings_read_ts ON gas_meter_readings(read_ts DESC, id DESC);

                    CREATE TABLE IF NOT EXISTS data_quality_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        room TEXT,
                        source TEXT NOT NULL,
                        reason TEXT NOT NULL,
                        raw_value REAL
                    );
                    CREATE INDEX IF NOT EXISTS idx_data_quality_events_ts ON data_quality_events(ts);

                    CREATE TABLE IF NOT EXISTS ntfy_notifications (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        sent_ts INTEGER NOT NULL,
                        kind TEXT NOT NULL,
                        title TEXT NOT NULL,
                        message TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_ntfy_notifications_sent_ts ON ntfy_notifications(sent_ts DESC);

                    CREATE TABLE IF NOT EXISTS room_alert_latches (
                        room TEXT PRIMARY KEY,
                        latched_high INTEGER NOT NULL DEFAULT 0,
                        latched_low INTEGER NOT NULL DEFAULT 0,
                        updated_ts INTEGER NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            EnsurePercentageDemandColumn(c);
            EnsureSetpointDetailColumns(c);
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

    private static void EnsureSetpointDetailColumns(SqliteConnection c)
    {
        var hasCurrent = false;
        var hasScheduled = false;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(room_readings);";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var col = r.GetString(1);
                if (col == "current_setpoint_c")
                    hasCurrent = true;
                if (col == "scheduled_setpoint_c")
                    hasScheduled = true;
            }
        }

        if (!hasCurrent)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE room_readings ADD COLUMN current_setpoint_c REAL;";
            alter.ExecuteNonQuery();
        }

        if (!hasScheduled)
        {
            using var alter = c.CreateCommand();
            alter.CommandText = "ALTER TABLE room_readings ADD COLUMN scheduled_setpoint_c REAL;";
            alter.ExecuteNonQuery();
        }
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

    private void RunWalCheckpointFull()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(FULL);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates a consistent snapshot via <c>VACUUM INTO</c> (standalone file, no WAL).
    /// Caller should delete the returned path after reading. Thread-safe.
    /// </summary>
    public string ExportDatabaseBackupToTempFile()
    {
        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("Invalid database path.");
        Directory.CreateDirectory(dir);
        var tempOut = Path.Combine(dir, $"wiser-backup-{Guid.NewGuid():N}.sqlite3");
        var escaped = SqliteVacuumIntoPathLiteral(tempOut);

        lock (_gate)
        {
            RunWalCheckpointFull();
            using (var c = Open())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"VACUUM INTO '{escaped}';";
                cmd.ExecuteNonQuery();
            }
        }

        if (!File.Exists(tempOut))
            throw new InvalidOperationException("Backup failed: output file was not created.");
        return tempOut;
    }

    /// <summary>In-memory backup for download endpoints (deletes temp file after read).</summary>
    public byte[] ExportDatabaseBackupBytes()
    {
        var path = ExportDatabaseBackupToTempFile();
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    /// <summary>
    /// Replace the live database with <paramref name="source"/> (must be a valid Wiser Monitor SQLite file).
    /// Backs up the current file, then runs <see cref="InitDb"/> so schema migrations apply to older backups.
    /// </summary>
    public void RestoreDatabaseFromStream(Stream source, long? declaredMaxBytes = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var max = declaredMaxBytes is { } d
            ? Math.Min(d, MaxDatabaseRestoreBytes)
            : MaxDatabaseRestoreBytes;

        var dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException("Invalid database path.");
        Directory.CreateDirectory(dir);

        var tempNew = Path.Combine(dir, $"wiser-restore-{Guid.NewGuid():N}.sqlite3");
        var moved = false;
        try
        {
            CopyStreamWithLimit(source, tempNew, max);
            ValidateRestorableDatabase(tempNew);

            lock (_gate)
            {
                RunWalCheckpointFull();

                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (File.Exists(_path))
                {
                    var preBackup = Path.Combine(dir, $"wiser_monitor.pre-restore-{stamp}-{Guid.NewGuid():N}.sqlite3");
                    File.Copy(_path, preBackup, overwrite: true);

                    DeleteWalSidecarFiles(_path);
                    File.Delete(_path);
                }

                File.Move(tempNew, _path);
                moved = true;
            }
        }
        finally
        {
            if (!moved)
                TryDeleteFile(tempNew);
        }

        InitDb();
    }

    private static void CopyStreamWithLimit(Stream source, string destPath, long maxBytes)
    {
        using var fs = File.Create(destPath);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidOperationException($"Restore file exceeds maximum size ({maxBytes} bytes).");
                fs.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateRestorableDatabase(string path)
    {
        var len = new FileInfo(path).Length;
        if (len < 100)
            throw new InvalidOperationException("File is too small to be a SQLite database.");

        Span<byte> magic = stackalloc byte[16];
        using (var fs = File.OpenRead(path))
        {
            if (fs.Read(magic) < 16)
                throw new InvalidOperationException("Could not read the uploaded file.");
        }

        var sig = Encoding.ASCII.GetString(magic);
        if (!sig.StartsWith("SQLite format 3", StringComparison.Ordinal))
            throw new InvalidOperationException("Not a SQLite database (wrong file header).");

        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString();
        using var c = new SqliteConnection(csb);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name IN ('room_readings','app_settings');
            """;
        var n = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (n < 2)
            throw new InvalidOperationException("This file does not look like a Wiser Monitor database (missing core tables).");

        cmd.CommandText = "PRAGMA quick_check;";
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                throw new InvalidOperationException("SQLite integrity check returned no result.");
            var quick = r.IsDBNull(0) ? null : r.GetString(0);
            if (!string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SQLite integrity check failed: {quick ?? "unknown"}");
        }
    }

    private static string SqliteVacuumIntoPathLiteral(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        return full.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
    }

    private static void DeleteWalSidecarFiles(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var p = dbPath + suffix;
            TryDeleteFile(p);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    public void InsertRoom(
        long ts,
        string room,
        double tempC,
        double? setpointC,
        int heatDemand,
        int? percentageDemand,
        double? currentSetpointC = null,
        double? scheduledSetpointC = null)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO room_readings (ts, room, temp_c, setpoint_c, current_setpoint_c, scheduled_setpoint_c, heat_demand, percentage_demand)
                VALUES ($ts, $room, $temp, $sp, $csp, $ssp, $hd, $pct)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$room", room);
            cmd.Parameters.AddWithValue("$temp", tempC);
            cmd.Parameters.AddWithValue("$sp", setpointC ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$csp", currentSetpointC ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$ssp", scheduledSetpointC ?? (object)DBNull.Value);
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
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM data_quality_events WHERE ts < $c";
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
    /// Per local calendar day: simple HDD (15.5 °C base − mean outdoor) and heating-time proxy from poll counts × interval.
    /// </summary>
    public IReadOnlyList<DailySummaryRow> GetDailySummaries(int days, int pollIntervalSec, TimeZoneInfo? zone = null)
    {
        days = Math.Clamp(days, 1, 366);
        var intervalMin = pollIntervalSec / 60.0;
        const double hddBaseC = 15.5;
        var list = new List<DailySummaryRow>();
        var localZone = zone ?? TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localZone).Date;

        lock (_gate)
        {
            using var c = Open();
            for (var i = days - 1; i >= 0; i--)
            {
                var day = today.AddDays(-i);
                var startOffset = localZone.GetUtcOffset(day);
                var nextDay = day.AddDays(1);
                var endOffset = localZone.GetUtcOffset(nextDay);
                var start = new DateTimeOffset(day, startOffset).ToUnixTimeSeconds();
                var end = new DateTimeOffset(nextDay, endOffset).ToUnixTimeSeconds();

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

    public Dictionary<string, bool> GetRoomActivityMap()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT room, is_active FROM room_settings";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                var room = r.GetString(0);
                var isActive = r.GetInt32(1) != 0;
                map[room] = isActive;
            }

            return map;
        }
    }

    public void SetRoomActive(string room, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(room))
            return;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO room_settings (room, is_active, updated_ts)
                VALUES ($room, $active, $ts)
                ON CONFLICT(room) DO UPDATE SET
                    is_active = excluded.is_active,
                    updated_ts = excluded.updated_ts
                """;
            cmd.Parameters.AddWithValue("$room", room.Trim());
            cmd.Parameters.AddWithValue("$active", isActive ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    public (TimeSpan Start, TimeSpan End) GetTrendIgnoreWindow()
    {
        // Default: ignore 00:00 -> 07:00 local time for trend analysis.
        var fallbackStart = TimeSpan.Zero;
        var fallbackEnd = TimeSpan.FromHours(7);

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT key, value FROM app_settings
                WHERE key IN ('trend_ignore_start', 'trend_ignore_end')
                """;
            using var r = cmd.ExecuteReader();

            var start = fallbackStart;
            var end = fallbackEnd;
            while (r.Read())
            {
                var key = r.GetString(0);
                var value = r.GetString(1);
                if (!int.TryParse(value, out var minutes))
                    continue;
                minutes = Math.Clamp(minutes, 0, 1439);
                if (key == "trend_ignore_start")
                    start = TimeSpan.FromMinutes(minutes);
                else if (key == "trend_ignore_end")
                    end = TimeSpan.FromMinutes(minutes);
            }

            return (start, end);
        }
    }

    public void SetTrendIgnoreWindow(TimeSpan start, TimeSpan end)
    {
        var startMinutes = Math.Clamp((int)start.TotalMinutes, 0, 1439);
        var endMinutes = Math.Clamp((int)end.TotalMinutes, 0, 1439);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            using var c = Open();
            UpsertSetting(c, "trend_ignore_start", startMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), ts);
            UpsertSetting(c, "trend_ignore_end", endMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture), ts);
        }
    }

    public GasMeterReminderSettings GetGasMeterReminderSettings()
    {
        lock (_gate)
        {
            using var c = Open();
            var enabled = false;
            var timesCsv = "";
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT key, value FROM app_settings
                    WHERE key IN ('gas_meter_reminder_enabled', 'gas_meter_reminder_times')
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = r.GetString(0);
                    var value = r.GetString(1);
                    if (key == "gas_meter_reminder_enabled")
                        enabled = value == "1";
                    else if (key == "gas_meter_reminder_times")
                        timesCsv = value;
                }
            }

            return new GasMeterReminderSettings(enabled, ParseGasMeterReminderTimes(timesCsv));
        }
    }

    public void SetGasMeterReminderSettings(bool enabled, IReadOnlyList<TimeSpan> times)
    {
        var normalized = times
            .Select(t => TimeSpan.FromMinutes(Math.Clamp((int)t.TotalMinutes, 0, 1439)))
            .Distinct()
            .OrderBy(t => t.TotalMinutes)
            .ToList();

        var parts = normalized.Select(t => $"{t.Hours:D2}:{t.Minutes:D2}");
        var timesCsv = string.Join(",", parts);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            using var c = Open();
            UpsertSetting(c, "gas_meter_reminder_enabled", enabled ? "1" : "0", ts);
            UpsertSetting(c, "gas_meter_reminder_times", timesCsv, ts);
        }
    }

    public FloorplanConfig GetFloorplanConfig()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_settings WHERE key = 'floorplan_config_v1' LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            return ParseFloorplanConfig(raw);
        }
    }

    public FloorplanConfig SaveFloorplanConfig(FloorplanConfig config)
    {
        var normalized = NormalizeFloorplanConfig(config, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var json = JsonSerializer.Serialize(normalized);
        lock (_gate)
        {
            using var c = Open();
            UpsertSetting(c, "floorplan_config_v1", json, normalized.UpdatedTs);
        }

        return normalized;
    }

    public HeatingSavingsProfile GetHeatingSavingsProfile()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT value FROM app_settings WHERE key = 'heating_savings_profile_v1' LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            return ParseHeatingSavingsProfile(raw);
        }
    }

    public HeatingSavingsProfile SaveHeatingSavingsProfile(HeatingSavingsProfile profile)
    {
        var normalized = NormalizeHeatingSavingsProfile(profile, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var json = JsonSerializer.Serialize(normalized);
        lock (_gate)
        {
            using var c = Open();
            UpsertSetting(c, "heating_savings_profile_v1", json, normalized.UpdatedTs);
        }

        return normalized;
    }

    public IReadOnlyDictionary<int, DateOnly> GetGasMeterReminderLastSent()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT value FROM app_settings WHERE key = 'gas_meter_reminder_last_sent' LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            return ParseReminderLastSentJson(raw);
        }
    }

    public void SetGasMeterReminderLastSent(int minutesSinceMidnight, DateOnly localDate)
    {
        minutesSinceMidnight = Math.Clamp(minutesSinceMidnight, 0, 1439);

        lock (_gate)
        {
            using var c = Open();
            string? raw = null;
            using (var readCmd = c.CreateCommand())
            {
                readCmd.CommandText =
                    "SELECT value FROM app_settings WHERE key = 'gas_meter_reminder_last_sent' LIMIT 1";
                raw = readCmd.ExecuteScalar() as string;
            }

            var map = new Dictionary<int, DateOnly>(ParseReminderLastSentJson(raw));
            map[minutesSinceMidnight] = localDate;
            var json = JsonSerializer.Serialize(
                map.ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            UpsertSetting(c, "gas_meter_reminder_last_sent", json, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
    }

    private static IReadOnlyList<TimeSpan> ParseGasMeterReminderTimes(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return [];

        var list = new List<TimeSpan>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseReminderTime(part, out var t))
                list.Add(t);
        }

        return list
            .Select(t => TimeSpan.FromMinutes(Math.Clamp((int)t.TotalMinutes, 0, 1439)))
            .Distinct()
            .OrderBy(t => t.TotalMinutes)
            .ToList();
    }

    private static bool TryParseReminderTime(string text, out TimeSpan value)
    {
        value = default;
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out value))
            return true;
        if (TimeSpan.TryParseExact(text, @"h\:mm", CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }

    private static Dictionary<int, DateOnly> ParseReminderLastSentJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<int, DateOnly>();

        try
        {
            var doc = JsonDocument.Parse(json);
            var map = new Dictionary<int, DateOnly>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minuteKey))
                    continue;
                minuteKey = Math.Clamp(minuteKey, 0, 1439);
                if (p.Value.ValueKind != JsonValueKind.String)
                    continue;
                if (DateOnly.TryParseExact(p.Value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    map[minuteKey] = d;
            }

            return map;
        }
        catch
        {
            return new Dictionary<int, DateOnly>();
        }
    }

    private static HeatingSavingsProfile ParseHeatingSavingsProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return HeatingSavingsProfile.Default;

        try
        {
            var parsed = JsonSerializer.Deserialize<HeatingSavingsProfile>(json);
            return NormalizeHeatingSavingsProfile(parsed ?? HeatingSavingsProfile.Default, parsed?.UpdatedTs ?? 0);
        }
        catch
        {
            return HeatingSavingsProfile.Default;
        }
    }

    private static FloorplanConfig ParseFloorplanConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return FloorplanConfig.Default;

        try
        {
            var parsed = JsonSerializer.Deserialize<FloorplanConfig>(json);
            return NormalizeFloorplanConfig(parsed ?? FloorplanConfig.Default, parsed?.UpdatedTs ?? 0);
        }
        catch
        {
            return FloorplanConfig.Default;
        }
    }

    private static FloorplanConfig NormalizeFloorplanConfig(FloorplanConfig config, long updatedTs)
    {
        var imageFile = string.IsNullOrWhiteSpace(config.ImageFileName)
            ? null
            : Path.GetFileName(config.ImageFileName.Trim());
        var imageType = string.IsNullOrWhiteSpace(config.ImageContentType)
            ? null
            : config.ImageContentType.Trim();

        var pins = (config.Pins ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Room))
            .GroupBy(p => p.Room.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var p = g.Last();
                return new FloorplanRoomPin(
                    g.Key,
                    Math.Clamp(p.XPercent, 0, 100),
                    Math.Clamp(p.YPercent, 0, 100));
            })
            .OrderBy(p => p.Room, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FloorplanConfig(
            imageFile,
            imageType,
            updatedTs,
            pins);
    }

    private static HeatingSavingsProfile NormalizeHeatingSavingsProfile(HeatingSavingsProfile profile, long updatedTs)
    {
        var rooms = (profile.PriorityRooms ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return profile with
        {
            WeekdayAwayStartMin = Math.Clamp(profile.WeekdayAwayStartMin, 0, 1439),
            WeekdayAwayEndMin = Math.Clamp(profile.WeekdayAwayEndMin, 0, 1439),
            WeekendAwayStartMin = Math.Clamp(profile.WeekendAwayStartMin, 0, 1439),
            WeekendAwayEndMin = Math.Clamp(profile.WeekendAwayEndMin, 0, 1439),
            PriorityRooms = rooms,
            UpdatedTs = updatedTs,
        };
    }

    /// <summary>Total rows in ntfy_notifications.</summary>
    public int CountNtfyNotifications()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ntfy_notifications";
            return Convert.ToInt32((long)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Rows with sent_ts strictly after last mark-viewed (unix). Default 0 = all rows count as unread until user views.</summary>
    public int CountNtfyNotificationsUnread()
    {
        var after = GetNtfyNotificationsLastViewedSentTs();
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM ntfy_notifications WHERE sent_ts > $after";
            cmd.Parameters.AddWithValue("$after", after);
            return Convert.ToInt32((long)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Max sent_ts the user has acknowledged (notifications page or bell menu).</summary>
    public long GetNtfyNotificationsLastViewedSentTs()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "SELECT value FROM app_settings WHERE key = 'ntfy_notifications_last_viewed_ts' LIMIT 1";
            var raw = cmd.ExecuteScalar() as string;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0)
                return v;
            return 0;
        }
    }

    /// <summary>Mark all notifications as viewed up to now (badge clears until a newer notification arrives).</summary>
    public void MarkNtfyNotificationsViewedNow()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var c = Open();
            UpsertSetting(c, "ntfy_notifications_last_viewed_ts", ts.ToString(CultureInfo.InvariantCulture), ts);
        }
    }

    /// <summary>True if an identical notification was logged within the last <paramref name="windowSeconds"/>.</summary>
    public bool NtfyNotificationDuplicateRecent(long sentTs, string kind, string title, string message, int windowSeconds = 300)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "alert" : kind.Trim();
        title ??= "";
        message ??= "";
        var cutoff = sentTs - windowSeconds;
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT 1 FROM ntfy_notifications
                WHERE kind = $kind AND title = $title AND message = $message AND sent_ts >= $cutoff
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$kind", kind.Length > 64 ? kind[..64] : kind);
            cmd.Parameters.AddWithValue("$title", title.Length > 500 ? title[..500] : title);
            cmd.Parameters.AddWithValue("$message", message.Length > 4000 ? message[..4000] : message);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void RecordNtfyNotification(long sentTs, string kind, string title, string message)
    {
        kind = string.IsNullOrWhiteSpace(kind) ? "alert" : kind.Trim();
        if (kind.Length > 64)
            kind = kind[..64];
        title = title ?? "";
        if (title.Length > 500)
            title = title[..500];
        message ??= "";
        if (message.Length > 4000)
            message = message[..4000];

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO ntfy_notifications (sent_ts, kind, title, message)
                VALUES ($sent_ts, $kind, $title, $message)
                """;
            cmd.Parameters.AddWithValue("$sent_ts", sentTs);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$message", message);
            cmd.ExecuteNonQuery();
        }
    }

    public RoomAlertLatchRow? GetRoomAlertLatch(string room)
    {
        var key = NormalizeRoomLatchKey(room);
        if (key.Length == 0)
            return null;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT latched_high, latched_low FROM room_alert_latches
                WHERE room = $room
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$room", key);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new RoomAlertLatchRow(r.GetInt32(0) != 0, r.GetInt32(1) != 0);
        }
    }

    public void UpsertRoomAlertLatch(string room, bool latchedHigh, bool latchedLow)
    {
        var key = NormalizeRoomLatchKey(room);
        if (key.Length == 0)
            return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO room_alert_latches (room, latched_high, latched_low, updated_ts)
                VALUES ($room, $high, $low, $ts)
                ON CONFLICT(room) DO UPDATE SET
                    latched_high = excluded.latched_high,
                    latched_low = excluded.latched_low,
                    updated_ts = excluded.updated_ts
                """;
            cmd.Parameters.AddWithValue("$room", key);
            cmd.Parameters.AddWithValue("$high", latchedHigh ? 1 : 0);
            cmd.Parameters.AddWithValue("$low", latchedLow ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.ExecuteNonQuery();
        }
    }

    private static string NormalizeRoomLatchKey(string room)
    {
        if (string.IsNullOrWhiteSpace(room))
            return "";
        return room.Trim().ToLowerInvariant();
    }

    public IReadOnlyList<NtfyNotificationRow> ListNtfyNotifications(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, sent_ts, kind, title, message
                FROM ntfy_notifications
                ORDER BY sent_ts DESC, id DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var list = new List<NtfyNotificationRow>();
            while (r.Read())
            {
                list.Add(new NtfyNotificationRow(
                    r.GetInt32(0),
                    r.GetInt64(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.GetString(4)));
            }

            return list;
        }
    }

    /// <summary>Removes one ntfy history row by primary key. Returns true if a row was deleted.</summary>
    public bool DeleteNtfyNotification(int id)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM ntfy_notifications WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int CreateGasMeterReceipt(DateOnly entryDate, int volCredit, decimal amountGbp, string? ocrRawJson, string? sourceImagePath)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.CommandText =
                """
                INSERT INTO gas_meter_receipts (entry_date, vol_credit, amount_gbp, created_ts, updated_ts, ocr_raw_json, source_image_path)
                VALUES ($entry_date, $vol_credit, $amount_gbp, $created_ts, $updated_ts, $ocr_raw_json, $source_image_path);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$entry_date", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$vol_credit", volCredit);
            cmd.Parameters.AddWithValue("$amount_gbp", Convert.ToDouble(amountGbp, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$created_ts", now);
            cmd.Parameters.AddWithValue("$updated_ts", now);
            cmd.Parameters.AddWithValue("$ocr_raw_json", string.IsNullOrWhiteSpace(ocrRawJson) ? (object)DBNull.Value : ocrRawJson);
            cmd.Parameters.AddWithValue("$source_image_path", string.IsNullOrWhiteSpace(sourceImagePath) ? (object)DBNull.Value : sourceImagePath);
            return Convert.ToInt32((long)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<GasMeterReceiptRow> ListGasMeterReceipts()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, entry_date, vol_credit, amount_gbp, created_ts, updated_ts, ocr_raw_json, source_image_path
                FROM gas_meter_receipts
                ORDER BY entry_date DESC, id DESC
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<GasMeterReceiptRow>();
            while (r.Read())
                list.Add(ReadGasMeterReceipt(r));
            return list;
        }
    }

    public GasMeterReceiptRow? GetGasMeterReceipt(int id)
    {
        if (id <= 0)
            return null;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, entry_date, vol_credit, amount_gbp, created_ts, updated_ts, ocr_raw_json, source_image_path
                FROM gas_meter_receipts
                WHERE id = $id
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadGasMeterReceipt(r) : null;
        }
    }

    public bool UpdateGasMeterReceipt(int id, DateOnly entryDate, int volCredit, decimal amountGbp)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                UPDATE gas_meter_receipts
                SET entry_date = $entry_date,
                    vol_credit = $vol_credit,
                    amount_gbp = $amount_gbp,
                    updated_ts = $updated_ts
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$entry_date", entryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$vol_credit", volCredit);
            cmd.Parameters.AddWithValue("$amount_gbp", Convert.ToDouble(amountGbp, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$updated_ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteGasMeterReceipt(int id)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM gas_meter_receipts WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int CreateGasMeterReading(int readingValue)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.CommandText =
                """
                INSERT INTO gas_meter_readings (reading_value, read_ts, created_ts, updated_ts)
                VALUES ($reading_value, $read_ts, $created_ts, $updated_ts);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$reading_value", readingValue);
            cmd.Parameters.AddWithValue("$read_ts", now);
            cmd.Parameters.AddWithValue("$created_ts", now);
            cmd.Parameters.AddWithValue("$updated_ts", now);
            return Convert.ToInt32((long)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<GasMeterReadingRow> ListGasMeterReadings()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, reading_value, read_ts, created_ts, updated_ts
                FROM gas_meter_readings
                ORDER BY read_ts DESC, id DESC
                """;
            using var r = cmd.ExecuteReader();
            var list = new List<GasMeterReadingRow>();
            while (r.Read())
                list.Add(new GasMeterReadingRow(
                    r.GetInt32(0),
                    r.GetInt32(1),
                    r.GetInt64(2),
                    r.GetInt64(3),
                    r.GetInt64(4)));
            return list;
        }
    }

    /// <summary>Unix seconds of the most recent gas meter read, or null if none.</summary>
    public long? GetLatestGasMeterReadingUnixTs()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT read_ts FROM gas_meter_readings
                ORDER BY read_ts DESC, id DESC
                LIMIT 1
                """;
            var scalar = cmd.ExecuteScalar();
            if (scalar is null || scalar == DBNull.Value)
                return null;
            return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
    }

    public GasMeterReadingRow? GetGasMeterReading(int id)
    {
        if (id <= 0)
            return null;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, reading_value, read_ts, created_ts, updated_ts
                FROM gas_meter_readings
                WHERE id = $id
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new GasMeterReadingRow(
                r.GetInt32(0),
                r.GetInt32(1),
                r.GetInt64(2),
                r.GetInt64(3),
                r.GetInt64(4));
        }
    }

    public bool UpdateGasMeterReading(int id, int readingValue)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.CommandText =
                """
                UPDATE gas_meter_readings
                SET reading_value = $reading_value,
                    read_ts = $read_ts,
                    updated_ts = $updated_ts
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$reading_value", readingValue);
            cmd.Parameters.AddWithValue("$read_ts", now);
            cmd.Parameters.AddWithValue("$updated_ts", now);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteGasMeterReading(int id)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM gas_meter_readings WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static GasMeterReceiptRow ReadGasMeterReceipt(SqliteDataReader r)
    {
        var entryDateText = r.GetString(1);
        var entryDate = DateOnly.TryParseExact(entryDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.UtcNow.Date);

        return new GasMeterReceiptRow(
            r.GetInt32(0),
            entryDate,
            r.GetInt32(2),
            Convert.ToDecimal(r.GetDouble(3), CultureInfo.InvariantCulture),
            r.GetInt64(4),
            r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7));
    }

    private static void UpsertSetting(SqliteConnection c, string key, string value, long updatedTs)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO app_settings (key, value, updated_ts)
            VALUES ($key, $value, $ts)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_ts = excluded.updated_ts
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$ts", updatedTs);
        cmd.ExecuteNonQuery();
    }

    public void InsertDataQualityEvent(long ts, string? room, string source, string reason, double? rawValue)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(reason))
            return;

        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO data_quality_events (ts, room, source, reason, raw_value)
                VALUES ($ts, $room, $source, $reason, $raw)
                """;
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$room", string.IsNullOrWhiteSpace(room) ? (object)DBNull.Value : room.Trim());
            cmd.Parameters.AddWithValue("$source", source.Trim());
            cmd.Parameters.AddWithValue("$reason", reason.Trim());
            cmd.Parameters.AddWithValue("$raw", rawValue ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public DataQualitySummary GetDataQualitySummary(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hours * 3600L;

        lock (_gate)
        {
            using var c = Open();
            var byReason = new List<DataQualityReasonCount>();
            var bySource = new List<DataQualitySourceCount>();
            var byRoom = new List<DataQualityRoomCount>();

            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT reason, COUNT(*) FROM data_quality_events
                    WHERE ts >= $since
                    GROUP BY reason
                    ORDER BY COUNT(*) DESC
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    byReason.Add(new DataQualityReasonCount(r.GetString(0), (int)r.GetInt64(1)));
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT source, COUNT(*) FROM data_quality_events
                    WHERE ts >= $since
                    GROUP BY source
                    ORDER BY COUNT(*) DESC
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    bySource.Add(new DataQualitySourceCount(r.GetString(0), (int)r.GetInt64(1)));
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    """
                    SELECT COALESCE(room, 'unknown') AS room_name, COUNT(*) FROM data_quality_events
                    WHERE ts >= $since
                    GROUP BY room_name
                    ORDER BY COUNT(*) DESC
                    LIMIT 20
                    """;
                cmd.Parameters.AddWithValue("$since", since);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    byRoom.Add(new DataQualityRoomCount(r.GetString(0), (int)r.GetInt64(1)));
            }

            var total = byReason.Sum(static x => x.Count);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return new DataQualitySummary(hours, since, now, total, byReason, bySource, byRoom);
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
                SELECT r.room, r.temp_c, r.setpoint_c, r.current_setpoint_c, r.scheduled_setpoint_c, r.heat_demand, r.ts, r.percentage_demand
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
                    r.IsDBNull(3) ? null : r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetDouble(4),
                    r.GetInt32(5),
                    r.GetInt64(6),
                    r.IsDBNull(7) ? null : r.GetInt32(7));
            }
            return map;
        }
    }

    /// <summary>Most recent boiler / heating-active flags (same poll batch as <see cref="LatestByRoom"/> when in sync).</summary>
    public LatestSystemSnapshot? GetLatestSystem()
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT ts, heating_relay_on, heating_active
                FROM system_readings
                ORDER BY ts DESC
                LIMIT 1
                """;
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return null;
            return new LatestSystemSnapshot(
                r.GetInt64(0),
                r.GetInt32(1) != 0,
                r.GetInt32(2) != 0);
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
                SELECT ts, temp_c, setpoint_c, current_setpoint_c, scheduled_setpoint_c, heat_demand, percentage_demand FROM room_readings
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
                    r.IsDBNull(3) ? null : r.GetDouble(3),
                    r.IsDBNull(4) ? null : r.GetDouble(4),
                    r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6)));
            }
            return list;
        }
    }

    /// <summary>
    /// Rooms to show on “heating activity” temperature charts: any sample with valve/output demand, or any sample
    /// from a poll where the hub was heating-active (boiler relay or any zone demand — same basis as daily “heat demand est.”).
    /// Per-room <c>heat_demand</c> can stay 0 while the relay fires, so we also match <c>system_readings</c> at the same <c>ts</c>
    /// when <c>heating_active</c> or <c>heating_relay_on</c> is set.
    /// </summary>
    public IReadOnlyList<string> ListRoomsCallingHeatSince(long sinceTs)
    {
        lock (_gate)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                """
                SELECT DISTINCT rr.room
                FROM room_readings rr
                WHERE rr.ts >= $since
                  AND (
                    rr.heat_demand != 0
                    OR (rr.percentage_demand IS NOT NULL AND rr.percentage_demand > 0)
                    OR EXISTS (
                      SELECT 1 FROM system_readings sr
                      WHERE sr.ts = rr.ts
                        AND (sr.heating_active != 0 OR sr.heating_relay_on != 0)
                    )
                  )
                ORDER BY rr.room COLLATE NOCASE
                """;
            cmd.Parameters.AddWithValue("$since", sinceTs);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read())
                list.Add(r.GetString(0));
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

public sealed record LatestDto(
    double TempC,
    double? SetpointC,
    double? CurrentSetpointC,
    double? ScheduledSetpointC,
    int HeatDemand,
    long Ts,
    int? PercentageDemand)
{
    /// <summary>Hub reports demand via TRV output and/or valve percentage.</summary>
    public bool CallingForHeat => HeatDemand != 0 || (PercentageDemand ?? 0) > 0;
}

public sealed record LatestSystemSnapshot(long Ts, bool HeatingRelayOn, bool HeatingActive);

public sealed record RoomSeriesRow(
    long Ts,
    double TempC,
    double? SetpointC,
    double? CurrentSetpointC,
    double? ScheduledSetpointC,
    int HeatDemand,
    int? PercentageDemand)
{
    public bool CallingForHeat => HeatDemand != 0 || (PercentageDemand ?? 0) > 0;
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

public sealed record DataQualitySummary(
    int WindowHours,
    long FromTs,
    long ToTs,
    int TotalIgnored,
    IReadOnlyList<DataQualityReasonCount> ByReason,
    IReadOnlyList<DataQualitySourceCount> BySource,
    IReadOnlyList<DataQualityRoomCount> ByRoom);

public sealed record DataQualityReasonCount(string Reason, int Count);
public sealed record DataQualitySourceCount(string Source, int Count);
public sealed record DataQualityRoomCount(string Room, int Count);

public sealed record GasMeterReminderSettings(bool Enabled, IReadOnlyList<TimeSpan> Times);

public sealed record GasMeterReceiptRow(
    int Id,
    DateOnly EntryDate,
    int VolCredit,
    decimal AmountGbp,
    long CreatedTs,
    long UpdatedTs,
    string? OcrRawJson,
    string? SourceImagePath);

public sealed record GasMeterReadingRow(
    int Id,
    int ReadingValue,
    long ReadTs,
    long CreatedTs,
    long UpdatedTs);

public sealed record RoomAlertLatchRow(bool LatchedHigh, bool LatchedLow);

public sealed record NtfyNotificationRow(int Id, long SentTs, string Kind, string Title, string Message);
