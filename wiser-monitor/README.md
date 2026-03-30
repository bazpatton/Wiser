# Wiser Monitor (Docker)

Self-contained service that:

- Polls the Wiser Heat Hub (`GET /data/domain/`) on an interval
- Records **all rooms** (temperature, setpoint, heat demand) to **SQLite**
- Optionally fetches **outdoor** temperature (Open-Meteo) for context on the chart
- Sends **ntfy.sh** alerts (latched high/low) for **every** room with a valid temperature — same global thresholds and per-room latches as `scripts/wiser_ntfy_monitor.py`
- Serves a small **web UI** at `/` with Chart.js graphs

## Quick start

1. Copy `env.example` to `.env` and set `WISER_IP`, `WISER_SECRET`, and optionally `NTFY_TOPIC`, coordinates, etc.
2. From this directory:

   ```bash
   docker compose up -d --build
   ```

3. Open `http://localhost:8080` (or your Pi’s IP on port `HOST_PORT`).

Data lives in the `wiser_monitor_data` Docker volume (`/data` in the container).

## Local run (no Docker)

```bash
cd wiser-monitor
python -m venv .venv
.venv\Scripts\activate   # Windows
# source .venv/bin/activate  # Linux / Pi
pip install -r requirements.txt
set DATA_DIR=./data
set WISER_IP=...
set WISER_SECRET=...
python -m uvicorn wiser_monitor.main:app --host 0.0.0.0 --port 8080
```

## Pi vs PC

Build the image on each machine, or use a multi-platform build:

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t wiser-monitor:latest .
```

On Linux, if the container cannot reach the hub on the LAN, try `network_mode: host` in Compose (only on Linux hosts) and use `8080` on the host.

## Outdoor temperature

`OPEN_METEO_LAT` and `OPEN_METEO_LON` add a green **Outdoor** line on the chart. It is **not** used for control — it helps interpret how fast rooms move with the weather. Wiser still controls on room sensors and schedules.

## API

| Path | Purpose |
|------|---------|
| `GET /api/health` | Poll status, alert config |
| `GET /api/rooms` | Room names (DB + last hub poll) |
| `GET /api/latest` | Latest sample per room |
| `GET /api/series?room=...&hours=24&include_outdoor=true` | Time series |

## Environment variables

See `env.example`. Critical: `WISER_IP`, `WISER_SECRET`. If `NTFY_TOPIC` is set, keep at least one of `TEMP_ALERT_ABOVE_C` (>0) or `TEMP_ALERT_BELOW_C` (non-zero).
