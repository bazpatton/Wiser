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

## 8. Automatic deploy with a GitHub Actions runner

A push to `main` that touches `Wiser.Monitor/` (or a manual **Run workflow**) should **`git pull`** on the Pi and run **`docker compose up -d --build`**. That is [`.github/workflows/deploy-wiser-monitor.yml`](../.github/workflows/deploy-wiser-monitor.yml). It does **not** use GitHub-hosted machines: it needs a **self-hosted runner** on this Pi, plus a persistent clone at **`$HOME/Wiser`** (or the path you set in **`WISER_DEPLOY_PATH`**).

Complete sections **4–7** first (Docker, clone, **`.env`**, a working container). The workflow only updates that clone; it does not create **`.env`**.

Prefer a **private** repository. A self-hosted runner executes whatever a workflow on this machine is allowed to run, including jobs from **fork pull requests** on a **public** repo.

### 8.1 Create the runner on GitHub

1. Open the repo on GitHub → **Settings** → **Actions** → **Runners** → **New self-hosted runner**.
2. **Operating system:** Linux. **Architecture:** ARM64 (`uname -m` on the Pi must print `aarch64`).
3. Leave that page open. GitHub shows **download** and **`./config.sh`** commands with a **one-hour** registration token. Copy those; do not reuse an old token.

You need **admin** access to the repo (or org) to add a runner.

### 8.2 Install the runner on the Pi

SSH in as the same user that owns **`~/Wiser`** and can run Docker (after **`sudo usermod -aG docker $USER`** and a fresh login).

Install a common dependency, then run **GitHub’s** download/extract commands (version numbers change; paste from the Settings page):

```bash
sudo apt install -y curl tar libicu-dev
mkdir -p ~/actions-runner && cd ~/actions-runner
# paste GitHub’s curl + tar lines here
```

Configure (paste GitHub’s line, or the same URL/token):

```bash
cd ~/actions-runner
./config.sh --url https://github.com/OWNER/Wiser --token PASTE_TOKEN_FROM_GITHUB
```

Accept the defaults unless you have a reason not to:

| Prompt | Suggested answer |
|--------|------------------|
| Runner group | Default |
| Runner name | `wiser-pi` (or the hostname) |
| Labels | Extra labels optional. The workflow uses **`runs-on: self-hosted`** only. |
| Work folder | `_work` (default) |

Do **not** run **`config.sh`** as root.

Install as a **systemd** service so it survives reboot and SSH disconnect:

```bash
cd ~/actions-runner
sudo ./svc.sh install
sudo ./svc.sh start
sudo ./svc.sh status
```

On GitHub, **Settings** → **Actions** → **Runners** should show the runner **Idle** (green).

Foreground test (skip if the service is already running): **`./run.sh`** should print **Connected to GitHub** and **Listening for Jobs**. Stop it with Ctrl+C before installing the service so you do not run two copies.

### 8.3 Git pull as the runner user

The job runs **`git fetch` / `git pull --ff-only origin main`** in the clone. That user must be able to pull **without a password prompt**.

**SSH (recommended)** if the remote is `git@github.com:…`:

```bash
ls -l ~/.ssh
# If you have no key yet:
ssh-keygen -t ed25519 -C "wiser-pi-deploy" -f ~/.ssh/id_ed25519 -N ""
cat ~/.ssh/id_ed25519.pub
```

Add the **public** key as a GitHub **Deploy key** (**Settings** → **Deploy keys**, read-only is enough) or as a user SSH key. Test:

```bash
cd ~/Wiser
git remote -v
ssh -T git@github.com
git fetch origin main
```

If the clone still uses HTTPS, switch it:

```bash
cd ~/Wiser
git remote set-url origin git@github.com:OWNER/Wiser.git
```

Replace **`OWNER/Wiser`** with your repo.

### 8.4 Deploy path (if the clone is not `$HOME/Wiser`)

The workflow uses **`$HOME/Wiser`**. If the runner service’s home is different (for example **`/home/runner`**) or you cloned elsewhere, set a repository **Actions variable**:

1. GitHub → **Settings** → **Secrets and variables** → **Actions** → **Variables**.
2. **New repository variable**: name **`WISER_DEPLOY_PATH`**, value the folder that contains **`.git`** (e.g. `/home/admin/Wiser`).

