# Wiser.Monitor

ASP.NET **.NET 9** service: **Minimal API** + **Blazor Server** UI with **MudBlazor** / **MudChart** (same REST shape as `wiser-monitor` Python). **BackgroundService** polls the Wiser hub, writes **SQLite** history, optional **Open-Meteo** outdoor series, **ntfy** latched alerts for **every** room (same global thresholds, independent latches per room).

## Run locally

The app **requires** `WISER_IP` and `WISER_SECRET` (same values as Wiser.Control / hub `SECRET` header).

**Option A — environment (PowerShell):**

```powershell
cd Wiser.Monitor
$env:WISER_IP="192.168.x.x"
$env:WISER_SECRET="your-secret"
$env:TIME_ZONE="Europe/London"
$env:DATA_DIR=".\data"
dotnet run
```

**Option B — user secrets (good for F5 in Visual Studio):**

```powershell
cd Wiser.Monitor
dotnet user-secrets set "WISER_IP" "192.168.1.50"
dotnet user-secrets set "WISER_SECRET" "your-hub-secret"
```

Do **not** leave `WISER_IP` / `WISER_SECRET` empty in `launchSettings.json`; empty env vars override secrets.

Open http://localhost:8080 (or the URL in `launchSettings.json`).

### Docker: “Set WISER_IP…” / “Set WISER_SECRET…”

Compose only loads variables from a file named **`.env`** in the **same folder** as `docker-compose.yml` (`Wiser.Monitor/`).

1. `cd` to `Wiser.Monitor`, copy `env.example` → `.env`, edit real IP and secret (no quotes needed).
2. Run `docker compose config` and confirm the `wiser-monitor` service shows your values (secret may be redacted in some versions).
3. Rebuild if you changed nothing else: `docker compose up -d --build`.

If you run Compose from the **repo root**, either `cd Wiser.Monitor` first or pass an explicit project:  
`docker compose -f Wiser.Monitor/docker-compose.yml --project-directory Wiser.Monitor up -d`  
so the **`Wiser.Monitor/.env`** file is picked up for `env_file` and for `${HOST_PORT}` substitution.

## Docker

Full walkthrough (fresh Pi OS image → Docker): **[RASPBERRY-PI-SETUP.md](RASPBERRY-PI-SETUP.md)**.

```bash
cp env.example .env
# edit .env
docker compose up -d --build
```

Image: `mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim`. Build multi-arch from a PC:

```bash
docker buildx build --platform linux/arm64 -t wiser-monitor:net --load .
```

## UI stack

The dashboard now runs as **Blazor Server** with **MudBlazor** components and **MudChart**-based trend charts.  
Font Awesome is included for additional iconography in the UI.

Core data paths are unchanged: the app still uses the same minimal API endpoints and `TemperatureStore`, so scripts and integrations that call `/api/*` remain compatible.

### Floorplan temperature overlay

- Go to **Settings -> Floorplan temperature overlay**.
- Upload a floorplan image (`.png`, `.jpg/.jpeg`, `.webp`).
- Map each room to a pin position using X/Y percentages (0-100), then save.
- Home shows the floorplan with live room temperature pins colored by comfort range.

Uploaded images are stored in `DATA_DIR/floorplan/`. Mapping + selected image metadata are stored in SQLite `app_settings` (`floorplan_config_v1`).

## API

`GET /api/health`, `/api/rooms`, `/api/latest` (includes optional `system`: `heating_relay_on`, `heating_active`, `ts`), `/api/series?room=&hours=&include_outdoor=`

JSON uses **snake_case** property names for compatibility with the existing UI.

Per room sample (history + `/api/latest`):

- **`temp_c`** — measured room temperature  
- **`setpoint_c`** — heat target (hub `CurrentSetPoint`, else `ScheduledSetPoint`)  
- **`heat_demand`** — `1` when the hub reports demand (valve % or TRV output on), else `0`  
- **`heat_demand`** — in JSON/`/api/latest`; non-zero when the room is calling for heat  
- **`percentage_demand`** — TRV **PercentageDemand** (0–100) when the hub sends it  

Existing databases pick up **`percentage_demand`** via a one-time `ALTER TABLE` on startup.

### Energy proxies (no smart meter)

Each poll stores **boiler relay** and **heating active** (relay on or any room demanding heat), matching the idea behind Wiser.Control’s `IsHeatingActive`.

| Path | Purpose |
|------|---------|
| `GET /api/daily-summary?days=14` | Per **UTC** day: **HDD** (15.5 °C base − mean outdoor), estimated **heat demand minutes** and **relay-on minutes** (poll count × interval). Needs Open-Meteo for HDD/outdoor columns. |
| `GET /api/system-series?hours=48` | Time series of `heating_relay_on` / `heating_active` for custom charts. |

Use **HDD** to compare cold vs mild weeks; use **estimated minutes** as a relative gas/proxy trend, not kWh.

### Floorplan endpoints

| Path | Purpose |
|------|---------|
| `GET /api/floorplan/config` | Returns current floorplan config (`image_file_name`, `pins`, etc.). |
| `POST /api/floorplan/config` | Saves floorplan config (room pin mappings). |
| `POST /api/floorplan/upload` | Upload floorplan image (max 10 MB; png/jpg/webp). |
| `GET /api/floorplan/image` | Streams the currently configured floorplan image. |
