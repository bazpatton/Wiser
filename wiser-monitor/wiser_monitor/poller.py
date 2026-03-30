from __future__ import annotations

import logging
import threading
import time

import requests

from wiser_monitor.alerts import AlertLatches, process_room_temp
from wiser_monitor.config import Settings
from wiser_monitor.hub import fetch_rooms, parse_rooms
from wiser_monitor.store import Store
from wiser_monitor.weather import fetch_outdoor_temp_c

logger = logging.getLogger(__name__)


class PollerRuntime:
    """Background hub polling; exposes last room list for API before DB fills."""

    def __init__(self, settings: Settings, store: Store) -> None:
        self.settings = settings
        self.store = store
        self._latches: dict[str, AlertLatches] = {}
        self.last_rooms: list[str] = []
        self.last_error: str | None = None
        self.last_ok_ts: float | None = None
        self._tick = 0

    def poll_once(self) -> None:
        ts = int(time.time())
        rooms = fetch_rooms(self.settings)
        parsed = parse_rooms(rooms)
        self.last_rooms = [p["name"] for p in parsed]

        for p in parsed:
            self.store.insert_room(
                ts,
                p["name"],
                p["temp_c"],
                p["setpoint_c"],
                p["heat_demand"],
            )

        current_keys: set[str] = set()
        for p in parsed:
            key = p["name"].strip().casefold()
            current_keys.add(key)
            latch = self._latches.setdefault(key, AlertLatches())
            process_room_temp(
                settings=self.settings,
                observed_room=p["name"],
                temp_c=p["temp_c"],
                latches=latch,
            )

        for k in list(self._latches):
            if k not in current_keys:
                del self._latches[k]

        if self.settings.open_meteo_lat is not None and self.settings.open_meteo_lon is not None:
            try:
                outdoor = fetch_outdoor_temp_c(
                    self.settings.open_meteo_lat,
                    self.settings.open_meteo_lon,
                )
                if outdoor is not None:
                    self.store.insert_outdoor(ts, outdoor)
            except requests.RequestException as e:
                logger.debug("outdoor fetch failed: %s", e)

        self._tick += 1
        if self._tick % 6 == 0:
            self.store.prune(self.settings.retention_days)

        self.last_error = None
        self.last_ok_ts = time.time()


def poller_loop(runtime: PollerRuntime, stop: threading.Event) -> None:
    while True:
        if stop.wait(timeout=runtime.settings.interval_sec):
            break
        try:
            runtime.poll_once()
        except requests.RequestException as e:
            runtime.last_error = str(e)
            logger.warning("hub poll failed: %s", e)
        except (ValueError, OSError) as e:
            runtime.last_error = str(e)
            logger.warning("hub poll error: %s", e)


def start_poller_thread(runtime: PollerRuntime) -> tuple[threading.Thread, threading.Event]:
    stop = threading.Event()
    t = threading.Thread(target=poller_loop, args=(runtime, stop), daemon=True, name="wiser-poller")
    t.start()
    return t, stop