Do not put **`WISER_SECRET`** in Actions secrets for this workflow: Compose still reads **`Wiser.Monitor/.env`** on the Pi.

### 8.5 What the workflow does

On **`push` to `main`** when **`Wiser.Monitor/**`** or the workflow file changes, or on **Actions** → **Deploy Wiser.Monitor (Pi)** → **Run workflow**:

1. `cd` to the clone.
2. `git checkout main` and `git pull --ff-only origin main`.
3. `cd Wiser.Monitor` and `docker compose up -d --build`.

`--ff-only` **fails** if the Pi clone has local commits or a dirty tree (other than ignored files such as **`.env`**). Do not edit tracked files on the Pi.

### 8.6 First deploy check

1. Confirm the runner is **Idle**.
2. GitHub → **Actions** → **Deploy Wiser.Monitor (Pi)** → **Run workflow** → **Run workflow**.
3. Open the job log. You should see pull + compose build/start.
4. On the Pi: `docker compose -f ~/Wiser/Wiser.Monitor/docker-compose.yml ps` and `curl -sS http://127.0.0.1:8080/api/health`.

After that, a **`git push`** to **`main`** that changes **`Wiser.Monitor/`** should deploy the same way.

### 8.7 Runner troubleshooting

| Problem | What to check |
|--------|----------------|
| Job queued, never starts | Runner **Offline**. `sudo ~/actions-runner/svc.sh status`. Reboot or `sudo ./svc.sh start`. |
| `permission denied` while talking to Docker | Runner user not in **`docker`** group. `groups`; `sudo usermod -aG docker $USER`; restart the runner service (and re-login). |
| `No git repo at …` | Clone missing, or **`WISER_DEPLOY_PATH`** / `$HOME` does not match. `echo $HOME` as that user. |
| `git pull` authentication failed | SSH key / deploy key; `git remote -v`. |
| `fatal: Not possible to fast-forward` | Local commits or a divergent `main` on the Pi. Inspect with `git status` in the clone; prefer resetting only if you know you can discard Pi-only commits. |
| Two runners / jobs stolen | Only one **`run.sh`** / service. `ps aux \| grep Runner`. |

Useful service commands (from **`~/actions-runner`**):

```bash
sudo ./svc.sh status
sudo ./svc.sh stop
sudo ./svc.sh start
```

To unregister (GitHub Settings → Runners → remove, then on the Pi):

```bash
cd ~/actions-runner
sudo ./svc.sh stop
sudo ./svc.sh uninstall
./config.sh remove --token PASTE_NEW_REMOVE_TOKEN
```

GitHub documents adding runners here: [Adding self-hosted runners](https://docs.github.com/en/actions/how-tos/managing-self-hosted-runners/adding-self-hosted-runners).

---

## 9. After reboots

`docker-compose.yml` uses **`restart: unless-stopped`**, so the container should start again after power cycles. If it does not:

```bash
cd ~/Wiser/Wiser.Monitor
docker compose up -d
```

---

## 10. Troubleshooting

| Problem | What to check |
|--------|----------------|
| Web UI does not load | `sudo ss -tlnp \| grep 8080`, host firewall (`ufw`), and `HOST_PORT` in `.env` |
| Hub / poll errors in logs | From the Pi: `curl -sS -H "SECRET: YOURSECRET" "http://WISER_IP/data/domain/" \| head` |
| Wrong CPU architecture | Use **64-bit** Raspberry Pi OS; the published image targets **linux/arm64** |

---

## 11. Running without Docker (optional)

Install the **ASP.NET Core runtime** for Linux **ARM64** (see Microsoft’s Linux install docs), publish the app (e.g. self-contained `linux-arm64`), copy the output to the Pi, set the same environment variables, and run under **systemd**. Docker is usually fewer steps for this project.

---

## Summary

1. **Raspberry Pi Imager** → **Pi OS Lite (64-bit)** → enable **SSH** → write SD card.  
2. Boot Pi → **SSH** → `sudo apt update && sudo apt full-upgrade`.  
3. Install **Docker** (+ **Compose**).  
4. **Clone or copy** `Wiser.Monitor` → **`cp env.example .env`** and edit secrets.  
5. **`docker compose up -d --build`** → open **`http://<pi-ip>:8080`**.  
6. Optional: **section 8** — GitHub self-hosted runner so **`main`** auto-pulls and rebuilds.
