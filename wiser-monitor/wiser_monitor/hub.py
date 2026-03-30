from __future__ import annotations

import json
from typing import Any

import requests

from wiser_monitor.config import Settings


def fetch_rooms(settings: Settings) -> list[dict[str, Any]]:
    url = f"http://{settings.wiser_ip}/data/domain/"
    r = requests.get(url, headers={"SECRET": settings.wiser_secret}, timeout=15)
    r.raise_for_status()
    data = r.json()
    rooms = data.get("Room") or data.get("room")
    if rooms is None:
        raise ValueError(
            "No Room array in domain JSON (keys: " + ", ".join(sorted(data.keys())) + ")"
        )
    return rooms


def temp_c_from_tenths(tenths: Any) -> float | None:
    if tenths is None:
        return None
    try:
        v = float(tenths)
    except (TypeError, ValueError):
        return None
    if v >= 2000:
        return None
    return v / 10.0


def room_temperature_c(room: dict[str, Any]) -> float | None:
    tenths = room.get("CalculatedTemperature")
    if tenths is None:
        tenths = room.get("DisplayedTemperature")
    return temp_c_from_tenths(tenths)


def room_setpoint_c(room: dict[str, Any]) -> float | None:
    tenths = room.get("CurrentSetPoint")
    if tenths is None:
        tenths = room.get("ScheduledSetPoint")
    return temp_c_from_tenths(tenths)


def room_heat_demand(room: dict[str, Any]) -> int:
    demand = room.get("PercentageDemand")
    try:
        if demand is not None and int(demand) > 0:
            return 1
    except (TypeError, ValueError):
        pass
    state = room.get("ControlOutputState")
    if isinstance(state, str) and state.lower() in ("on", "open"):
        return 1
    return 0


def room_display_name(room: dict[str, Any]) -> str:
    return str(room.get("Name") or room.get("name") or "").strip() or "Room"


def parse_rooms(rooms: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Normalized rows for storage: name, temp_c, setpoint_c, heat_demand."""
    out: list[dict[str, Any]] = []
    for room in rooms:
        name = room_display_name(room)
        temp = room_temperature_c(room)
        if temp is None:
            continue
        out.append(
            {
                "name": name,
                "temp_c": temp,
                "setpoint_c": room_setpoint_c(room),
                "heat_demand": room_heat_demand(room),
            }
        )
    return out
