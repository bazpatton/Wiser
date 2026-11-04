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
        self._last_outdoor_mono: float | None = None
        self.last_outdoor_error: str | None = None

    def poll_once(self) -> None:
        ts = int(time.time())
        logger.debug("poll_once: fetching rooms from hub")
        rooms = fetch_rooms(self.settings)
        logger.debug("poll_once: hub returned %d room(s)", len(rooms))
        parsed = parse_rooms(rooms)
        logger.debug("poll_once: parsed %d room(s) with valid temps", len(parsed))
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

        self._tick += 1
        if self._tick % 6 == 0:
            self.store.prune(self.settings.retention_days)

        self.last_error = None
        self.last_ok_ts = time.time()

    def poll_outdoor_if_due(self) -> None:
        """Fetch outdoor temp at most every outdoor_interval_sec; independent of hub success.

        Only a successful fetch resets the timer, so failures retry on the next tick.
        """
        s = self.settings
        if s.open_meteo_lat is None or s.open_meteo_lon is None:
            return
        now = time.monotonic()
        if self._last_outdoor_mono is not None and now - self._last_outdoor_mono < s.outdoor_interval_sec:
            return
        try:
            outdoor = fetch_outdoor_temp_c(s.open_meteo_lat, s.open_meteo_lon)
        except requests.RequestException as e:
            self.last_outdoor_error = str(e)
            logger.warning("outdoor fetch failed: %s", e)
            return
        if outdoor is None:
            self.last_outdoor_error = "no temperature value in response"
            logger.warning("outdoor fetch returned no temperature value")
            return
        self.store.insert_outdoor(int(time.time()), outdoor)
        self._last_outdoor_mono = now
        self.last_outdoor_error = None
        logger.debug("outdoor temp fetched: %.1f °C", outdoor)


def poller_loop(runtime: PollerRuntime, stop: threading.Event) -> None:
    while True:
        if stop.wait(timeout=runtime.settings.interval_sec):
            break
        try:
            runtime.poll_once()
        except requests.RequestException as e:
            runtime.last_error = str(e)
            logger.warning("hub poll failed: %s", e)
        except Exception as e:
            # Never let an unexpected error (sqlite, bad JSON shape, ...) kill the thread.
            runtime.last_error = str(e)
            logger.exception("hub poll error")

        try:
            runtime.poll_outdoor_if_due()
        except Exception as e:
            runtime.last_outdoor_error = str(e)
            logger.exception("outdoor poll error")


def start_poller_thread(runtime: PollerRuntime) -> tuple[threading.Thread, threading.Event]:
    stop = threading.Event()
    t = threading.Thread(target=poller_loop, args=(runtime, stop), daemon=True, name="wiser-poller")
    t.start()
    return t, stop
