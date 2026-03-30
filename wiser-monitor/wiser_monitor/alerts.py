from __future__ import annotations

import logging

import requests

from wiser_monitor.config import Settings

logger = logging.getLogger(__name__)


def send_ntfy(topic: str, title: str, message: str, priority: str = "high") -> None:
    url = f"https://ntfy.sh/{topic}"
    requests.post(
        url,
        data=message.encode("utf-8"),
        headers={"Title": title, "Priority": priority, "Tags": "thermometer"},
        timeout=15,
    ).raise_for_status()


class AlertLatches:
    latched_high: bool = False
    latched_low: bool = False


def process_room_temp(
    *,
    settings: Settings,
    observed_room: str,
    temp_c: float,
    latches: AlertLatches,
) -> None:
    if not settings.alerts_enabled:
        return

    topic = settings.ntfy_topic

    if settings.use_high_alert:
        if temp_c > settings.temp_alert_above_c:
            if not latches.latched_high:
                try:
                    send_ntfy(
                        topic,
                        "Temperature high",
                        f"{observed_room} is {temp_c:.1f} °C — above {settings.temp_alert_above_c:.1f} °C",
                    )
                except requests.RequestException as e:
                    logger.warning("ntfy high alert failed: %s", e)
                latches.latched_high = True
        elif temp_c <= settings.temp_alert_above_c:
            latches.latched_high = False

    if settings.use_low_alert and settings.temp_alert_below_c is not None:
        if temp_c < settings.temp_alert_below_c:
            if not latches.latched_low:
                try:
                    send_ntfy(
                        topic,
                        "Temperature low",
                        f"{observed_room} is {temp_c:.1f} °C — below {settings.temp_alert_below_c:.1f} °C",
                    )
                except requests.RequestException as e:
                    logger.warning("ntfy low alert failed: %s", e)
                latches.latched_low = True
        elif temp_c >= settings.temp_alert_below_c:
            latches.latched_low = False
