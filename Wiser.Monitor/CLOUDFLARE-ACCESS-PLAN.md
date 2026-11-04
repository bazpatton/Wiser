# Authentication plan: Cloudflare Access

Status: **planned** (written 2026-11-04). Nothing below is implemented yet.

## Goal

- The monitor is reachable from the internet through a Cloudflare Tunnel and currently has **no authentication**. Anyone with the URL can boost rooms, set away mode, change schedules or restore the database.
- It is used mainly on an Android (Samsung) phone, usually **off the home network**.
- Family members need access too.
- Logging in should be rare: once per phone, then remembered for weeks.

## Decision

Put **Cloudflare Access** (Zero Trust, free for up to 50 users) in front of the tunnel hostname, with **Google** as the login method and a **1-month session**. Unauthenticated requests are stopped at Cloudflare's edge and never reach the Pi.

The app then validates the Access token itself (defence in depth) and uses the signed-in email to record who did what, and optionally to restrict admin-only actions.

### Alternatives considered

| Option | Why not |
|---|---|
| Login built into the app | More code to secure ourselves; every family member needs a password managed by us. |
| Tailscale / VPN | Each person must install it and keep it on; on Android it takes the single VPN slot (clashes with work VPNs and ad blockers). |
| Basic auth at the proxy | Shared password, no per-person revoke, poor experience in the installed (PWA) app. |

---

## Phase 1: Cloudflare Access (dashboard only, no code)

1. **Zero Trust → Settings → Authentication → Login methods → Add → Google.** Follow the wizard (it needs a Google Cloud OAuth client). One-time PIN by email can be added as a fallback for anyone without Google.
2. **Zero Trust → Access → Access Groups → Add group `Family`.** Include → Emails → each family member's Google address.
3. **Zero Trust → Access → Applications → Add → Self-hosted.**
   - Application domain: the tunnel hostname that serves the monitor.
   - Session duration: **1 month**.
   - Identity providers: Google (and One-time PIN if added). Enable "Instant auth" if only Google is used, which skips the provider picker.
   - Policy `Family`: Action **Allow**, Include → Access Group → `Family`.
   - No IP-bypass rule: usage is mostly off-network, and the month-long session already covers home.
4. **Note for Phase 2:** the **team domain** (`<team>.cloudflareaccess.com`) and the application's **AUD tag** (Application → Overview).
5. **Test** from mobile data (Wi-Fi off):
   - The URL shows the Cloudflare login, then the app.
   - Pages load, the live connection works (boost a room), and ntfy notification links open the app.
   - A non-family Google account is refused.
6. **Android:** open the app in **Chrome** and use *Add to Home screen / Install app*. The Access login completes inside the installed app window.

Access passes WebSockets through, which the Blazor live connection needs. Nothing else calls the monitor's API through the tunnel: ntfy only sends "open" links, and `Wiser.Control` talks to the hub directly. So no service tokens or path exceptions are needed.

---

## Phase 2: App changes in `Wiser.Monitor`

### 2.1 Validate the Access token

Cloudflare adds a signed JWT to every request in the `Cf-Access-Jwt-Assertion` header (and the `CF_Authorization` cookie).

- Add a JWT bearer authentication scheme (`Microsoft.AspNetCore.Authentication.JwtBearer`) that reads the token from that header:
  - Authority / metadata: `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs` (keys rotate, so let the handler fetch them, not hard-code them).
  - `ValidIssuer` = `https://<team>.cloudflareaccess.com`; `ValidAudience` = the AUD tag.
  - Map the `email` claim to the user's name.
- New config (also add it to `env.example`, `docker-compose.yml` and `MonitorOptions`):
  - `CF_ACCESS_TEAM_DOMAIN`
  - `CF_ACCESS_AUD`
  - `CF_ACCESS_ENFORCE` (default `false` until verified, then `true`)
- Register `AddCascadingAuthenticationState()` so Blazor components see the user. The Blazor connection's `HttpContext.User` comes from the same scheme, so circuits are covered automatically.

**Which requests must carry a token?** Direct LAN access (`http://<pi>:8080`) should keep working without login, but tunnel traffic must not slip through as "LAN".

- **Decide first (open question 1):** where `cloudflared` runs. If it runs on the Pi, tunnel requests arrive from `127.0.0.1` or the Docker bridge, so "is it local?" cannot be decided by remote IP alone.
- **Rule:** a request with a `Cf-Ray` / `Cf-Connecting-IP` header came via Cloudflare, so it **requires** a valid token. A request without those headers from a configured LAN range (`TRUSTED_LAN_CIDRS`, e.g. `192.168.1.0/24`) is allowed as user `lan`. Anything else is rejected with 401 when enforcement is on.
- Apply this as a fallback authorization policy covering pages, `/_blazor` and `/api/*`. Allow `/api/health` anonymously only if something external monitors it.

