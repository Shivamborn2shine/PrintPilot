# PrintAway — Product Requirements Document (PRD)

**Version:** 0.2 (founder decisions incorporated)  
**Date:** 2026-11-04  
**Status:** Requirements aligned — critical operational decisions pending  
**Audience:** Founder, product designer, developer, Antigravity coding agent  
**Mission:** Turn existing print-shop printers into a self-service printing experience, reducing routine operator involvement from file receipt through job submission.

> This document specifies the intended product, not a claim that every function already exists. All assumptions are labeled. Do not treat unconfirmed product choices as approvals.

## 1. Product summary

PrintAway is a customer-facing web application and a lightweight Windows agent. Customers upload PDFs, configure their print requirements, receive a quote, pay online through UPI, and track the order. **Payment verification does not authorize immediate printing**: a staff admin must explicitly approve each eligible, paid order. The Windows host stores documents and runs local job processing, and a securely controlled print worker submits approved jobs to the verified physical printer without staff opening each file. The first pilot uses a **Xerox B1025** on **DESKTOP-LJKG4N6**, not the previously discussed Canon G2010.

**Brand line (draft):** “Printing ke liye pareshan ho? Just PrintAway.”  
**Value proposition:** Upload. Configure. Pay. Print. Collect.  
**Product distinction:** A low-cost software layer for existing printers, not necessarily new kiosk hardware.

### Explicit operational limitation

A USB printer with an exposed output tray is **not** a fully unattended private print kiosk. Even with automatic submission, someone may need to replenish paper/toner, clear jams, supervise physical access and ensure the correct customer collects the paper. The pilot claims **operatorless initiation for routine print jobs**, not guaranteed secure 24/7 unattended pickup.

## 1A. Confirmed founder decisions — 2026-11-04

| Topic | Confirmed decision | Architectural consequence |
|---|---|---|
| Launch scope | **One shop now; support multiple printers later** | Design printer/station tables now, activate exactly one verified physical device in pilot |
| Customer entry | **Both shop QR and remote website** | Responsive customer flow, shop-specific QR/deep link and public HTTPS access |
| Print release | **Admin approval required** | No agent dispatch until an authorized admin approves; paid orders stay pending |
| Payments | **Online UPI** | Server-created payment intent/order and verified gateway webhook; no screenshot-based confirmation |
| File formats | **PDF only** | Parse and validate PDFs; reject everything else; no DOCX/office conversion in v1 |
| Admin | **Full dashboard** | Orders, approvals, payments, prices, printer health, alerts, analytics, staff/audit controls |
| Collection | **Staff pickup** | Operator verifies pickup code; customer never assumes unattended confidential output |
| Cost | **Free-first** | Optimize for no subscription in dev; distinguish free tiers from production guarantees and gateway fees |
| Local host | **Host PC provides document storage and operational processing** | Keep canonical PDFs and working files on Windows host; avoid mandatory cloud object storage; support remote connectivity safely |

**Agreed transaction order:** `upload → validate → quote → customer confirms → create payment → UPI payment verified → awaiting_admin_approval → admin approves → queued → printed/submitted with evidence → staff marks ready → staff hands off to customer → collected`. Admin may reject or hold a paid order; refunds are handled under an explicit policy. The system must never print merely because a payment webhook arrived.

**Storage interpretation:** The Windows host is the primary storage and job-processing node. A *minimal public ingress/control mechanism* is still needed for remote browsers and payment-provider webhooks. Public remote access cannot work while the shop PC or its Internet connection is offline unless a separate cloud component temporarily accepts orders/uploads. For v1, offline remote orders are disabled or clearly rejected rather than silently pretending cloud availability.

## 2. Known pilot environment

| Attribute | Observed from supplied system output | Implication |
|---|---|---|
| Host | `DESKTOP-LJKG4N6` | Windows agent target identifier (not a public endpoint) |
| OS | Windows 11 Pro, build 22621, x64 | Use Windows-compatible printer APIs and service/task startup |
| Hardware | HP xw4600 Workstation | Older hardware; avoid heavy local web stacks or unnecessary background apps |
| CPU | Intel64 Family 6 Model 23, ~2.67 GHz | Pilot with serial/low-concurrency job rendering |
| RAM | ~8 GB installed, ~2.7 GB free at observation | Cap PDF render processes, disk usage, and local queues |
| Printer | Xerox B1025 Multifunction Printer | Monochrome laser; do **not** offer color printing on this station |
| Port | `USB001` | Agent controls Windows queues locally; do not expose USB device over Internet |
| Queue A | `Xerox B1025 Multifunction Printer PCL6` | Xerox Global Print Driver PCL6; candidate only |
| Queue B | `Xerox B1025 Multifunction Printer` | Xerox B1025 V4 PCL6; candidate only |
| Print queue state | Both show `Normal`, 0 jobs when checked | Does not prove that either driver prints correctly |
| Time zone | System reports Pacific Time | Verify Windows timezone; use UTC in backend, Asia/Kolkata for customer-facing timestamps if Indian deployment |

