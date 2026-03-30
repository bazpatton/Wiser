# AGENTS.md

## Cursor Cloud specific instructions

### Codebase overview

This monorepo contains three components for monitoring/controlling a Drayton Wiser Heat Hub:

| Component | Tech | Port | Purpose |
|---|---|---|---|
| `Wiser.Monitor/` | ASP.NET Core 9 (C#) | 8080 | Hub polling, SQLite history, Chart.js dashboard, ntfy alerts |
| `wiser-monitor/` | Python 3.12 / FastAPI | 8080 | Same functionality, Python implementation |
| `Wiser.Control/` | .NET MAUI (C#) | N/A | Cross-platform mobile/desktop app (cannot build on Linux) |

### Running the services

Both monitor services require `WISER_IP` and `WISER_SECRET` env vars. Without a physical hub, use dummy values (e.g. `WISER_IP=10.0.0.1 WISER_SECRET=dev-secret`) — the services start and serve API/UI normally but hub polls will fail gracefully.

**.NET monitor:**
```bash
cd Wiser.Monitor
WISER_IP=10.0.0.1 WISER_SECRET=dev-secret DATA_DIR=./data dotnet run --urls="http://0.0.0.0:8080"
```

**Python monitor:**
```bash
cd wiser-monitor
source .venv/bin/activate
WISER_IP=10.0.0.1 WISER_SECRET=dev-secret DATA_DIR=./data python -m uvicorn wiser_monitor.main:app --host 0.0.0.0 --port 8080
```

### Key gotchas

- `.NET 9 SDK` is installed at `$HOME/.dotnet`. The PATH and DOTNET_ROOT are set in `~/.bashrc`.
- `Wiser.Control` targets `net9.0-android;net9.0-ios;net9.0-maccatalyst` — it **cannot be built on Linux**. Only `Wiser.Monitor` (targeting `net9.0`) builds on Linux.
- Python venv for `wiser-monitor` lives at `wiser-monitor/.venv`.
- Both services use the same env var names (`WISER_IP`, `WISER_SECRET`, `DATA_DIR`, etc.) — see `env.example` in each directory.
- SQLite databases are stored in `DATA_DIR` (defaults to `./data`).
- No automated test suite exists in the repo. Testing is manual via API endpoints and the web UI.
- When running both services simultaneously, use different ports (e.g. 8080 and 8081).

### API endpoints (both services share the same shape)

- `GET /api/health` — service status
- `GET /api/rooms` — list room names
- `GET /api/latest` — latest sample per room
- `GET /api/series?room=...&hours=24&include_outdoor=true` — time series
- `GET /` — Chart.js dashboard UI
