# Phase 0 — Environment setup checklist

Goal of Phase 0 (plan Section 8): demo accounts, cTrader ID linked, Open API app registered, .NET + VS Code on Ubuntu, Git repo. When every box below is ticked, run `scripts/check-env.sh`; it should print no `FAIL` lines.

Status on 11-Oct-2026: Ubuntu toolchain, network, folders and secrets file done (`check-env.sh`: no failures). IC Markets demo checked (Hedging, Raw Spread). Open API app **submitted** (ID 43770, awaiting Spotware approval). Pepperstone cTrader demo created as Broker B (Hedging, same cTrader ID). Open: tokens after approval; confirm Pepperstone demo account type (Razor).

---

## Step 1 — IC Markets demo account (done)

- [x] Log in at **https://app.ctrader.com** with the cTrader ID (email) you used for the IC Markets demo. The account should appear in the account list.
- [x] Check the account is in **Hedging** mode, not Netting (account info in cTrader). The whole hedge design (plan 10.6) needs this. *Confirmed: Hedging.*
- [x] Note the account type: **Raw Spread** (spread + commission) or **Standard** (spread only). Open item in plan Section 12; the Audit Bot will compare them. *Confirmed: Raw Spread.*
- [x] Note the base currency (USD assumed) and leverage. *Confirmed: USD, 1:500, demo balance USD 5,000 (entity: Raw Trading Ltd). Demo account number: **10124393**.*
- [ ] Do **not** note down passwords anywhere in this repo. The bot never uses the account password — it uses OAuth tokens (Step 3).

## Step 2 — Second broker demo (done: Pepperstone)

