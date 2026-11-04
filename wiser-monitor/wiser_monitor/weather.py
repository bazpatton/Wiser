from __future__ import annotations

from typing import Any

import requests


def fetch_outdoor_temp_c(latitude: float, longitude: float) -> float | None:
    """Open-Meteo current air temperature at 2 m (no API key)."""
    url = "https://api.open-meteo.com/v1/forecast"
    r = requests.get(
        url,
        params={
            "latitude": latitude,
            "longitude": longitude,
            "current": "temperature_2m",
        },
        timeout=(10, 20),
    )
    r.raise_for_status()
    data: Any = r.json()
    if not isinstance(data, dict):
        return None
    current = data.get("current")
    if not isinstance(current, dict):
        return None
    t = current.get("temperature_2m")
    if t is None:
        return None
    try:
        return float(t)
    except (TypeError, ValueError):
        return None
