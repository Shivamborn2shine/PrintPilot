# PrintAway — Architecture & Product Decisions Log

**Last Updated:** 2026-11-04  
**Source of Truth:** [PRD.md](../PRD.md) (§1A, §8, §17, §18)  
**Current Scope:** Milestone 1 — Read-only host, printer, spooler, storage, and connectivity diagnostics.

---

## 1. Confirmed Founder & Product Decisions (§1A & §18)

| ID | Area | Status | Decision | Architectural & Operational Consequence |
|---|---|---|---|---|
| **D-01** | Brand | Working Selection | **PrintAway** ("Printing ke liye pareshan ho? Just PrintAway.") | Use PrintAway branding across UI/docs; Indian trademark clearance remains pending. |
| **D-02** | Launch Scope | **Confirmed** | **One shop now; support multiple printers later** | Model `shops`, `stations`, `printers`, `printer_queues`, and `printer_queue_bindings` in the database now, but activate exactly **one** verified physical printer queue for the pilot. |
| **D-03** | Customer Entry | **Confirmed** | **Both shop QR and remote website** | Mobile-first responsive customer web flow accessible via shop-specific QR deep link and public HTTPS URL. |
| **D-04** | Print Release Gate | **Confirmed (Hard Rule)** | **Mandatory admin approval before every print** | Payment verification transitions an order to `paid_awaiting_approval` — **never** directly to `queued` or print dispatch. Only an authenticated, authorized admin action can transition an order from `paid_awaiting_approval` to `approved` / `queued`. |
| **D-05** | Payments | **Confirmed** | **Online UPI via payment gateway** | Server-created payment order/intent + cryptographic webhook verification + server-side reconciliation. Never trust client redirects, frontend flags, or payment screenshots. |
| **D-06** | File Formats | **Confirmed** | **PDF only in v1** | Validate `%PDF-` header, MIME type, file size, encryption/password state, and page count on the server. Reject DOCX, images, and malformed or encrypted PDFs in v1. |
| **D-07** | Admin Experience | **Confirmed** | **Full admin dashboard** | Protected staff portal covering approval inbox, live order board, manual hold/reject/cancel/retry, payment/refund tracking, dynamic pricing rules, printer queue & health controls, analytics, and UTC audit logs. |
| **D-08** | Collection Flow | **Confirmed** | **Staff-managed counter pickup** | USB printer has an exposed output tray (not an unattended private kiosk). Staff verify physical completion (`ready_for_pickup`) and check the customer's pickup code before handover (`collected`). |
| **D-09** | Cost Strategy | **Confirmed** | **Free-first** | Zero mandatory cloud SaaS subscriptions during development/pilot; run web/API/worker and SQLite on the Windows host PC. |
| **D-10** | Document Storage & Processing | **Confirmed** | **Windows host PC is primary storage & processing node** | Store uploaded PDFs outside the web root (e.g., `%ProgramData%\PrintAway\documents`) with randomized file names, strict Windows ACLs, disk quotas, and automatic TTL cleanup. |
| **D-11** | Color & Paper Baseline | **Hardware Constraint** | **B&W (Monochrome) A4 only on Xerox B1025** | Xerox B1025 is a monochrome laser MFP. Color printing options must be hidden/disabled for this printer. Initial launch defaults to A4 simplex, copies 1–20; duplex and page ranges require physical verification first. |
| **D-12** | Job Concurrency | **Provisional** | **Serial printing (1 active print job; max 2 concurrent PDF validations)** | Constrained by the HP xw4600 workstation (~2.67 GHz Core2 Quad, 8 GB RAM) and single physical USB printer. |
| **D-13** | Timezone Handling | **Recommended** | **Store UTC; display `Asia/Kolkata` (IST)** | Host OS currently reports Pacific Time (`America/Los_Angeles`). All database and audit timestamps must use UTC (`YYYY-MM-DDTHH:MM:SSZ`), while customer/staff UI formats timestamps in `Asia/Kolkata`. |
| **D-14** | Sensitive Host Data | **Hard Rule** | **Redact machine & registration identifiers** | Never expose Windows Product ID, local Windows username, or raw machine hostnames in public UI, client bundles, public APIs, or unredacted exports. |