### 2.2 Remove the app calling its own API over HTTP

`Home.razor` and `Settings.razor` make about 12 calls to their own `/api/...` endpoints through an `HttpClient` aimed at `127.0.0.1` (for example boost, away and expire-due). Under 2.1 those requests carry no token or user. They would either be blocked, or need a loopback exemption that a co-located `cloudflared` would also match.

- Move each endpoint's logic out of `Program.cs` into a service (for example `RoomControlService`, `TimedAwayService`).
- Call that service directly from the Razor components and from the minimal API endpoints.
- This also passes the real user through to the audit log (2.3) and removes an unneeded HTTP round trip.

### 2.3 Record who did what

- Add an `audit_log` SQLite table: `ts`, `user_email`, `action`, `target`, `detail`, `source` (`ui` / `api` / `lan`). Write to it from the services in 2.2.
  - Use a table because `MonitorEventLog` is in-memory and only captures warnings.
- Record: boost (rooms, °C, minutes), cancel override, room mode, home/away, timed away schedule/extend/cancel, schedule apply/import, floorplan changes, gas meter entries, backup restore.
- Show it as an "Activity" list on the Settings or Diagnostics page.
- Optionally add the name to ntfy messages ("Alex set away until 18:00").

### 2.4 Admin-only actions (optional)

- `ADMIN_EMAILS` config, comma-separated.
- An `Admin` policy for: backup restore, schedule apply/import, away policy import, floorplan upload/config, and Settings writes.
- Hide or disable those controls in the UI for non-admins, and enforce the policy on the API endpoints as well.
- LAN user `lan`: treat as admin (local access is already trusted), or configure it otherwise (open question 3).

### 2.5 Recover when the Access session expires

When the Access cookie expires while a page is open, the Blazor reconnect request is redirected to the Cloudflare login. The browser cannot follow that redirect, so `Blazor.reconnect()` keeps throwing and the auto-recovery script in `Components/App.razor` keeps retrying without ever reloading.

- In that script: after about 3 failed retries while `navigator.onLine` is true, do a full `location.reload()`. Cloudflare then shows its login page and returns the user to the app.
- Keep the existing behaviour for genuine outages: while offline, wait for the `online` event instead of reloading into a browser error page.

---

## Rollout order

1. **Phase 1** in the dashboard, then test from mobile data. The app is protected from this point.
2. **2.2** refactor to drop the self-HTTP calls. It has no behaviour change and is safe to ship on its own.
3. **2.1** with `CF_ACCESS_ENFORCE=false`. Log the decision for each request (allowed/denied, user, reason) for a few days and check that nothing legitimate would be blocked.
4. Set `CF_ACCESS_ENFORCE=true`.
5. **2.3** audit log, then **2.5** expiry reload.
6. **2.4** admin roles, if wanted.

## Testing checklist

- [ ] Mobile data: login shown once; app works; still signed in after closing and reopening the installed app.
- [ ] Family member's phone: can sign in; a non-family account is refused.
- [ ] LAN `http://<pi>:8080`: works with no login (records as `lan`).
- [ ] Request to the tunnel hostname with Access temporarily bypassed (or a forged `Cf-Ray` header with no token) gets 401 once enforcement is on.
- [ ] Boost / away from Home records the correct email in the audit log.
- [ ] Non-admin cannot restore a backup (UI hidden and API returns 403).
- [ ] Access session expiry (set the session to 15 min temporarily): an open page reloads to the Cloudflare login rather than spinning.
- [ ] ntfy notification links open the app (signing in first if the session has expired).

## Open questions

1. Where does `cloudflared` run? It is **not** in this repo's container: the `Dockerfile` runs only `dotnet`, and `docker-compose.yml` has a single service. So it is most likely on the Pi as a system service, or in a separate container. Confirm on the Pi with `systemctl status cloudflared` and `docker ps | grep -i cloudflare`, and note the URL the tunnel forwards to.
   - On the host, forwarding to `localhost:8080`: tunnel requests reach the app from the Docker gateway (typically `172.17.0.1`); LAN clients keep their `192.168.x.x` address.
   - In a separate container: tunnel requests come from that container's Docker-network address.
   - Either way, `TRUSTED_LAN_CIDRS` must list the home LAN only, **never** a Docker `172.x` range, and the Cloudflare-header rule in 2.1 decides which requests need a token.
2. Team domain and AUD tag. Available after Phase 1.
3. Should LAN access count as admin?
4. Which family members, if any, should be admins?
5. Should the Python `wiser-monitor` get the same protection, or be retired? It has no auth either. If it's exposed through the tunnel, put it behind its own Access application.