**Important:** Both Xerox entries point to the same `USB001` port. They may be two Windows logical queues to **one physical Xerox printer**, not two printers. Agent must identify one verified queue by exact name; never dispatch to both by default. OneNote and Microsoft Print to PDF must be excluded from customer-accessible printer choices.

**Xerox B1025 capability baseline (verify physical configuration):** black-and-white laser printing, up to 25 A4 pages/minute single-sided, two-sided printing support, USB and Ethernet. Capabilities and installed accessories should be detected from the printer/driver and confirmed with a test page; do not infer live paper, toner or error status from `PrinterStatus: Normal` alone.

**Sensitive-data handling:** Do not place Windows registration Product ID, account name, or machine identifiers into public-facing UI, frontend bundles, logs, or analytics. Treat the machine name as private configuration.

## 3. Users and jobs to be done

1. **Customer (student / walk-in / remote):** “I need a printout with the correct settings, clear price, and minimal waiting.”
2. **Shop owner/admin:** “I need one place to manage prices, orders, printer status, payments, refunds, and printing errors.”
3. **Operator (exception-only):** “I intervene for jams, supply replacement, disputes, and physical collection exceptions, not routine file printing.”
4. **Future multi-shop operator:** “I need isolated shop accounts, stations, staff permissions, and auditable reports.”

## 4. User journeys

### 4.1 Standard customer order — approved public flow

1. Open PrintAway via short link, QR code, or selected shop page.
2. See shop identity, current acceptance status, approximate turnaround, and services.
3. Upload one or more documents (PDF only in first implementation; other formats later).
4. Server validates file size, MIME/type, extension, virus scan, PDF integrity, page count, encrypted-PDF state, and safe preview eligibility.
5. Select service: B&W; A4; simplex/duplex if verified supported; copies; pages/all-pages; optional orientation (auto default).
6. Show calculated **sheet count and printed-side count** and transparent, admin-configured price. Quote must be server-authoritative, not solely client-calculated.
7. Review order and privacy/collection instructions. Obtain consent for data retention.
8. Create a pending order and a gateway payment intent; redirect/launch UPI payment flow. Never trust screenshots, browser redirects, or client-side paid flags.
9. After a **server-verified payment webhook and reconciliation**, mark `paid_awaiting_approval` and notify admin. An admin must inspect metadata and **explicitly approve or reject** the order. A paid order must not auto-print.
10. On approval, queue the job for the selected verified physical printer; if rejected, follow audited refund/cancellation policy.
11. Windows agent claims the job with a lease, downloads it using a short-lived authorized URL, and renders/prints via the verified Windows queue.
12. UI shows honest statuses. **Windows accepted** does not mean **physically completed**. Operator or trusted hardware telemetry must verify final completion.
13. Customer receives pickup identifier and instructions; status becomes `ready_for_pickup` only when verified by configured policy.
14. At the counter, staff verify the pickup code and hand over physical pages; after collection, mark `collected`; delete uploaded content according to retention policy.

### 4.2 Offline or failure flow

- If host PC/agent offline, the remote customer upload and checkout flow must be marked unavailable in the local-host-only design; no charge must be taken for an order that the host cannot persist.
- If printer jams/runs out of paper/toner, stop dispatch, preserve affected order, notify admin and do not automatically duplicate printing.
- If process crashes after sending a job to Windows but before acknowledging server, mark `reconciliation_required` until correlated with the spooler or manually resolved; do not blindly resend.
- If payment succeeded but print cannot be fulfilled, admin can refund/reassign with full audit trail.

### 4.3 Future secure release flow

For public/unattended terminals, retain paid jobs until the customer arrives, scans a station QR and authenticates using an expiring code. Only then release to print; investigate physical output locks/dispensers before claiming privacy or 24/7 unattended operation.

## 5. Scope and phases

### Phase 0 — hardware and driver proof (must pass before cloud automation)

