from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


def _sanitize_numeric_raw(raw: str) -> str:
    """Strip leading '=' from values like '=54.4' (e.g. .env typo OPEN_METEO_LAT==54.4)."""
    s = raw.strip()
    while s.startswith(("=", " ")):
        s = s.lstrip("=").strip()
    return s


def _parse_float(key: str, default: str) -> float:
    return float(_sanitize_numeric_raw(os.environ.get(key, default)))


def _parse_optional_low(key: str) -> float | None:
    raw = os.environ.get(key, "14")
    s = _sanitize_numeric_raw(raw)
    if s in ("", "0"):
        return None
    return float(s)


def _parse_optional_double_env(key: str) -> float | None:
    raw = os.environ.get(key, "").strip()
    if not raw:
        return None
    s = _sanitize_numeric_raw(raw)
    if not s:
        return None
    return float(s)


@dataclass(frozen=True)
class Settings:
    wiser_ip: str
    wiser_secret: str
    ntfy_topic: str
    interval_sec: int
    temp_alert_above_c: float
    temp_alert_below_c: float | None
    host: str
    port: int
    data_dir: Path
    retention_days: int
    open_meteo_lat: float | None
    open_meteo_lon: float | None

    @property
    def db_path(self) -> Path:
        return self.data_dir / "wiser_monitor.sqlite3"

    @property
    def use_high_alert(self) -> bool:
        return self.temp_alert_above_c > 0

    @property
    def use_low_alert(self) -> bool:
        return self.temp_alert_below_c is not None

    @property
    def alerts_enabled(self) -> bool:
        if not self.ntfy_topic:
            return False
        return self.use_high_alert or self.use_low_alert


def load_settings() -> Settings:
    lat = _parse_optional_double_env("OPEN_METEO_LAT")
    lon = _parse_optional_double_env("OPEN_METEO_LON")
    if (lat is None) ^ (lon is None):
        raise ValueError("Set both OPEN_METEO_LAT and OPEN_METEO_LON, or neither.")

    data_dir = Path(os.environ.get("DATA_DIR", "./data")).resolve()

    return Settings(
        wiser_ip=os.environ.get("WISER_IP", "").strip(),
        wiser_secret=os.environ.get("WISER_SECRET", "").strip(),
        ntfy_topic=os.environ.get("NTFY_TOPIC", "").strip(),
        interval_sec=int(os.environ.get("INTERVAL_SEC", "300")),
        temp_alert_above_c=_parse_float("TEMP_ALERT_ABOVE_C", "22"),
        temp_alert_below_c=_parse_optional_low("TEMP_ALERT_BELOW_C"),
        host=os.environ.get("HTTP_HOST", "0.0.0.0"),
        port=int(os.environ.get("HTTP_PORT", "8080")),
        data_dir=data_dir,
        retention_days=int(os.environ.get("RETENTION_DAYS", "60")),
        open_meteo_lat=lat,
        open_meteo_lon=lon,
    )


def validate_settings(s: Settings) -> list[str]:
    """Return human-readable errors; empty if OK."""
    errors: list[str] = []
    if not s.wiser_ip or s.wiser_ip in ("192.168.x.x",):
        errors.append("Set WISER_IP to your hub LAN address.")
    if not s.wiser_secret or s.wiser_secret == "your-secret-here":
        errors.append("Set WISER_SECRET to your hub SECRET.")
    if s.ntfy_topic and not s.alerts_enabled:
        errors.append(
            "NTFY_TOPIC is set but no alert thresholds: use TEMP_ALERT_ABOVE_C>0 and/or TEMP_ALERT_BELOW_C."
        )
    return errors
