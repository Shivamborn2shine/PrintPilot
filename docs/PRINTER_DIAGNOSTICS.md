# PrintAway — Milestone 1 Host & Printer Diagnostics Report

**Date (UTC):** 2026-11-04  
**Scope:** Milestone 1 (Strictly Read-Only Diagnostics — No Print Jobs Dispatched, No Public Endpoints Created)  
**Diagnostic Script:** [`scripts/diagnose-host.ps1`](../scripts/diagnose-host.ps1)

---

## 1. Executive Summary & Critical Findings

1. **Single Physical Printer Behind Two Windows Queues (`USB001`) — Active Queue Locked:**
   - Two logical Windows printer queues point to the exact same `USB001` (`Dynamic Print Monitor`) port backed by one physical **Xerox B1025 Multifunction Printer** (`USB\VID_0924&PID_42E7&MI_01`):
     - **Queue A (SELECTED & VERIFIED):** `Xerox B1025 Multifunction Printer PCL6` (`Xerox Global Print Driver PCL6`, v3 x64) — Founder confirmed this queue is physically loaded and manually tested.
     - **Queue B (EXCLUDED / DISABLED IN PRINTAWAY):** `Xerox B1025 Multifunction Printer` (`Xerox B1025 Multifunction Printer V4 PCL6`, v4 x64) — Explicitly ignored by PrintAway to prevent duplicate jobs.
2. **Paper Size & Monochrome Standardization Applied (`A4`, `Color: False`, `OneSided`):**
   - Initially, both Xerox print queues reported `PaperSize: Letter` due to the US OS timezone/locale (`Pacific Standard Time`), and Queue A had `Color: True`.
   - **Resolved (2026-11-04):** Standardized `Xerox B1025 Multifunction Printer PCL6` via `Set-PrintConfiguration` to **`PaperSize: A4`**, **`DuplexingMode: OneSided`**, **`Color: False`**, and **`Collate: True`**. PrintAway's print worker also explicitly enforces `A4` and monochrome per job ticket.
3. **Configuration Divergence Between the Two Xerox Queues:**
   - `Xerox B1025 Multifunction Printer PCL6` (v3 Global Driver) has `DuplexingMode: OneSided` and `Color: true` (despite B1025 being monochrome hardware).
   - `Xerox B1025 Multifunction Printer` (v4 Model-Specific Driver) has `DuplexingMode: TwoSidedLongEdge` and `Color: false`.
   - **Implication:** Sending a multi-page PDF to `Xerox B1025 Multifunction Printer` without overriding `DuplexingMode` would default to two-sided printing, whereas `Xerox B1025 Multifunction Printer PCL6` defaults to single-sided. Both queues must be tested under human supervision in Milestone 2 to pick the single canonical queue and verify deterministic simplex/duplex control.
4. **Memory Headroom Constraint (~2.0 GB Free RAM):**
   - The host (`HP xw4600 Workstation`, Intel Core2 Quad Q9400 @ 2.66 GHz) has `7.98 GB` total RAM with **~`2.00 GB` free** during baseline inspection.
   - **Implication:** Confirms PRD §2 & §8 — avoid heavyweight Node.js/Next.js server processes on the host; use a single lightweight Python FastAPI server + SQLite (WAL) + strictly serial PDF rendering/printing (`1` active print job at a time).
5. **Ample Local Storage Across Multiple NTFS Volumes:**
   - `C:` has **145.91 GB free** (of 195.31 GB), `D:` has **159.63 GB free**, and `E:` has **540.77 GB free**. Local PDF storage (`%ProgramData%\PrintAway\documents`) and local encrypted backup staging have plenty of disk capacity.

---

## 2. Host Hardware, OS & Power Baseline

*Note: Per PRD §2 sensitive-data handling rules, Windows Product ID, user account names, and private machine registration identifiers are omitted.*