- Audit physical printer and Windows queues; identify actual queue that prints reliably.
- Print one-page PDF, 10-page PDF, multiple copies, then verified duplex and selected page range.
- Confirm Windows account/agent permissions, startup behavior, sleep settings and USB reconnect behavior.
- Record actual consumables, supported page sizes, print settings and output tray procedure.
- Baseline CPU/RAM/disk under a 20-page test job.

### Phase 1 — functional local MVP (admin approval included)

**Must:** customer upload (PDF), page counting, B&W A4, copies, basic admin-set pricing, order creation, **mandatory admin approval gate**, persistent FIFO queue, one active job at a time, Windows worker, printer mapping, honest statuses, admin hold/cancel/retry, basic logs, local admin dashboard, file deletion, basic access controls.  
**May:** duplex and page ranges if validated end-to-end.  
**Not yet:** public deployment, online payments, multi-tenancy, color printing, DOCX conversion, unattended physical pickup.

### Phase 2 — secure customer-facing public beta (local-host-first)

- Secure public HTTPS access to the host-based application through an outbound tunnel or another authenticated ingress proxy; local encrypted-at-rest PDF storage and local database initially. A hosted control plane/database/object store is **optional** for reliability or future scale, not mandatory for pilot. Authentication/guest OTP or an approved alternative, upload scanning, rate limits and abuse controls are required.
- Separate local host API/service from print worker, both protected by least privilege and authenticated IPC. If using an outbound tunnel, **no router port forwarding or publicly exposed Windows services**. Restrict ingress by hostname, allowlists where applicable, API authentication and rate limits.
- Customer tracking, mobile UI, **full admin dashboard**, notifications, operational reporting, integrated **UPI payment gateway** (provider yet to be chosen), verified webhooks, admin approval queue, cancellation/refunds.
- Availability-aware ordering (show closed/offline and optionally accept delayed orders with explicit consent).

### Phase 3 — advanced operations and SaaS

- Multi-shop tenancy, per-shop pricing and branding, multiple printers per shop, capability-aware routing, load balancing, staff roles, batch jobs, scheduled release, reconciliation, metrics and billing.
- Optional QR pickup/secure release and dedicated kiosks; DOCX/images with sandboxed conversion; large format only where a device supports it.

## 6. Functional requirements and acceptance criteria

### Customer experience

| ID | Requirement | Priority | Acceptance criteria |
|---|---|---|---|
| C01 | Mobile-first branded landing/shop page | P0 | Loads on common Android phones; clear shop, service and status |
| C02 | PDF-only uploads for initial MVP | P0 | Reject malformed, encrypted or oversized PDFs with actionable messages |
| C03 | Actual PDF page counting | P0 | Server count matches sample documents; cannot be edited by customer |
| C04 | Print options | P0 | Copies 1–20, A4 B&W, simplex; unsupported options hidden |
| C05 | Duplex/page selection | P1 | Enabled only after printer/driver verification, validated and test-printed |
| C06 | Price breakdown | P0 | Server totals based on sides, sheets, copies and shop pricing rules; no silent charges |
| C07 | Order tracking | P0 | Non-guessable link; status updated without leaking others' orders |
| C08 | UPI online payment | P0 for paid beta | Paid status only from verified provider webhook plus reconciliation; idempotent updates |
| C09 | Multi-file upload | P1 | File-specific counts/options; total reproducible and auditable |
| C10 | File preview | P1 | Safe PDF preview and user confirmation before release |

### Agent and printing

| ID | Requirement | Priority | Acceptance criteria |
|---|---|---|---|
| A01 | Discover Windows printer queues | P0 | Excludes virtual printers; reports queue/driver/port accurately |
| A02 | Explicit admin-selected queue | P0 | No automatic printing to both Xerox queues |
| A03 | Outbound-only authenticated agent | P0 | No inbound port required; unpaired agent cannot fetch jobs |
| A04 | Queue leases/heartbeats | P0 | Only one agent claims a job; stale claims reconciled |
| A05 | Deterministic print rendering | P0 | Selected options applied to test PDFs; physical output inspected |
| A06 | Windows job ID correlation | P0 | Server stores mapped Windows spooler ID when available |
| A07 | Fault recovery | P0 | Crash after dispatch never triggers an unreviewed duplicate print |
| A08 | One active print job | P0 | Jobs print sequentially on pilot machine |
| A09 | Start on Windows boot | P1 | Agent reconnects after reboot and network interruption |
| A10 | Device heartbeat | P0 | UI clearly distinguishes online agent from printer-ready state |

### Admin and operations

