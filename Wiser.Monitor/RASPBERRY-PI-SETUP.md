# Wiser.Monitor on Raspberry Pi (fresh install)

End-to-end setup from a **new SD card** to **Docker Compose** running **Wiser.Monitor**. Uses **64-bit Raspberry Pi OS Lite** and the **`Wiser.Monitor`** folder inside the **Wiser** monorepo.

The GitHub repo root contains **`Wiser.Control`** (MAUI app), **`Wiser.Monitor`** (this Docker service), **`wiser-monitor`** (Python), and **`scripts`**. On the Pi you only need the **`Wiser.Monitor`** path after cloning; the other folders are ignored unless you use them.

---

## 1. What to download (on your PC)

1. **[Raspberry Pi Imager](https://www.raspberrypi.com/software/)** (Windows, macOS, or Linux).
2. A **microSD card** (32 GB or larger is fine; A2-rated cards are a bit faster).

You do **not** need to download a raw `.img` file manually—Imager downloads the OS image for you.

---

## 2. Flash the SD card

1. Open **Raspberry Pi Imager**.
2. **Choose device** → your Pi model (e.g. Raspberry Pi 4 or 5).
3. **Choose OS** → **Raspberry Pi OS (other)** → **Raspberry Pi OS Lite (64-bit)**.  
   - *Lite* = no desktop; ideal for a small always-on server.
4. **Choose storage** → your microSD card.
5. Click the **gear icon** (OS customisation) and configure at least:
   - **Hostname** (e.g. `wiser-pi`)
   - **Username** and **password**
   - **Enable SSH** (password or public-key authentication)
   - **Wireless LAN** if the Pi will use Wi‑Fi (optional if using Ethernet)
   - **Locale** and **timezone** (e.g. Europe/London)
6. Click **Write** and wait until it finishes, then eject the card safely.

---

## 3. First boot

1. Insert the SD card, connect **Ethernet** or ensure **Wi‑Fi** is configured, then power on the Pi.
2. On your PC, find the Pi’s address:
   - Your router’s DHCP client list, or
   - `ping wiser-pi.local` (if your network supports mDNS), or
   - the hostname you set.
3. SSH in (replace user and host):

   ```bash
   ssh youruser@wiser-pi.local
   ```

4. Update the system:

   ```bash
   sudo apt update && sudo apt full-upgrade -y
   sudo reboot
   ```

   SSH in again after the reboot.

---

## 4. Install Docker

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
```

Log out and SSH back in so the `docker` group applies.

Verify:

```bash
docker run --rm hello-world
```

Ensure **Docker Compose** is available:

```bash
docker compose version
```

If the command is missing:

```bash
sudo apt install -y docker-compose-plugin
docker compose version
```

---

## 5. Get Wiser.Monitor onto the Pi

### Option A — Git (good for updates)

Clone the **monorepo** (not the old `Wiser.Control`-only repository), then work only in **`Wiser.Monitor`**:

```bash
sudo apt install -y git
cd ~
git clone https://github.com/bazpatton/Wiser.git Wiser
cd Wiser/Wiser.Monitor
```

Replace the URL with **your** fork or private remote if different. For a **private** repo, configure SSH keys (`git@github.com:USER/Wiser.git`) or HTTPS credentials on the Pi.

If you previously cloned **`Wiser.Control`** by itself, switch to this repo (or copy **`Wiser.Monitor`** via Option B) so paths match `~/Wiser/Wiser.Monitor`.

On your **PC**, publish the monorepo once: create an empty GitHub repository named **`Wiser`**, then `git remote add origin …` and `git push -u origin main` from the repo root. After that, the `git clone` line above works on the Pi.

### Option B — Copy files

Copy the **`Wiser.Monitor`** directory (Dockerfile, `docker-compose.yml`, `wwwroot`, etc.) to the Pi using **SCP**, **WinSCP**, or a USB drive.

---

## 6. Configure environment variables

```bash
cd ~/Wiser/Wiser.Monitor
cp env.example .env
nano .env
```

Set at least:

| Variable | Purpose |
|----------|---------|
| `WISER_IP` | Wiser hub IP on your LAN (must be reachable from the Pi) |
| `WISER_SECRET` | Hub **SECRET** header (same as Wiser.Control) |

Optional: `NTFY_TOPIC`, `OPEN_METEO_LAT` / `OPEN_METEO_LON`, `HOST_PORT`, etc.

Save in nano: **Ctrl+O**, Enter, then **Ctrl+X**.

---

## 7. Build and run with Docker Compose

Run these from the directory that contains **`docker-compose.yml`** and **`.env`**:

```bash
cd ~/Wiser/Wiser.Monitor
docker compose up -d --build
```

Follow logs:

```bash
docker compose logs -f
```

On your PC, open a browser (use your Pi’s IP and port; default **8080** unless you changed `HOST_PORT`):

`http://192.168.x.x:8080`

---

## 8. After reboots

`docker-compose.yml` uses **`restart: unless-stopped`**, so the container should start again after power cycles. If it does not:

```bash
cd ~/Wiser/Wiser.Monitor
docker compose up -d
```

---

## 9. Troubleshooting

| Problem | What to check |
|--------|----------------|
| Web UI does not load | `sudo ss -tlnp \| grep 8080`, host firewall (`ufw`), and `HOST_PORT` in `.env` |
| Hub / poll errors in logs | From the Pi: `curl -sS -H "SECRET: YOURSECRET" "http://WISER_IP/data/domain/" \| head` |
| Wrong CPU architecture | Use **64-bit** Raspberry Pi OS; the published image targets **linux/arm64** |

---

## 10. Running without Docker (optional)

Install the **ASP.NET Core runtime** for Linux **ARM64** (see Microsoft’s Linux install docs), publish the app (e.g. self-contained `linux-arm64`), copy the output to the Pi, set the same environment variables, and run under **systemd**. Docker is usually fewer steps for this project.

---

## Summary

1. **Raspberry Pi Imager** → **Pi OS Lite (64-bit)** → enable **SSH** → write SD card.  
2. Boot Pi → **SSH** → `sudo apt update && sudo apt full-upgrade`.  
3. Install **Docker** (+ **Compose**).  
4. **Clone or copy** `Wiser.Monitor` → **`cp env.example .env`** and edit secrets.  
5. **`docker compose up -d --build`** → open **`http://<pi-ip>:8080`**.
