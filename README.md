# PrintAway — Self-Service Print Shop Software Layer

**Brand Line:** *"Printing ke liye pareshan ho? Just PrintAway."*  
**Value Proposition:** Upload. Configure. Pay. Print. Collect.  
**Active Pilot Queue:** `Xerox B1025 Multifunction Printer PCL6` (`USB001` — Standardized to ISO A4, Monochrome B&W, Simplex/Duplex)

---

## 1. Architecture Overview (Local-Host-First)

PrintAway runs natively on the Windows 11 Pro host workstation (`HP xw4600`, 8 GB RAM) with **zero external runtime or cloud dependencies**:
- **Local Web & API Server (`127.0.0.1:8080`):** Serves the mobile-first Customer Portal (`/`) and protected Staff Admin Dashboard (`/admin`), enforcing the mandatory admin approval gate.
- **SQLite WAL Database (`data/printaway.db`):** Powered by Windows 11 native `winsqlite3.dll` with Write-Ahead Logging (`PRAGMA journal_mode=WAL;`) and strict transactional state transitions.
- **Private PDF Vault (`data/private_vault_pdfs`):** Stores validated customer PDFs outside the web root using randomized UUID filenames, SHA-256 verification, free-disk quota checks, and automatic deletion upon counter collection.
- **Verified Print Worker:** Claims at most **1 active print job at a time** via short-lived leases, correlates Windows Spooler Job IDs (`Win32_PrintJob`), and transitions any interrupted render/spool job to `reconciliation_required` (never blind automatic reprinting).

### Enforced Order State Machine (PRD §1A & §7)
```text
draft → validating → quoted → awaiting_payment → paid_awaiting_approval
  → approved → queued → leased → downloading → rendering
  → submitted_to_spooler → ready_for_pickup → collected
```
- **Hard Rule:** Verified UPI payment webhooks (`POST /api/payments/webhook`) transition orders **only** to `paid_awaiting_approval`. A job is never queued or printed until an authenticated admin explicitly approves it (`POST /api/admin/orders/{id}/approve`).

---

## 2. Quick Start Commands

### Run Read-Only Host & Printer Diagnostics (Milestone 1)
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\diagnose-host.ps1
```

### Run PDF Validation & Queue Ticket Proof (Milestone 2)
```powershell
# Safe dry-run proof (validates sample PDFs and verifies A4 monochrome ticket on Xerox B1025 PCL6)
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-print-proof.ps1

# Supervised physical 1-page test print on Xerox B1025 Multifunction Printer PCL6
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-print-proof.ps1 -SendTestPrint
```

### Build & Run Automated PRD §14 Integration Test Suite (Milestones 3–5)
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-and-test.ps1
```

### Start Local PrintAway Server & Print Worker
```powershell
# Start in safe spooler simulation mode (for UI testing without wasting physical paper)
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-printaway.ps1

# Start with LIVE physical dispatch to 'Xerox B1025 Multifunction Printer PCL6'
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-printaway.ps1 -RealPrint
```

- **Customer Web App:** http://127.0.0.1:8080/
- **Staff Admin Dashboard:** http://127.0.0.1:8080/admin (`admin` / `PrintAway@Admin2026`)