| ID | Requirement | Priority | Acceptance criteria |
|---|---|---|---|
| M01 | Admin authentication | P0 | Customer cannot access admin routes or orders |
| M02 | Order approval and management | P0 | Paid orders require explicit authenticated approve/reject; search, hold, cancel, retry with reason and audit log |
| M03 | Pricing configuration | P0 | Changing prices affects new quotes, not confirmed past orders |
| M04 | Printer controls | P0 | Test print, pause intake, view queue, diagnostics |
| M05 | Audit logs | P0 | Each order/agent/admin transition timestamped in UTC |
| M06 | Refund/reconciliation management | P1 | Failed paid orders resolved without duplicate charges or prints |
| M07 | Full admin dashboard and analytics | P0 for beta | Orders, approval queue, payment reconciliation, printed sides, revenue, failure rates, station health and downtime |

## 7. Job status model — admin approval gate

**Customer/payment order states:**

`draft → validating → quoted → awaiting_payment → paid_awaiting_approval → approved → queued → leased → downloading → rendering → submitted_to_spooler → ready_for_pickup → collected`.

**Alternative transitions:** `paid_awaiting_approval → rejected_refund_pending → refunded`; `paid_awaiting_approval → on_hold`; `awaiting_payment → payment_failed / expired`; and controlled `failed / reconciliation_required / cancelled` transitions.

**Hard rules:** (1) `paid_awaiting_approval` cannot transition directly to `queued`; (2) only an authorized admin can approve; (3) only a verified, non-refunded paid order can be approved; (4) an order cannot be approved twice or result in duplicate dispatch; (5) **spooler submitted ≠ paper printed**, so staff confirms physical readiness; (6) every transition is audited with UTC timestamp and actor.

## 8. Architecture — Windows host as primary infrastructure

```text
Customer phone / QR / remote browser
                  │ HTTPS
                  ▼
         Public HTTPS hostname
    (secure outbound tunnel or proxy)
                  │ encrypted tunnel; no router port forward
                  ▼
  HP xw4600 / Windows 11 — DESKTOP-LJKG4N6
  ┌──────────────────────────────────────────┐
  │ Web UI + secured API                     │
  │ Local DB (SQLite initially)             │
  │ Private PDF files on local disk         │
  │ PDF validation and page counting        │
  │ Payment webhook verification            │
  │ Admin approval + audited job queue      │
  │ PrintAway worker (separate process)     │
  └───────────────────┬──────────────────────┘
                      │ verified Windows queue only
                      ▼
                USB001 / Xerox B1025
                      │
                      ▼
                  Staff pickup

Payment provider (UPI) ─ HTTPS verified callback ─► Public ingress ─► Host API
```

### Decisions and deployment boundaries

1. **Host as source of truth for print files and operational data.** Private PDFs remain in a designated Windows data directory outside the web root (e.g. `%ProgramData%\PrintAway\documents`) with ACLs, encryption at rest where feasible, per-order directories, disk quotas and TTL deletion. No raw PDFs in public URLs or diagnostic exports.
2. **Local-first database:** SQLite with WAL and migrations for one-shop, low-write-volume pilot; nightly encrypted backups to separately protected storage, tested restore. Change to PostgreSQL when concurrency/multi-machine scale warrants it. A host-only DB is lost if the disk fails without backups.
3. **Public access:** A secure outbound HTTPS tunnel is a candidate (for example Cloudflare Tunnel, if compatible with chosen domain/plan and provider policy). Validate the actual tunnel's upload-size limits, uptime, webhook reachability and pricing. Never use raw router port forwarding. A free public deployment is **not** a production availability guarantee.
4. **Payments:** Hosted Indian payment gateway/UPI checkout; verify signatures, event IDs, amount, currency, order ownership, final status and idempotency; reconcile with provider server-side when webhooks are missed. Never start a print based on customer screenshots or app redirects.
5. **Admin:** Separate protected staff routes, strong authentication (MFA recommended), role-based authorization, CSRF protection, audit trail, session timeout. Approval is an explicit action showing quote, pages, copies, payment state and targeted printer.
6. **Printer processes:** Prefer isolated, headless worker under restricted Windows user; select exactly one queue from both installed Xerox queues, test print quality and no duplicate output. If an agent is later installed on remote shop PCs, connect it via outbound authenticated HTTPS to a separate central control plane.
7. **Host downtime:** With no cloud buffering, no new remotely uploaded orders are accepted while host is offline. Existing paid orders must remain durable locally; API/site must communicate closed/offline honestly. Prevent paid checkout when printer/job intake unavailable unless user explicitly accepts a delayed service and reliable off-host order capture has been added.
8. **Local performance:** One active print job, bounded PDF processing, configured storage limits and observability; no heavyweight background services on the old HP machine.
9. **Future multiple physical printers:** Maintain `printers`, `capabilities`, `printer_queue_bindings`, and `jobs.target_printer_id`; separate physical printer identity from Windows logical queue. Only expose verified B&W/duplex/A3 etc. settings per device.

