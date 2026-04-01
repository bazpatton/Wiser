-- Cleanup historical offline/sentinel temperature rows.
-- Targets known bad values such as -327.68C and physically impossible temperatures.
--
-- Usage (from repo root, sqlite3 installed):
--   sqlite3 "Wiser.Monitor/data/wiser.db" < scripts/cleanup_offline_sentinel.sql
--
-- Optional preview-only query (no deletes):
--   SELECT
--     (SELECT COUNT(*) FROM room_readings WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80) AS bad_room_rows,
--     (SELECT COUNT(*) FROM outdoor_readings WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80) AS bad_outdoor_rows;

BEGIN TRANSACTION;

-- Snapshot counts before deletion.
SELECT 'room_readings_bad_before' AS metric, COUNT(*) AS rows
FROM room_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

SELECT 'outdoor_readings_bad_before' AS metric, COUNT(*) AS rows
FROM outdoor_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

-- Delete invalid room temperatures.
DELETE FROM room_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

-- Delete invalid outdoor temperatures (defensive; usually none).
DELETE FROM outdoor_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

-- Snapshot counts after deletion (should both be 0).
SELECT 'room_readings_bad_after' AS metric, COUNT(*) AS rows
FROM room_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

SELECT 'outdoor_readings_bad_after' AS metric, COUNT(*) AS rows
FROM outdoor_readings
WHERE temp_c = -327.68 OR temp_c < -50 OR temp_c > 80;

COMMIT;