---

## 2. Local-First Architecture & Process Boundaries

### 2.1 Component Topology on Host PC
1. **Local Web & API Service (`127.0.0.1` only):**
   - Lightweight **Python FastAPI** backend serving static/mobile-first frontend assets (avoiding heavy Node/Next.js build/runtime overhead on the older HP xw4600 workstation).
   - Handles streaming PDF upload, PDF validation & page counting, server-authoritative quote calculation, UPI payment intent creation, signed webhook verification, admin authentication, and approval state transitions.
2. **Local Database (SQLite with WAL):**
   - Single-node transactional store with Write-Ahead Logging (`PRAGMA journal_mode=WAL;`), strict foreign keys, and schema migrations.
   - Enforces the state machine:
     `draft → validating → quoted → awaiting_payment → paid_awaiting_approval → approved → queued → leased → downloading → rendering → submitted_to_spooler → ready_for_pickup → collected`
   - Backed up nightly (encrypted) to off-host storage once configured.
3. **Private Document Vault (`%ProgramData%\PrintAway\documents`):**
   - Located outside any static web root or cloud-synced user folder (excluded from OneDrive/Desktop).
   - Per-order isolated directory with UUID/random filenames (never original user filenames), SHA-256 checksums, free-space quota gate prior to upload/payment, and automated retention purge after collection/expiry.
4. **Isolated Print Worker Process:**
   - Runs as a separate local worker process communicating with the local API via authenticated loopback requests (`POST /api/worker/claim`, `POST /api/worker/jobs/{id}/events`).
   - Claims **at most one** `queued` job at a time via a short-lived lease with heartbeat renewal.
   - Dispatches exclusively to the **single admin-verified Windows print queue** (never both Xerox USB001 queues).
   - Records the Windows Spooler Job ID (`Get-PrintJob`) before reporting `submitted_to_spooler`. If the worker crashes after spooler submission but before server acknowledgment, the job transitions to `reconciliation_required` — **never** automatically reprinted.

### 2.2 Safe Public HTTPS Tunnel Approach (Design Only — Not Deployed in Milestone 1)
- **No Inbound Port Forwarding:** The shop router and Windows firewall must **never** expose inbound ports (80/443/445/3389) or raw USB/printer shares to the Internet.
- **Outbound-Only Encrypted Tunnel:** Use a lightweight outbound HTTPS tunnel daemon (e.g., `cloudflared` named tunnel or equivalent authenticated reverse proxy) connecting from the Windows host to the edge network over outbound TCP/UDP 443.
- **Ingress Hardening Checklist (Pre-Phase 2 Gate):**
  - Terminate TLS 1.2/1.3 at the edge and bind local FastAPI strictly to `127.0.0.1:<port>`.
  - Enforce request body size limits at both proxy and FastAPI layers (e.g., 20 MB max per PDF upload).
  - Restrict `/api/worker/*` to local loopback only (block completely at public ingress rules).
  - Protect `/admin/*` and `/api/admin/*` with strong staff authentication (password + MFA / passkey), short session TTLs, CSRF tokens, and strict rate limiting.
  - Validate payment provider webhook signatures using raw request bodies and idempotency keys (`event_id`).
  - Honest Offline Behavior: Because the Windows host is the primary storage and processing node, if the host PC or internet connection is down, the edge/tunnel returns an explicit **"Shop Currently Offline — No Orders or Payments Accepted"** page rather than accepting unpersisted orders.

---

## 3. Open Blockers (§17A) — Must Be Resolved Before Dependent Features Activate