- [x] Open a cTrader demo at the second broker (Pepperstone, FP Markets or Fusion Markets) **with the same cTrader ID email**, so one access token covers both accounts. *Done 11-Oct-2026: **Pepperstone** demo **5357135** (server cTrader DEMO), listed under the same cTrader ID as IC Markets.*
- [ ] Same checks as Step 1 (Hedging, account type). *Hedging confirmed; USD, 1:200, demo balance USD 5,000. Account type still to confirm — should be **Razor** (Pepperstone's raw spread + commission) to match IC Markets.*

Notes:
- `pepperstone.com` is blocked on this home network (TLS connection reset); sign-up works through `https://secure.pepperstone.com/register`.
- A Pepperstone **live** cTrader account (1398129, Razor, USD, 1:200, unfunded) was also created by mistake. It is not used until Phase 5; do not fund it before then.

The Audit Bot needs both, but Phase 0 can finish with one and add the second before Phase 2.

## Step 3 — Register the Open API application (submitted 11-Oct-2026, awaiting approval)

1. Go to **https://openapi.ctrader.com/** and log in with the same cTrader ID.
2. Open **https://openapi.ctrader.com/apps** → **Add new app**.
3. Fill in the form. Give a detailed description — Spotware says this speeds up approval. Suggested text:
   > Personal application for my own cTrader accounts only. A C# (.NET) service on Linux that (1) logs spreads, slippage and execution latency on demo accounts to compare brokers, and later (2) executes and manages my own discretionary trades with server-side protective orders. No third-party users, no copy-trading, no distribution.
4. Save. Status shows **Submitted**. *Done 11-Oct-2026: app "Trend Audit Bot", ID 43770, permission "Access your accounts and trade" only. The portal limits the description to 300 characters, so a shortened version of the text above was used, and app names may not contain third-party brands (cTrader, IC Markets).* Spotware reviews it and replies by email (no fixed review time is published).
5. After approval:
   - [ ] Copy the **Client ID** and **Client Secret** into the secrets file (Step 6), never into the repo.
   - [ ] Use the **Playground** on the app page to get an **access token** and **refresh token** with scope **`trading`**. (The default redirect URI only works in the Playground, which is fine for a personal bot.)
   - [ ] Copy both tokens into the secrets file.

Facts to know (from Spotware docs):

| Item | Value |
|---|---|
| Demo endpoint (Protobuf) | `demo.ctraderapi.com:5035` (TLS) |
| Live endpoint (Protobuf) | `live.ctraderapi.com:5035` (TLS) |
| Connections | One for all demo accounts, one for all live accounts |
| Heartbeat | Send every 10 s |
| Rate limits per connection | 50 req/s normal, 5 req/s historical data |
| Access token lifetime | ~30 days; renew with the refresh token |
| Refresh token | Does not expire; each refresh returns a new pair and invalidates the old one |

Terms:
- **Client ID / Client Secret** — the username/password of the *application* (not of you). Identifies the bot to Spotware.
- **Access token** — a time-limited key that lets the app act on the accounts you granted. Like a session ticket.
- **Refresh token** — a long-lived key used only to get a new access token. Because each refresh replaces it, the bot must save the new one every time.
- **Scope** — what the token allows: `accounts` = read-only, `trading` = read + trade.

## Step 4 — Ubuntu toolchain

Check your Ubuntu release first: `lsb_release -a`.

### 4.1 .NET 10 SDK (LTS)

> **Plan v1.4:** .NET 8 and .NET 9 reach end of support on **10 Nov 2026**, so the project uses **.NET 10 LTS** (supported to Nov 2028).

Ubuntu 24.04 or newer — .NET 10 is in Ubuntu's own archive:

```bash
sudo apt update
sudo apt install -y dotnet-sdk-10.0
```

Ubuntu 22.04 — use the .NET backports PPA:

```bash
sudo add-apt-repository ppa:dotnet/backports
sudo apt update
sudo apt install -y dotnet-sdk-10.0
```

Check: `dotnet --list-sdks` shows a `10.0.x` line.

Do not mix Ubuntu's .NET packages with Microsoft's own apt repository on the same machine; pick one source (Ubuntu's, above).

Terms:
- **LTS (Long-Term Support)** — a release that gets security fixes for 3 years, versus 18–24 months for others.
- **SDK vs runtime** — the SDK builds code; the runtime only runs it. A development machine needs the SDK; the VPS later only needs the runtime.
- **PPA** — a Personal Package Archive: an extra apt source that ships newer packages than the Ubuntu release has.

### 4.2 Git and GitHub access

```bash
sudo apt install -y git
git config --global user.name "Zeeshan"
git config --global user.email "<your GitHub email>"
```

Authenticate to GitHub once, either:
- **GitHub CLI:** `sudo apt install -y gh` then `gh auth login` (choose HTTPS, browser login), or
- **SSH key:** `ssh-keygen -t ed25519`, then add `~/.ssh/id_ed25519.pub` in GitHub → Settings → SSH keys.

Clone the repo:

```bash
mkdir -p ~/code && cd ~/code
git clone https://github.com/ziash/Trader.git
```

### 4.3 VS Code + C# Dev Kit

```bash
sudo snap install code --classic
code --install-extension ms-dotnettools.csdevkit
```

C# Dev Kit installs the C# language extension and the .NET install tool with it.

### 4.4 Clock (NTP)

```bash
timedatectl
```

Must show `System clock synchronized: yes` and `NTP service: active`. If not: `sudo timedatectl set-ntp true`. Slippage and latency figures are only valid if the clock is right.

If NTP is active but never synchronises, the network may be blocking the default time servers (UDP 123). On this home network (Pakistan) `ntp.ubuntu.com`, `pool.ntp.org`, Google and Cloudflare all timed out; only `pk.pool.ntp.org` answered, and the clock had drifted ~24 s. Fix used:

```bash
sudo mkdir -p /etc/systemd/timesyncd.conf.d && printf '[Time]\nNTP=pk.pool.ntp.org\nFallbackNTP=ntp.ubuntu.com\n' | sudo tee /etc/systemd/timesyncd.conf.d/trader.conf
sudo systemctl restart systemd-timesyncd
```

To undo: delete that file and restart the service.

## Step 5 — Network check

The bot talks to `demo.ctraderapi.com` on **TCP port 5035** over TLS. Some home and mobile broadband networks filter non-standard ports, so test it once:

```bash
openssl s_client -connect demo.ctraderapi.com:5035 -brief </dev/null
```

A line with `CONNECTION ESTABLISHED` and a TLS protocol version means the path is open. A timeout means port 5035 is blocked on this network (router firewall or ISP).

## Step 6 — Folders and secrets (outside the repo)

Runtime folders from plan Section 6.3:

```bash
mkdir -p ~/tradebot/{config,run/instances,logs,reports}
```

Secrets file (never in Git):

```bash
mkdir -p ~/.config/trader
install -m 600 /dev/null ~/.config/trader/secrets.env
nano ~/.config/trader/secrets.env
```

Contents (fill in after Step 3 approval):

```
CTRADER_CLIENT_ID=
CTRADER_CLIENT_SECRET=
CTRADER_ACCESS_TOKEN=
CTRADER_REFRESH_TOKEN=
CTRADER_ENV=demo
```

`install -m 600` creates the file readable and writable by your user only.

## Step 7 — Verify

```bash
cd ~/code/Trader
bash scripts/check-env.sh
```

Fix any `FAIL`; `WARN` lines are items that can wait (e.g. Open API approval still pending).

---

## Note on the cTrader C# library

Spotware's official `cTrader.OpenAPI.Net` NuGet package (v1.4.4) was last published in 2022 and targets .NET 6. It still runs on .NET 10, but its dependencies are old. Phase 1 starts with it; if it causes trouble, the fallback is to generate C# classes directly from Spotware's `.proto` message files and write a thin TLS client — more work, fully under our control.