### Implementation stack (provisional; subject to one decision)

- **Web UI:** Next.js/TypeScript/Tailwind or lighter local-served UI if machine benchmarking favors it. Avoid running a heavy Next.js production compiler on the old workstation; static web assets + local FastAPI is a strong option.
- **API + jobs:** Python FastAPI, local SQLite (WAL), transactional job queue and auth; local-only FastAPI bound to `127.0.0.1` behind trusted HTTPS reverse tunnel/proxy.
- **Print worker:** Python with pywin32 and a vetted headless PDF renderer/printer path; run as a scheduled task or restricted Windows service only after actual testing.
- **Files:** Validated local PDFs; safe rendering; Windows filesystem ACLs and encryption/backup plan.
- **Deployment:** Host PC serves application for the pilot; public HTTPS endpoint via outbound tunnel. No mandatory Vercel/Supabase subscription or cloud file storage for v1.

### Initial API sketch

- `POST /api/uploads` — authenticated/limited streaming PDF upload, validate and store locally.
- `POST /api/orders/quote` — server-authoritative price and page count.
- `POST /api/orders` — create awaiting-payment order.
- `POST /api/payments/create` — server-created UPI checkout order.
- `POST /api/payments/webhook` — signed verified payment events, idempotent state changes.
- `GET /api/orders/{public_token}` — minimal status with unguessable token.
- `GET /api/admin/orders?state=paid_awaiting_approval` — admin review queue.
- `POST /api/admin/orders/{id}/approve` — authorized, audited, paid-only transition to queued.
- `POST /api/admin/orders/{id}/reject` — audited rejection and refund workflow.
- `POST /api/admin/orders/{id}/ready` — physical-output verified, staff confirms pickup readiness.
- `POST /api/admin/orders/{id}/collected` — staff enters/verifies pickup code.
- `GET /api/admin/printers` — queue selection, capability probes, health, diagnostics.
- `POST /api/worker/claim` — local/authenticated atomic lease, one job per physical device.
- `POST /api/worker/jobs/{id}/events` — spooler ID, output status, reconciliation evidence.

## 9. Suggested data entities

- `shops` (single active shop now; multi-tenant-ready)
- `users` / `staff_roles`
- `stations` (local host identity, worker heartbeat, future remote agents)
- `printers` / `printer_queues` (physical device ID separately from Windows queue name, driver, USB port, tested capabilities, enabled)
- `files` (private host filesystem path/key, SHA-256, page count, size, scan status, retention deadline)
- `price_rules` (version, print mode, simplex/duplex, min charges, taxes if required)
- `quotes` (immutable calculated breakdown, expiry)
- `orders` / `order_items` (customer options, quote version, status, collection code)
- `print_jobs` (job state, assigned station, lease token, attempts, idempotency key)
- `agent_events` / `audit_events` (timestamp, actor, details, redacted metadata)
- `payments` / `refunds` (UPI gateway order IDs, verified webhook event IDs, reconciliation)
- `approvals` (authorized admin, decision, UTC timestamp, reason, target device, price/payment snapshot)

## 10. Pricing model — needs founder choice

- B1025 is **monochrome**: define B&W **per printed side**, not per physical sheet, unless specified otherwise.
- Distinguish simplex (one side per sheet) from duplex (up to two sides per sheet).
- Example: 5-page PDF × 2 copies simplex = 10 printed sides, 10 sheets; duplex = 10 printed sides, **6 sheets** if each copy starts on a fresh sheet.
- Support `quantity × per-side-rate + fixed fees - discounts + applicable tax`, with round-off policy.
- **UPI online is confirmed;** actual rates, minimum order, tax treatment, payment provider and refund/collection rules remain pending.

## 11. Privacy, security and safeguards

