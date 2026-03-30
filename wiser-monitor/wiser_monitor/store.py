from __future__ import annotations

import sqlite3
import threading
import time
from pathlib import Path
from typing import Any


class Store:
    def __init__(self, path: Path) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        self._path = path
        self._lock = threading.Lock()
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self._path, check_same_thread=False, timeout=30.0)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode=WAL")
        return conn

    def _init_db(self) -> None:
        with self._lock:
            c = self._connect()
            try:
                c.executescript(
                    """
                    CREATE TABLE IF NOT EXISTS room_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        room TEXT NOT NULL,
                        temp_c REAL NOT NULL,
                        setpoint_c REAL,
                        heat_demand INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_room_readings_room_ts
                        ON room_readings(room, ts);
                    CREATE INDEX IF NOT EXISTS idx_room_readings_ts ON room_readings(ts);

                    CREATE TABLE IF NOT EXISTS outdoor_readings (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ts INTEGER NOT NULL,
                        temp_c REAL NOT NULL,
                        source TEXT NOT NULL DEFAULT 'open-meteo'
                    );
                    CREATE INDEX IF NOT EXISTS idx_outdoor_ts ON outdoor_readings(ts);
                    """
                )
                c.commit()
            finally:
                c.close()

    def insert_room(
        self,
        ts: int,
        room: str,
        temp_c: float,
        setpoint_c: float | None,
        heat_demand: int,
    ) -> None:
        with self._lock:
            c = self._connect()
            try:
                c.execute(
                    """
                    INSERT INTO room_readings (ts, room, temp_c, setpoint_c, heat_demand)
                    VALUES (?, ?, ?, ?, ?)
                    """,
                    (ts, room, temp_c, setpoint_c, heat_demand),
                )
                c.commit()
            finally:
                c.close()

    def insert_outdoor(self, ts: int, temp_c: float, source: str = "open-meteo") -> None:
        with self._lock:
            c = self._connect()
            try:
                c.execute(
                    """
                    INSERT INTO outdoor_readings (ts, temp_c, source) VALUES (?, ?, ?)
                    """,
                    (ts, temp_c, source),
                )
                c.commit()
            finally:
                c.close()

    def prune(self, retention_days: int) -> None:
        if retention_days <= 0:
            return
        cutoff = int(time.time()) - retention_days * 86400
        with self._lock:
            c = self._connect()
            try:
                c.execute("DELETE FROM room_readings WHERE ts < ?", (cutoff,))
                c.execute("DELETE FROM outdoor_readings WHERE ts < ?", (cutoff,))
                c.commit()
            finally:
                c.close()

    def list_rooms(self) -> list[str]:
        with self._lock:
            c = self._connect()
            try:
                rows = c.execute(
                    "SELECT DISTINCT room FROM room_readings ORDER BY room COLLATE NOCASE"
                ).fetchall()
                return [str(r[0]) for r in rows]
            finally:
                c.close()

    def latest_by_room(self) -> dict[str, dict[str, Any]]:
        with self._lock:
            c = self._connect()
            try:
                rows = c.execute(
                    """
                    SELECT r.room, r.temp_c, r.setpoint_c, r.heat_demand, r.ts
                    FROM room_readings r
                    INNER JOIN (
                        SELECT room, MAX(ts) AS max_ts FROM room_readings GROUP BY room
                    ) x ON r.room = x.room AND r.ts = x.max_ts
                    """
                ).fetchall()
                return {
                    str(row["room"]): {
                        "temp_c": row["temp_c"],
                        "setpoint_c": row["setpoint_c"],
                        "heat_demand": row["heat_demand"],
                        "ts": row["ts"],
                    }
                    for row in rows
                }
            finally:
                c.close()

    def series_room(self, room: str, since_ts: int) -> list[dict[str, Any]]:
        with self._lock:
            c = self._connect()
            try:
                rows = c.execute(
                    """
                    SELECT ts, temp_c, setpoint_c, heat_demand FROM room_readings
                    WHERE room = ? AND ts >= ?
                    ORDER BY ts ASC
                    """,
                    (room, since_ts),
                ).fetchall()
                return [dict(row) for row in rows]
            finally:
                c.close()

    def series_outdoor(self, since_ts: int) -> list[dict[str, Any]]:
        with self._lock:
            c = self._connect()
            try:
                rows = c.execute(
                    """
                    SELECT ts, temp_c FROM outdoor_readings
                    WHERE ts >= ? ORDER BY ts ASC
                    """,
                    (since_ts,),
                ).fetchall()
                return [dict(row) for row in rows]
            finally:
                c.close()
