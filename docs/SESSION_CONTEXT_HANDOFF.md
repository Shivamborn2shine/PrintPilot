# PrintAway — Complete Conversation & Session Handoff Context

> **How to use on another device (Laptop):**
> Open the cloned `PrintPilot` repository in Antigravity on your laptop, start a chat, and paste:
> `Read docs/SESSION_CONTEXT_HANDOFF.md and docs/SYSTEM_FLOW_AND_MIGRATION_GUIDE.md and continue our PrintAway PRD Implementation session from where we left off.`

---

## 1. Project Identity, Live Links & Credentials

| Item | Value |
|---|---|
| **GitHub Repository** | `https://github.com/Shivamborn2shine/PrintPilot.git` (branch: `main`) |
| **Live Netlify Storefront** | `https://printitaway.netlify.app` |
| **Live Netlify Admin Dashboard** | `https://printitaway.netlify.app/admin` |
| **Local Counter Storefront** | `http://127.0.0.1:8080/` |
| **Local Counter Admin Dashboard** | `http://127.0.0.1:8080/admin` |
| **Admin Username** | `admin` |
| **Admin Password** | `PrintAway@Admin2026` |
| **Default Pilot Shop ID** | `pilot` (`https://printitaway.netlify.app/?shop=pilot`) |
| **Pilot Station Cloud Registry Topic** | `https://ntfy.sh/printaway_station_registry_pilot_9f8a7b6c5d4e` |
| **Other Shops Cloud Registry Topic** | `https://ntfy.sh/printaway_station_registry_<shopId>_9f8a7b` |
| **Verified Physical Printer (Pilot PC)** | `Xerox B1025 Multifunction Printer PCL6` |

---

## 2. Chronological Summary of Everything Built & Solved So Far

1. **Full PRD v2.0 Implementation (Zero External Runtime Dependencies):**
   - Built a 64-bit native Windows C# application (`bin\PrintAway.exe`) compiled using the built-in Windows .NET Framework 4.8 compiler (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) and native Windows SQLite (`C:\Windows\System32\winsqlite3.dll`).
   - Implemented strict PDF validation (`%PDF-` header, `/Type /Pages` count, rejects `/Encrypt` password-protected and `/JS` embedded JavaScript PDFs).
   - Implemented granular page selection (`all`, `first:N`, `odd`, `even`, custom ranges like `1-3,5,8-10`), Color vs. B&W pricing, Simplex vs. Duplex sheet calculation, and N-Up (`1`, `2`, `4` pages per side) layout transformations using a custom native PDF stream generator (`src/PdfNUpProcessor.cs`).
   - Built idempotent UPI payment webhook handling, mandatory counter admin approval before physical printing (`paid_awaiting_approval` -> `queued` -> `rendering` -> `submitted_to_spooler` -> `ready_for_pickup` -> `collected`), and crash-safe spooler reconciliation (`reconciliation_required`).
   - Created a 10-point automated integration test suite (`src/SelfTestRunner.cs` via `scripts/build-and-test.ps1`). All 10 tests pass (`10 Passed, 0 Failed`).

2. **Netlify Cloud Deployment + Local PC Tunnel Integration:**
   - Configured `netlify.toml` (`publish = "web"`, SPA `/admin` redirect, security headers).
   - Fixed `HttpListener` `ProtocolViolationException` crashes in `src/HttpServerAndRoutes.cs` (`res.KeepAlive = false`, safe `WriteBytes` helper, `ThreadPool` exception guard).
   - Fixed Cloudflare Tunnel (`cloudflared.exe`) QUIC UDP packet drops on PDF uploads by enforcing `--protocol http2` over TCP in `scripts/start-tunnel.ps1`.

3. **Zero-Config Permanent QR Code & 1-Click Auto-Start for Shopkeepers:**
   - Eliminated the need to update QR codes or push to Git when the Cloudflare tunnel URL changes.
   - `scripts/start-tunnel.ps1` broadcasts the live tunnel URL to an `ntfy.sh` pub/sub topic on startup and every 30 minutes.
   - `web/index.html` and `web/admin.html` query `ntfy.sh/<topic>/raw?poll=1&since=12h` on page load (~150ms) and connect automatically.
   - Added `enable-autostart.bat` (`scripts/enable-autostart.ps1`) to register a Windows Startup shortcut (`shell:startup`) so the shopkeeper only has to turn on their PC and printer.

4. **Multi-Shop Franchise & Multi-Printer Smart Routing Architecture:**
   - **Multi-Shop (`?shop=<shopId>`):** Any number of shops can clone this repository, run `setup-new-pc.bat` (`scripts/setup-station.ps1`), set their unique `shopId` (e.g., `pilot`, `shop-02`, `campus-01`) and `shopName` in `config/station.json`, and operate independently under `https://printitaway.netlify.app/?shop=<shopId>` and `https://printitaway.netlify.app/admin?shop=<shopId>`.
   - **Multi-Printer Smart Routing:** Each shop can configure:
     - **Primary / Default Printer Queue** (`activeQueueName`)
     - **Dedicated B&W Printer Queue** (`bwQueueName`) — auto-routes B&W orders on approval
     - **Dedicated Color Printer Queue** (`colorQueueName`) — auto-routes Color orders on approval
     - **Per-Order Target Printer Selection** (`targetQueueName`) — counter staff can route any individual order to any connected Windows printer via `⚙️ Edit Print Settings` in `/admin`.

---

## 3. Key Repository Files

- [`src/Program.cs`](../src/Program.cs) — Entry point, CLI flag parser, `config/station.json` startup loader.
- [`src/DatabaseSchemaAndRepo.cs`](../src/DatabaseSchemaAndRepo.cs) — SQLite schema (`shops`, `printers`, `pricing_rules`, `orders`, `print_jobs`, `audit_events`), multi-printer smart routing, pricing & page range engine.
- [`src/HttpServerAndRoutes.cs`](../src/HttpServerAndRoutes.cs) — HTTP server, CORS headers, customer & admin REST API routes.
- [`src/PrintWorker.cs`](../src/PrintWorker.cs) — Background worker that renders N-Up/page ranges and dispatches jobs to Windows Spooler (`targetQueueName`).
- [`src/PdfValidatorAndPrinterDiscovery.cs`](../src/PdfValidatorAndPrinterDiscovery.cs) — WMI printer discovery and strict PDF security/page validator.
- [`src/PdfNUpProcessor.cs`](../src/PdfNUpProcessor.cs) — Pure C# PDF page extractor and 2-up/4-up layout generator.
- [`src/SelfTestRunner.cs`](../src/SelfTestRunner.cs) — 10-point automated PRD test suite.
- [`web/index.html`](../web/index.html) — Customer storefront with multi-shop `?shop=<id>` auto-discovery.
- [`web/admin.html`](../web/admin.html) — Counter Admin Dashboard with multi-printer routing, shop identity config, and per-order overrides.
- [`config/station.json`](../config/station.json) — Portable per-shop and multi-printer configuration file.
- [`scripts/start-tunnel.ps1`](../scripts/start-tunnel.ps1) — 1-click server + HTTP/2 Cloudflare tunnel + `ntfy.sh` broadcaster.
- [`scripts/setup-station.ps1`](../scripts/setup-station.ps1) — Interactive wizard for onboarding a new shop PC or changing printers.
- [`docs/SYSTEM_FLOW_AND_MIGRATION_GUIDE.md`](SYSTEM_FLOW_AND_MIGRATION_GUIDE.md) — Complete architecture, system flow, and migration reference.