- HTTPS everywhere; no exposed PC/printer ports, remote desktop access, or default credentials.
- Restrict uploads by type, size, page count and scan; inspect actual file signature. Sandboxed renderer; no embedded PDF JavaScript execution.
- Strong admin authentication (MFA recommended), least-privilege worker identity, rotation/revocation and protected Windows access. Device-specific pairing applies when future remote agents are introduced.
- **Host-local private storage** outside public document root; restricted Windows ACLs, disk encryption where feasible, encrypted off-host backups with restore tests. No PDF contents in logs; redact filename/customer details by default.
- Automatic deletion of host-stored source documents after the agreed retention window; backups also need deletion/expiry policies. Audit metadata can be retained separately.
- Rate limiting, CAPTCHA or equivalent abuse defenses, quotas and payment-before-release if public.
- Signed payment webhook validation; prices recalculated on the server; no trust in frontend-generated payment flags.
- Explicit rules for copyrighted, prohibited or abusive print content; complaint and takedown path.
- Preview privacy: the shop operator should not see sensitive document contents except when necessary and authorized.
- Never silently reprint after agent crash; require safe spooler reconciliation or operator approval.
- Keep Windows patched and avoid running agent/renderers as administrator.

### Host storage and remote access protection checklist

- Host document directory excluded from public static file serving and desktop sync folders; account ACLs prevent non-staff browsing.
- Verify available free disk and enforce server-side per-file and aggregate quotas before accepting an upload or collecting payment.
- Configure encryption at rest if available (BitLocker or equivalent), key recovery, antivirus and a separate encrypted, off-host restore-tested backup.
- File path must be randomly generated; never trust original filenames to create local paths; cleanup covers temp render copies and crash residue.
- Secure tunnel/ingress terminates HTTPS; app restricts forwarded headers to trusted proxy addresses, validates hostname/origin, and has CSRF protection.
- Public approval and diagnostics APIs require admin authentication; do not expose local spooler controls to the public.
- Rate-limit uploads, quotes, OTP, payment creation and status polling; guard malformed PDFs and decompression/resource exhaustion.
- Public URLs for files are forbidden; customer gets a status token, not a direct PDF path.
- Review Windows 11 update/patch posture and printer drivers before public deployment; OS build 22621 requires support/patch status confirmation.

## 12. Non-functional requirements / initial targets

These are **targets to validate in pilot**, not guaranteed capabilities:

- Customer mobile pages usable on low-end Android and slow cellular connections.
- P95 API response < 1s excluding uploads or document rendering under normal pilot load.
- Upload cap: provisional 20 MB / file, 100 pages / order; change after machine stress tests.
- Job discovery after **admin approval**: target < 10s while worker and printer available.
- One print job at a time on pilot PC; maximum two concurrent PDF validation tasks (adjust after measurement).
- Availability indicator: agent heartbeat freshness, plus printer health where supported.
- No duplicate automatic reprint in power/network loss scenario.
- Recovery after reboot and disconnected USB.
- Traceable order IDs without exposing customer identity or sequential IDs.

## 13. UI / screens

**Customer:** Landing/QR entry; shop status; upload; preview and validation; printing preferences; quote/checkout; order status; pickup instruction; help/error flow.

**Admin:** Login/MFA; full dashboard; **paid-awaiting-approval inbox**; explicit approve/hold/reject actions; live order board; payments/refunds and reconciliation; printer/station controls; pricing; revenue and volumes analytics; alerts and error resolution; audit history; settings/security.

**Agent utility:** First-run pairing, queue selection and test print, status and last heartbeat, recent jobs, diagnostic export, pause/resume, update/error banner.

**Visual brand (provisional):** PrintAway wordmark; clear primary CTA; modern approachable, mobile-first; avoid heavy animations on customer order page. Colors, fonts, logo and domain TBD.

## 14. Critical test matrix

| Scenario | Required outcome |
|---|---|
| Both Xerox queues installed | Exactly one configured active queue; no duplicate copies |
| One-page PDF | Correct page prints once |
| 10-page PDF, 2 copies | Exactly 20 printed sides without interleaving |
| Duplex enabled | Verified physical side orientation and sheet count |
| Malformed/password-protected PDF | Rejected before dispatch |
| Agent disconnects mid-download | Lease expires, clean recovery, no partial file printed |
| Agent crashes after spooler accepts job | `reconciliation_required`, no blind retry |
| Printer jams/out of paper | Stop queue, surface alert; preserve paid orders |
| PC reboot | Agent returns; outstanding jobs reconciled |
| Payment webhook repeated | Order paid once; **still requires admin approval**, job issued once |
| Different customers track orders | Cannot read documents or status of other customers |
| Admin rejects paid order | No dispatch; audited refund or manual resolution |
| Unauthorized approve request | Rejected; order state unchanged |
| Admin cancels before print | Job never prints; payment handled by policy |
| Host offline during remote upload | User cannot complete upload/checkout; no silent charge |
| Host disk low/full | Upload refused before payment, alert admin, protect print queue |
| Host disk failure | Restore database/files from tested off-host backup, reconcile payments |
| Empty Windows queue after dispatch | Not automatically interpreted as successful physical printing |
| File retention deadline | File deleted; metadata retained only per policy |

