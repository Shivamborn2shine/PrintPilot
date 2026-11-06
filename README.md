# PrintAway — Self-Service Print Shop Software Layer

**Brand Line:** *"Printing ke liye pareshan ho? Just PrintAway."*  
**Value Proposition:** Upload. Configure. Pay. Print. Collect.  
**Live Cloud Storefront (Netlify):** `https://printitaway.netlify.app`  
**Local Host Portal:** `http://127.0.0.1:8080/`  
**Default Active Printer Queue:** `Xerox B1025 Multifunction Printer PCL6` (`USB001` — Configurable via Admin UI or `config/station.json`)

> **Full Deep-Dive Guide:** See [docs/SYSTEM_FLOW_AND_MIGRATION_GUIDE.md](docs/SYSTEM_FLOW_AND_MIGRATION_GUIDE.md) for the complete architecture diagram, how the printer hardware is commanded, and how to change printers or move to a new PC in one click.

---

## 1. Architecture Overview (Local-Host + Cloud Tunnel)

PrintAway runs natively on any Windows 10/11 workstation with **zero external runtime installers** (uses built-in `.NET 4.8 csc.exe`, `winsqlite3.dll`, and WinRT `Windows.Data.Pdf`):

- **Cloud Storefront (`https://printitaway.netlify.app`) + Local Server (`127.0.0.1:8080`):** Serves the Customer Portal (`/`) and protected Staff Admin Dashboard (`/admin`) both locally and globally over a secure Cloudflare HTTP/2 Tunnel.
- **Full Printer & Page Controls:**
  - **Color Mode:** Black & White (ITU-R BT.601 luminance grayscale enforcement) vs. Full Color.
  - **Granular Page Selection:** All pages, First $N$ pages, From–To range, Odd only, Even only, or interactive clickable page chips (`1-3, 5`).
  - **Layout & Finishing:** Simplex / Duplex (Long-Edge & Short-Edge), N-Up (`1`, `2`, or `4` pages per sheet), Paper Size (`A4`, `Legal`, `Letter`), Orientation (`Auto`, `Portrait`, `Landscape`), Copies & Collation.
  - **Admin Override & Live Controls:** Counter staff can override any order's print settings before printing, switch active Windows printer queues, toggle Color/Duplex/Page-Range capabilities, and publish versioned pricing rules live.
- **SQLite WAL Database (`data/printaway.db`):** Powered by Windows native `winsqlite3.dll` with Write-Ahead Logging (`PRAGMA journal_mode=WAL;`).
- **Private PDF Vault (`data/private_vault_pdfs`):** Stores validated customer PDFs outside the web root using randomized tokens and SHA-256 verification.

---

## 2. 1-Click Launch & New PC / New Printer Setup

### Daily Startup (Start Local Server + Cloudflare Tunnel + Sync Netlify)
Simply double-click **[`start-station.bat`](start-station.bat)** in the root folder, or run:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-tunnel.ps1
```
This automatically:
1. Starts `bin\PrintAway.exe` on port `8080`.
2. Launches the Cloudflare HTTP/2 tunnel (`https://*.trycloudflare.com`).
3. Updates `web/index.html` and `web/admin.html` with the new tunnel URL and pushes to GitHub so `https://printitaway.netlify.app` connects automatically.

### Moving to a New Computer or Changing Printers (`setup-new-pc.bat`)
Whenever you move PrintAway to a new Windows PC or connect a different printer, double-click **[`setup-new-pc.bat`](setup-new-pc.bat)** or run:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-station.ps1
```
This wizard:
1. Verifies native Windows dependencies (`csc.exe`, `winsqlite3.dll`, Print Spooler).
2. Compiles `bin\PrintAway.exe` and runs all **10 automated self-tests**.
3. Lists all physical printers connected to the PC, lets you pick your active printer (or auto-detects it), and saves it to [`config/station.json`](config/station.json).

---

## 3. Useful Developer & Diagnostic Commands

```powershell
# Compile and run the 10-point automated integration test matrix
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-and-test.ps1

# Run read-only host & printer hardware diagnostics
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose-host.ps1

# Send a supervised physical 1-page test print to the active printer
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-print-proof.ps1 -SendTestPrint
```

- **Local Customer Web App:** http://127.0.0.1:8080/
- **Local Staff Admin Dashboard:** http://127.0.0.1:8080/admin (`admin` / `PrintAway@Admin2026`)
- **Netlify Customer Web App:** https://printitaway.netlify.app/
- **Netlify Staff Admin Dashboard:** https://printitaway.netlify.app/admin
