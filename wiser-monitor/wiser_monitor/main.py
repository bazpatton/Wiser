from __future__ import annotations

import logging
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, JSONResponse

from wiser_monitor.config import load_settings, validate_settings
from wiser_monitor.poller import PollerRuntime, start_poller_thread
from wiser_monitor.store import Store

logger = logging.getLogger(__name__)

STATIC_DIR = Path(__file__).resolve().parent.parent / "static"


def _align_outdoor_to_timeline(
    timestamps: list[int], outdoor_rows: list[dict[str, Any]]
) -> list[float | None]:
    """Last-known outdoor temp at or before each room sample time."""
    if not outdoor_rows:
        return [None] * len(timestamps)
    sorted_o = sorted(outdoor_rows, key=lambda r: int(r["ts"]))
    out: list[float | None] = []
    j = 0
    last: float | None = None
    for t in timestamps:
        while j < len(sorted_o) and int(sorted_o[j]["ts"]) <= t:
            last = float(sorted_o[j]["temp_c"])
            j += 1
        out.append(last)
    return out


@asynccontextmanager
async def lifespan(app: FastAPI):
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    settings = load_settings()
    errs = validate_settings(settings)
    if errs:
        for msg in errs:
            logger.error("%s", msg)
        raise SystemExit(1)

    settings.data_dir.mkdir(parents=True, exist_ok=True)
    store = Store(settings.db_path)
    runtime = PollerRuntime(settings, store)
    thread, stop = start_poller_thread(runtime)
    app.state.settings = settings
    app.state.store = store
    app.state.runtime = runtime
    app.state.poller_stop = stop
    app.state.poller_thread = thread

    try:
        runtime.poll_once()
    except Exception as e:
        logger.warning("Initial hub poll failed (will retry): %s", e)

    yield

    stop.set()
    thread.join(timeout=15.0)

app = FastAPI(title="Wiser Monitor", lifespan=lifespan)


@app.get("/api/health")
def health(request: Request) -> dict[str, object]:
    runtime: PollerRuntime = request.app.state.runtime
    s = request.app.state.settings
    return {
        "ok": True,
        "last_ok_ts": runtime.last_ok_ts,
        "last_error": runtime.last_error,
        "interval_sec": s.interval_sec,
        "alert_rooms": "all",
        "alerts_enabled": s.alerts_enabled,
        "outdoor_enabled": s.open_meteo_lat is not None,
    }


@app.get("/api/rooms")
def list_rooms(request: Request) -> dict[str, list[str]]:
    store: Store = request.app.state.store
    runtime: PollerRuntime = request.app.state.runtime
    names = sorted(set(store.list_rooms()) | set(runtime.last_rooms), key=str.casefold)
    return {"rooms": names}


@app.get("/api/latest")
def latest(request: Request) -> dict[str, object]:
    store: Store = request.app.state.store
    return {"rooms": store.latest_by_room()}


@app.get("/api/series")
def series(
    request: Request,
    room: str,
    hours: int = 24,
    include_outdoor: bool = True,
) -> dict[str, object]:
    if not room.strip():
        raise HTTPException(400, "room is required")
    hours = max(1, min(hours, 24 * 14))
    since = int(time.time()) - hours * 3600

    store: Store = request.app.state.store
    settings = request.app.state.settings

    rows_raw = store.series_room(room.strip(), since)
    outdoor_rows: list[dict[str, Any]] = []
    if settings.open_meteo_lat is not None:
        outdoor_rows = store.series_outdoor(since)

    room_series: list[dict[str, Any]] = []
    if include_outdoor and outdoor_rows and rows_raw:
        aligned = _align_outdoor_to_timeline([int(r["ts"]) for r in rows_raw], outdoor_rows)
        for i, r in enumerate(rows_raw):
            d = dict(r)
            d["outdoor_c"] = aligned[i]
            room_series.append(d)
    else:
        room_series = [dict(r) for r in rows_raw]
        if include_outdoor and settings.open_meteo_lat is not None:
            for d in room_series:
                d["outdoor_c"] = None

    return {
        "room": room.strip(),
        "hours": hours,
        "room_series": room_series,
        "outdoor_series": outdoor_rows,
        "outdoor_configured": settings.open_meteo_lat is not None,
    }


@app.get("/")
def index() -> FileResponse:
    index_path = STATIC_DIR / "index.html"
    if not index_path.is_file():
        return JSONResponse(
            {"error": "UI missing: static/index.html not found"},
            status_code=500,
        )
    return FileResponse(index_path)