## 15. Metrics for pilot

- Order submission conversion rate.
- Percentage of orders printed without manual file handling.
- Median time from approved order to Windows spooler submission.
- Printer failure/exception rate; duplicate print incidents (goal: zero).
- Payment success and refund rate (after Phase 2).
- Average printed sides per order, consumable cost estimate, revenue per order.
- Station uptime, printer-ready uptime, manual interventions per 100 orders.

## 16. Implementation plan for Antigravity

**Do not generate the entire SaaS in one giant step. Use scoped milestones and tests.**

1. **Discovery and diagnostics:** read this PRD; create `docs/DECISIONS.md` and `docs/PRINTER_DIAGNOSTICS.md`; build a read-only Windows printer auditor; no printing yet.
2. **Local print proof:** exact queue selection and safe manual test print of known PDF; record actual outcomes and settings.
3. **Local backend + UI:** PDF upload/validation, quotes, SQLite jobs, local admin controls, non-public demo.
4. **Reliable agent:** Windows queue integration, lease/idempotency, retries requiring reconciliation, status event logs, startup packaging.
5. **Integration tests:** simulate outages and printer errors; check real printer output and counts.
6. **Public host connectivity hardening:** choose domain and secure outbound tunnel/proxy; threat-model upload and payment webhook ingress; admin authentication, rate limits, OTP/guest choice. Hosted Postgres/storage are deferred unless justified by uptime, resilience or scale.
7. **Payments and pilot launch:** selected Indian UPI gateway, verified webhooks/refunds, strict admin approval, customer communications, controlled live orders.
8. **Expansion:** multi-shop/multi-printer, secure release hardware, richer formats and pricing.

### First prompt for Antigravity

```text
You are the lead engineer for PrintAway. Read PRD.md fully and treat §1A and §18 as the product source of truth. The current shop has Windows 11 Pro on an older HP xw4600, 8 GB RAM, one Xerox B1025 physical printer presented as TWO logical Windows USB001 queues. All customer PDFs and core processing must live primarily on the host Windows PC. The target flow is QR/remote upload → PDF validation → quote → UPI verified payment → PAID, WAITING FOR ADMIN APPROVAL → explicit authorized admin approval → exactly one print dispatch → staff pickup. Never automatically print just because payment succeeds. Start ONLY with Milestone 1: read-only Windows printer, spooler, disk, CPU/RAM, USB/driver and host connectivity diagnostics. Do not create a public endpoint or send a print automatically. Create docs/DECISIONS.md, docs/PRINTER_DIAGNOSTICS.md and a minimal reproducible PowerShell diagnostic script. Note that the two Xerox queue names likely refer to one physical USB printer. Require supervised queue validation before any print code is activated. Outline the local-first architecture and safe public HTTPS tunnel approach, but do not deploy it yet. Summarize changed files and validation steps.
```

## 17. Remaining discovery questions — after founder decisions

The launch/shop/access/payment/file/admin/pickup/hosting preferences are **answered** in §1A. Do not ask those eight questions again. Resolve the following unknowns; where marked **BLOCKER**, Antigravity must not deploy the dependent feature until answered.

### 17A. First implementation blockers

1. **Physical printer validation:** Which of the two Xerox Windows queues should be selected? Run supervised print tests; record queue name, output quality and single-copy consistency. **BLOCKER for printing.**
2. **Actual device features:** Is A4 loaded? Is automatic duplex installed and working? Does this specific B1025 support A3 as configured? Are there additional *physical* printers connected or planned, and what models? **BLOCKER for exposing options.**
3. **Paper/toner/operator hours:** Who will replenish supplies, fix jams and approve jobs? What are shop operating hours and the acceptable approval turnaround? **BLOCKER for customer-facing SLA.**
4. **Pricing:** Price per A4 B&W single printed side, duplex price (per printed side or sheet), bulk price tiers, minimum order, taxes/fees and refund policy. **BLOCKER for paid checkout.**
5. **Payment service:** Which Indian UPI-enabled payment gateway, and under whose legally authorized merchant account? Is account onboarding complete? **BLOCKER for live UPI.**
6. **Domain/public entry:** Which domain/subdomain will be used and who controls DNS? Is secure outbound HTTPS tunnel acceptable? **BLOCKER for remote public launch.**
7. **Host availability:** Is `DESKTOP-LJKG4N6` and Xerox powered on during shop hours or 24/7? Is the Windows user always logged in, does the workstation sleep, and is reliable broadband available? **BLOCKER for reliable remote service.**
8. **Storage capacity and privacy:** How much disk free on the host, which drive is used, desired PDF retention (e.g. 24h after collection), and where can we keep encrypted *off-host* backups? **BLOCKER for accepting real customer documents.**
9. **Admin identities:** Who approves? One owner or multiple authorized staff? How should admin sign-in work (password+MFA, passkeys, etc.)? **BLOCKER for exposing approval UI.**
10. **Customer identity/pickup:** Guest checkout with phone OTP or account? What code/identity check will staff use to safely hand over prints? **BLOCKER for public launch.**