| Metric | Observed Value | Operational Assessment |
|---|---|---|
| **OS & Build** | Microsoft Windows 11 Pro (`10.0.22621`, Build `22621`, 64-bit) | Compatible with Win32 Spooler APIs, `PrintManagement` cmdlets, and modern Python 3.x x64. |
| **Hardware Model** | Hewlett-Packard `HP xw4600 Workstation` | Legacy workstation hardware; requires low-memory, low-CPU background processes. |
| **CPU** | `Intel(R) Core(TM)2 Quad CPU Q9400 @ 2.66GHz` (4 cores / 4 logical processors) | Cap concurrent PDF validation to at most 2 workers; keep print rendering strictly serial (1 job at a time). |
| **Memory (RAM)** | `7.98 GB` Total / **`2.00 GB` Free** at observation | Tight free RAM margin; enforce upload size limit (20 MB) and stream file writes to disk rather than buffering in RAM. |
| **System Timezone** | `Pacific Standard Time` (UTC-8 / UTC-7) | Backend database and audit logs must store **UTC** (`YYYY-MM-DDTHH:MM:SSZ`); customer/admin UI must format times in `Asia/Kolkata` (`IST`, UTC+05:30). |
| **Active Power Scheme** | `Balanced` (`381b4222-f694-41f0-9685-ff5bb260df2e`) | **Risk:** Balanced power profile may allow system standby or USB selective suspend during idle shop periods. Must verify/disable AC sleep timeout before live operations. |
| **Network Interface** | `Ethernet` (`Broadcom NetXtreme Gigabit Ethernet`, `Up`, `1 Gbps`) | Wired Gigabit Ethernet link is active (`Up`). |
| **Print Spooler Service** | `Spooler` — Status: `Running`, StartType: `Automatic`, Account: `LocalSystem` | Healthy and set to start automatically on boot. |

---

## 3. Storage & Disk Capacity Audit

| Drive | File System | Total Size (GB) | Free Space (GB) | Free % | Recommended Role |
|---|---|---:|---:|---:|---|
| `C:` | NTFS | 195.31 | **145.91** | 74.7% | OS, application runtime, SQLite WAL database, and primary private document vault (`%ProgramData%\PrintAway\documents`). |
| `D:` | NTFS | 195.21 | **159.63** | 81.8% | Available secondary local volume. |
| `E:` | NTFS | 540.88 | **540.77** | 100.0% | Ideal local staging volume for encrypted nightly database/file snapshots prior to off-host backup sync. |
| `F:` | NTFS | 0.10 | 0.08 | 85.7% | System reserved partition — **do not use**. |
| `G:` | NTFS | 73.00 | **72.91** | 99.9% | Spare volume. |

---

## 4. Windows Printer Queues, Drivers & Ports Inventory

### 4.1 Installed Printer Queues (`Get-Printer` + `Get-PrintConfiguration` + `Win32_Printer`)

| Queue Name | Driver Name | Driver Ver | Port | Default | WorkOffline | Status | Jobs | PaperSize | DuplexingMode | Color Flag | Classification |
|---|---|---|---|---|---|---|---:|---|---|---|---|
| `Xerox B1025 Multifunction Printer PCL6` | `Xerox Global Print Driver PCL6` | v3 (x64) | `USB001` | `True` | `False` | `Normal` | 0 | `Letter` | `OneSided` | `true` | **Candidate A (Physical USB001)** |
| `Xerox B1025 Multifunction Printer` | `Xerox B1025 Multifunction Printer V4 PCL6` | v4 (x64) | `USB001` | `False` | `False` | `Normal` | 0 | `Letter` | `TwoSidedLongEdge` | `false` | **Candidate B (Physical USB001)** |
| `Send To OneNote 16` | `Send to Microsoft OneNote 16 Driver` | v4 (x64) | `nul:` | `False` | `False` | `Normal` | 0 | `Letter` | `OneSided` | `true` | **Excluded (Virtual)** |
| `Microsoft Print to PDF` | `Microsoft Print To PDF` | v4 (x64) | `PORTPROMPT:` | `False` | `False` | `Normal` | 0 | `Letter` | `OneSided` | `true` | **Excluded (Virtual)** |

