#!/usr/bin/env python3
"""
Poll a Wiser Heat Hub and push alerts via https://ntfy.sh/

Alerts apply to every room that reports a valid temperature, using the same
global thresholds (TEMP_ALERT_ABOVE_C / TEMP_ALERT_BELOW_C) and an independent
latch per room so you are not spammed while a room stays out of range.

Setup:
  pip install requests
  set environment variables (recommended) or edit the defaults below.

  Windows (PowerShell):
    $env:WISER_IP = "192.168.1.x"
    $env:WISER_SECRET = "your-hub-secret"
    $env:NTFY_TOPIC = "wiser_ctrl_m7nq4b8k_your_suffix"  # pick your own unguessable string
    python wiser_ntfy_monitor.py

Phone: install ntfy app → Subscribe to the same topic (ntfy.sh is public; topic name = shared secret).
"""

from __future__ import annotations

import json
import os
import sys
import time
from typing import Any

import requests

# --- Config: prefer environment variables (avoid committing secrets) ---
WISER_IP = os.environ.get("WISER_IP", "192.168.x.x")
WISER_SECRET = os.environ.get("WISER_SECRET", "your-secret-here")
NTFY_TOPIC = os.environ.get("NTFY_TOPIC", "")  # must set; see NTFY_TOPIC_SUGGESTION below

# Poll interval (seconds)
INTERVAL_SEC = int(os.environ.get("INTERVAL_SEC", "300"))

# Alert when temp goes ABOVE this (°C). Disable with TEMP_ALERT_ABOVE_C=0
TEMP_ALERT_ABOVE_C = float(os.environ.get("TEMP_ALERT_ABOVE_C", "22"))

# Alert when temp goes BELOW this (°C). Disable with TEMP_ALERT_BELOW_C=0
_raw_low = os.environ.get("TEMP_ALERT_BELOW_C", "14")
TEMP_ALERT_BELOW_C: float | None = float(_raw_low) if _raw_low.strip() not in ("", "0") else None

# Suggested topic pattern (replace with your own random string; do not use this exact value):
NTFY_TOPIC_SUGGESTION = "wiser_ctrl_m7nq4b8k_" + os.urandom(6).hex()


def fetch_rooms() -> list[dict[str, Any]]:
    """Same data source as Wiser.Control: GET /data/domain/ with SECRET header."""
    url = f"http://{WISER_IP}/data/domain/"
    r = requests.get(url, headers={"SECRET": WISER_SECRET}, timeout=15)
    r.raise_for_status()
    data = r.json()
    rooms = data.get("Room") or data.get("room")
    if rooms is None:
        raise ValueError("No Room array in domain JSON (keys: " + ", ".join(sorted(data.keys())) + ")")
    return rooms


def room_temp_c(room: dict[str, Any]) -> float | None:
    tenths = room.get("CalculatedTemperature")
    if tenths is None:
        tenths = room.get("DisplayedTemperature")
    if tenths is None:
        return None
    try:
        v = float(tenths)
    except (TypeError, ValueError):
        return None
    # Hub often uses 2000+ as error sentinel (see Wiser app / Wiser.Control)
    if v >= 2000:
        return None
    return v / 10.0


def room_display_name(room: dict[str, Any]) -> str:
    return str(room.get("Name") or room.get("name") or "").strip() or "Room"


def send_ntfy(title: str, message: str, priority: str = "high") -> None:
    if not NTFY_TOPIC:
        print("Set NTFY_TOPIC (env) to your private topic.", file=sys.stderr)
        sys.exit(2)
    url = f"https://ntfy.sh/{NTFY_TOPIC}"
    requests.post(
        url,
        data=message.encode("utf-8"),
        headers={"Title": title, "Priority": priority, "Tags": "thermometer"},
        timeout=15,
    ).raise_for_status()


def main() -> None:
    if WISER_IP in ("192.168.x.x", "") or WISER_SECRET in ("your-secret-here", ""):
        print("Set WISER_IP and WISER_SECRET (environment variables).", file=sys.stderr)
        sys.exit(2)
    if not NTFY_TOPIC:
        print(
            f"Set NTFY_TOPIC env var. Example new topic name: {NTFY_TOPIC_SUGGESTION}",
            file=sys.stderr,
        )
        sys.exit(2)

    use_high = TEMP_ALERT_ABOVE_C > 0
    use_low = TEMP_ALERT_BELOW_C is not None
    if not use_high and not use_low:
        print("Enable at least one of TEMP_ALERT_ABOVE_C or TEMP_ALERT_BELOW_C.", file=sys.stderr)
        sys.exit(2)

    # Per room (casefold key), per direction
    latched_high: dict[str, bool] = {}
    latched_low: dict[str, bool] = {}

    print(f"Monitoring all rooms; every {INTERVAL_SEC}s; ntfy topic set.", flush=True)
    if use_high:
        print(f"  Alert any room if temp > {TEMP_ALERT_ABOVE_C} °C", flush=True)
    if use_low:
        print(f"  Alert any room if temp < {TEMP_ALERT_BELOW_C} °C", flush=True)

    while True:
        try:
            rooms = fetch_rooms()
            ts = time.strftime("%H:%M:%S")
            seen_keys: set[str] = set()

            for room in rooms:
                rname = room_display_name(room)
                key = rname.casefold()
                temp = room_temp_c(room)
                if temp is None:
                    print(f"{ts}  {rname}: no valid temperature", flush=True)
                    continue

                seen_keys.add(key)
                print(f"{ts}  {rname}: {temp:.1f} °C", flush=True)

                if use_high and temp > TEMP_ALERT_ABOVE_C:
                    if not latched_high.get(key, False):
                        send_ntfy(
                            "Temperature high",
                            f"{rname} is {temp:.1f} °C — above {TEMP_ALERT_ABOVE_C:.1f} °C",
                        )
                        latched_high[key] = True
                elif use_high and temp <= TEMP_ALERT_ABOVE_C:
                    latched_high[key] = False

                if use_low and TEMP_ALERT_BELOW_C is not None and temp < TEMP_ALERT_BELOW_C:
                    if not latched_low.get(key, False):
                        send_ntfy(
                            "Temperature low",
                            f"{rname} is {temp:.1f} °C — below {TEMP_ALERT_BELOW_C:.1f} °C",
                        )
                        latched_low[key] = True
                elif use_low and TEMP_ALERT_BELOW_C is not None and temp >= TEMP_ALERT_BELOW_C:
                    latched_low[key] = False

            for k in list(latched_high.keys()):
                if k not in seen_keys:
                    latched_high.pop(k, None)
            for k in list(latched_low.keys()):
                if k not in seen_keys:
                    latched_low.pop(k, None)

        except requests.RequestException as e:
            print(f"{time.strftime('%H:%M:%S')}  request error: {e}", flush=True)
        except (ValueError, json.JSONDecodeError) as e:
            print(f"{time.strftime('%H:%M:%S')}  data error: {e}", flush=True)

        time.sleep(INTERVAL_SEC)


if __name__ == "__main__":
    main()