| # | Blocker | Blocks Milestone / Feature | Current State | Resolution Plan |
|---|---|---|---|---|
| **B-01** | **Physical Xerox Queue Selection** (`Xerox B1025 Multifunction Printer PCL6` vs `Xerox B1025 Multifunction Printer` on `USB001`) | Milestone 2 (Local Print Proof) & Milestone 4 (Worker Dispatch) | **RESOLVED (2026-11-04)** | Founder confirmed **`Xerox B1025 Multifunction Printer PCL6`** (`USB001`, `Xerox Global Print Driver PCL6`) is connected, loaded with paper, and manually verified. Standardized queue configuration to `PaperSize: A4`, `Color: False`, `DuplexingMode: OneSided`, `Collate: True`. The secondary queue (`Xerox B1025 Multifunction Printer`) is explicitly excluded/disabled in PrintAway. |
| **B-02** | **Verified Hardware Capabilities** (A4 tray status, hardware duplex unit behavior, A3 support) | Exposing Duplex / Page Range options in UI | **PENDING SUPERVISED TEST** | Xerox B1025 is an A4/Legal monochrome MFP (no A3). Duplex (`OneSided` currently configured on both queues) must be physically tested before enabling in customer UI. |
| **B-03** | **Shop Operating Hours, Supply Replenishment & Approval SLA** | Customer-facing turnaround messaging & auto-expiry | **PENDING FOUNDER INPUT** | Founder to confirm operating hours, who approves jobs at the counter, and max wait time before an unapproved paid order expires. |
| **B-04** | **Pricing Rules** (Per-side B&W rate, duplex rate, min order, taxes, refund rules) | Paid Checkout & Production Quotes | **PENDING FOUNDER INPUT** | Build configurable `price_rules` table with provisional dev defaults (e.g., ₹2/side simplex, ₹3/sheet duplex) until founder locks production rates. |
| **B-05** | **UPI Payment Gateway Selection & Merchant Onboarding** (Razorpay / Cashfree / PhonePe / Paytm) | Milestone 7 (Live UPI Payments) | **PENDING FOUNDER INPUT** | Implement gateway-agnostic payment adapter + mock signed webhook harness in local MVP until merchant keys are provisioned. |
| **B-06** | **Public Domain & Tunnel Provider Choice** | Milestone 6 (Public Host Connectivity) | **PENDING FOUNDER INPUT** | Keep all bindings on `127.0.0.1` during Milestones 1–5. |
| **B-07** | **Host Power, Sleep & Login Posture** | Unattended Daytime Operation | **OBSERVED / NEEDS HARDENING** | Host power scheme and sleep settings inspected in Milestone 1 diagnostics; workstation must not sleep during shop hours. |
| **B-08** | **Storage Quota, Retention Window & Off-Host Backup Target** | Accepting Real Customer PDFs | **PENDING FOUNDER INPUT** | Provisional default: 20 MB max per PDF, 24h post-collection retention, local `C:` free-space guard (>5 GB minimum reserve). Off-host backup destination required before live launch. |
| **B-09** | **Admin Identity & MFA Method** | Exposing Admin Approval UI Publicly | **PENDING FOUNDER INPUT** | Local MVP uses seeded admin account with strong password + TOTP MFA support ready for Phase 2. |
| **B-10** | **Customer Identity & Pickup Verification Policy** | Public Launch & Counter Handover | **PENDING FOUNDER INPUT** | Generate non-guessable tracking token + 6-character alphanumeric pickup verification code per order for staff counter check. |

---

## 4. Provisional Defaults Adopted for Local Development (§17B)

Until overridden by founder configuration:
- **Max Upload Size:** 20 MB per PDF file; max 100 pages per order.
- **Files per Order:** 1 PDF per order in initial slice (schema supports `order_items` for multi-file expansion).
- **Exposed Print Options (Initial):** Black & White only, Paper Size A4, Simplex (`OneSided`), Copies `1..20`. Duplex (`TwoSidedLongEdge` / `TwoSidedShortEdge`) and custom page ranges stay behind an admin capability toggle until Milestone 2 physical verification passes.
- **Admin PDF Preview Privacy:** Disabled by default (`metadata_only = true`); admin approval view shows filename (sanitized), page count, copies, sides, sheet count, quote amount, payment verification badge, and SHA-256 hash. Any emergency support preview requires explicit confirmation and writes an immutable `audit_events` entry.