### 4.2 USB & PnP Enumeration (`Get-PnpDevice`)

| Class | Friendly Name | Status | Instance ID |
|---|---|---|---|
| `USB` | `Xerox B1025 Multifunction Printer` | `OK` | `USB\VID_0924&PID_42E7&MI_01\6&1A932EAD&0&0001` |
| `Printer` | `Xerox B1025 Multifunction Printer V4 PCL6` | `OK` | `USBPRINT\XEROXB1025_MULTIFUNCTION_PRINTER\7&2A0874C3&0&USB001` |
| `PrintQueue` | `Xerox B1025 Multifunction Printer` | `OK` | `SWD\PRINTENUM\{989A8111-61C1-434F-B0F0-AC107960CB42}` |
| `PrintQueue` | `Xerox B1025 Multifunction Printer PCL6` | `OK` | `SWD\PRINTENUM\{1C7D8076-204F-4D96-869C-DE97A424E670}` |

**Hardware Verification Note:**
- USB Vendor ID `0x0924` (Xerox) / Product ID `0x42E7` (B1025) is actively enumerated (`Status: OK`) and bound to `USB001` (`Dynamic Print Monitor`).
- However, per PRD §2 & §4.1, `PrinterStatus: Normal` in Windows WMI/PrintManagement only indicates that the queue is not paused or in an active OS-level error state — it **does not** guarantee that paper is loaded, toner is sufficient, or that a given driver renders without stalling.

---

## 5. Supervised Queue Validation Protocol (Prerequisite for Milestone 2)

Before any automated print worker code is enabled, an operator must be physically present at the **Xerox B1025** to run the **Milestone 2 Supervised Queue Validation**:

1. **Physical Pre-Check at Printer:**
   - Confirm **A4 plain paper** is loaded in the main tray and the paper guides are set to **A4**.
   - Check the Xerox B1025 front panel / status display for any paper mismatch, jam, or low-toner warning.
2. **Standardize Queue Paper Size to `A4`:**
   - Change the test queue's paper size from `Letter` to `A4` (`Set-PrintConfiguration -PrinterName <SelectedQueue> -PaperSize A4`) so the driver does not request US Letter paper.
3. **Candidate Queue Comparison (Supervised 1-Page Test):**
   - **Candidate B (`Xerox B1025 Multifunction Printer` — V4 model-specific driver, `Color: false`):** Recommended to test first because it uses the dedicated Xerox B1025 V4 PCL6 driver (`USBPRINT\XEROXB1025_MULTIFUNCTION_PRINTER`) and accurately reports monochrome (`Color: false`).
   - **Candidate A (`Xerox B1025 Multifunction Printer PCL6` — V3 Xerox Global Print Driver):** Test second or if Candidate B exhibits rendering/spooling quirks with headless PDF printing.
4. **Test Matrix to Record in Milestone 2:**
   - Test 1: 1-page A4 B&W PDF (`1 copy`, `OneSided`). Verify exact spooler Job ID capture and single physical sheet output.
   - Test 2: 2-page A4 B&W PDF (`1 copy`, `OneSided` vs `TwoSidedLongEdge`). Verify whether hardware duplex works cleanly on this B1025 unit.
   - Test 3: Lock the winning queue name in configuration and mark the non-selected Xerox queue as **disabled/ignored** in PrintAway so duplicate jobs can never occur.

---

## 6. Reproducing Diagnostics

Run the read-only PowerShell script from the repository root at any time:

```powershell
# Human-readable table summary
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose-host.ps1

# Structured JSON output for automated inspection
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose-host.ps1 -AsJson
```