### 17B. Decisions we can defer with provisional defaults

11. Upload limit / PDF page cap (provisional 20 MB/file, 100 pages/order, subject to host testing).
12. One PDF vs multiple PDFs per order (provisional one PDF initially; multi-file in next iteration).
13. A4 single-sided + copies at launch; verified duplex and page ranges in follow-up; color unavailable on B1025.
14. Notifications: order status page first, email/SMS/WhatsApp later with cost/consent decision.
15. Shop opening schedule, maximum pending paid orders, admin auto-expiry and how stale approvals are refunded.
16. Customer language: English vs Hindi/Hinglish bilingual.
17. Public shop branding, logo, domain and theme; trademark check still pending.
18. How to handle paid order before approval if selected printer goes offline or stock is unavailable; default admin holds/refunds.
19. Whether admin may preview a sensitive PDF; default **metadata only** and preview only with legitimate support need and audit.
20. Print options to add for future printers: color, A3, sizes, collate, page selection, stapling/finishing.
21. When to add multiple printers in the shop; route by capabilities and configured priorities rather than random selection.
22. Backup frequency and tested disaster-recovery time objectives; provisional nightly encrypted copy.
23. Local app stack preference: lightweight Python FastAPI + static frontend recommended for old host, with a Next.js frontend optional.
24. Packaging: Python environment during initial proof; Windows executable/installer and update mechanism later.
25. Business/legal notices, refund timing, user-upload retention consent, and gateway fees/tax treatment.

## 18. Decision log — recorded vs pending

| Decision | State | Value |
|---|---|---|
| Brand | Selected working brand | PrintAway; Indian trademark clearance outstanding |
| Launch | **Confirmed** | One shop, multiple printers later |
| Access | **Confirmed** | QR code + remote website |
| Approval | **Confirmed** | Admin approval before every print |
| Payment | **Confirmed** | Online UPI, gateway TBD |
| Formats | **Confirmed** | PDF only |
| Admin | **Confirmed** | Full dashboard |
| Pickup | **Confirmed** | Staff-managed |
| Budget | **Confirmed** | Free-first |
| Document storage | **Confirmed** | Windows host primary storage and processing |
| Printer | Observed, pending test | Xerox B1025 on USB001; two logical Windows queues |
| Color | Hardware constraint | B&W only on current Xerox |
| First print size | Provisional | A4 simplex, copies 1–20 |
| Job concurrency | Provisional | One active print per physical printer |
| Host app | Recommended | Lightweight local Python API/workers; HTTPS outbound tunnel for public access |
| PDF deletion | Pending | Set retention deadline and separate encrypted backup policy |
| Customer timestamps | Recommended | Store UTC; display India local time; host currently reports Pacific Time |
| Public launch | Safety gate | Disabled until secure ingress, payment verification, backups, monitoring and approval checks pass |

## 19. Source references (technical validation)

- Xerox B1025 official product sheet: https://www.office.xerox.com/latest/XB1SS-01L.PDF
- Xerox B1025 official brochure: https://www.office.xerox.com/latest/XB1BR-012.PDF
- Xerox B1025 user guide: https://download.support.xerox.com/pub/docs/B1022_B1025/userdocs/any-os/en_GB/Xerox_B1025_mfp_ug_en-us.pdf
- Microsoft Get-PrintJob: https://learn.microsoft.com/powershell/module/printmanagement/get-printjob
- Microsoft Windows spooler support: https://support.microsoft.com/en-us/windows/hardware/printer/fix-print-job-stuck-in-queue-errors-in-windows

---

**Next action:** Founder resolves blocking questions in §17A; developer performs *read-only* diagnostics from Milestone 1, then supervised test print. Do not initiate public payments or production print dispatch until the tests and security gates pass.
