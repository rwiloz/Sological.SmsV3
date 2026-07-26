# Feasibility — SMS inbound + delivery receipts, and where the rewrite lands

**Created:** 2026-07-27
**Status:** DECIDED — build Sological SMS v2 (this repo), SMS Central upstream first.

The trigger: AI-Workforce's comms capability needs SMS **inbound** and **delivery receipts**
wired up. Its send path already works through the old Sological gateway; nothing pushes inbound
or DLRs to it. Evaluating how to close that gap surfaced the bigger decision: the Sological SMS
service must be rewritten by early next year anyway (SMS Central discontinuation → Sinch
migration), so the wiring question and the rewrite are the same project.

## The four options evaluated

### A — Poll the old Delphi gateway (`submitsms.dll`) — REJECTED as the end state

The gateway's client surface (reverse-engineered from `C:\Code\DelphiSms\uSubmitSMS.pas`):

| Action | Shape | Notes |
|---|---|---|
| `sendsms?Ref=&Channel=&MobileNo=&Message=&ApiKey=` | GET, returns `OK {SMSID}` or `Failed: …` | Channel + ApiKey1/2 + IP allowlist (`SMSValidIP`, `'Any'`). Ref → `SMSMessage.ExtRef` (50 chars). Max 612 chars. All new sends get `Provider='SMSCentral'`. |
| `checkstatus?SMSID=&Channel=&ApiKey=` | returns `OK {Status} {SMSID}` | Status letters: N new · S sent · D delivered · F failed · E error · I invalid. |
| `getsms?Channel=&ApiKey=` | returns `OK\t{SMSID}\t{FromMobile}\t{ReceivedDT}\t{Message}` or `FAILED\tNo messages` | **Consume-once** (sets `RetrievedDT`) — exactly one consumer per channel. One message per call. No To-number (the channel is the identity). |

Why rejected: polling both lanes; per-message `checkstatus` calls; consume-once means dev and
prod can steal each other's inbound; multipart inbound (>160 chars) is **lost** at the gateway
(SMS Central pushes it as UDH+BINARY, the gateway inserts an empty `Message`); and it keeps a
15-year-old single-box ISAPI DLL in the critical path of a production comms capability.

### B — Poll the SLSmsApiV2 replication API — RULED OUT

`C:\Code\Sological.Sms\SLSmsApiV2` is the DR-replication surface: checksum lists + row fetch
over `SMSMessage` / `SMSDeliveryStatus` / `SMSIn` / `SMSChannel` / `SMSValidIP` with ONE master
`ApiKey`. It exposes every channel's upstream credentials and client API keys. It is
management-plane, not client-grade. Never hand it to an application.

### C — Direct to SMS Central — ACCEPTED as the first upstream driver

SMS Central (`https://my.smscentral.com.au/wrapper/sms`, API Reference Jan-2013) — facts:

- **Send**: POST `ACTION=send`, `USERNAME`/`PASSWORD`, `ORIGINATOR` (≤11 alnum or `shared`),
  `RECIPIENT` (international), `REFERENCE` (unique per message — duplicate ⇒ 513 reject),
  `MESSAGE_TEXT` (≤7 parts / 1071 chars), optional `SCHEDULE`, `RECIPIENTMESSAGES` (JSON batch
  ≤100, all-or-nothing on invalid numbers). Response body: `0` accepted, else `{code} {text}`.
- **DLR push**: real-time HTTP **GET** to a configured URL: `REFERENCE` (round-trips), `ID`,
  `RESULT` (0 pending · 1 delivered · 5xx failures), `STATUS` (`DELIVRD`/`BUFFRED`/`FAILED`),
  `STATUSDESCRIPTION`, `UDH` (per-part receipts on multipart). Must answer `200` + body `0` or
  it retries (duplication risk).
- **Inbound push**: real-time GET: `ORIGINATOR`, `RECIPIENT` (our number), `MESSAGE_TEXT`,
  and — when the message is a reply to something we sent — the original send's `REFERENCE`
  (exact reply→send correlation). Multipart arrives as `UDH`+`BINARY` parts (DCS 8 = UCS-2);
  **we** reassemble. Same `200`+`0` ack contract.
- **On-demand read** (`ACTION=read`, `STATUS=UNREAD`) exists but marks-as-read account-wide —
  same single-consumer trap as option A. Backup lane only.
- Response codes: 0 sent-pending · 1 delivered · 500 internal · 503 expired · 511 bad creds ·
  513 duplicate REFERENCE · 514 no recipient · 519 blacklisted · 531 no content · 534 credit ·
  535 bad originator · 536 temporarily delayed. (Delphi gateway also maps 550 → failed.)
- **Sub-accounts are available** (confirmed by Ray) — the isolation mechanism: this service
  gets its own sub-account with its own DLR/inbound forward URLs, leaving the old gateway's
  account untouched. ⚠ Verify at setup: sub-accounts do get independent callback config.

### D — Sinch ("Sync", Engage, ex-MessageMedia) — ACCEPTED as the cutover driver

From `C:\Code\New.Sms\openapi.json` (Sinch Engage 2.1.0) + web verification 2026-07-27:

- **Send**: `POST /v1/messages` (batch ≤100). Each message: `destination_number`, `content`
  (≤5000 — the platform handles parts), `source_number`/`source_number_type`,
  `delivery_report`, `metadata` (key/values — **round-trips on DLRs and replies**), optional
  per-message `callback_url`, `scheduled`, `format` = `SMS | TTS | MMS` (no RCS on this surface).
- **DLR**: three lanes — per-message `callback_url` push, account webhooks
  (`POST /v1/webhooks/messages`, events like `ENROUTE_DR`/`DELIVERED_DR`, templated payload,
  HMAC signature keys via `/v1/iam/signature_keys`), or **poll+confirm**
  (`GET /v1/delivery_reports` → oldest 100 unconfirmed → `POST /v1/delivery_reports/confirmed`).
  Status enum: `enroute submitted delivered expired rejected undeliverable queued processed
  cancelled scheduled failed`.
- **Inbound**: same three lanes (`GET /v1/replies` + confirm). A reply carries the original
  message's `message_id` + `metadata` → exact correlation; `content` is reassembled by the
  platform (no UDH handling).
- **AU instance exists**: `https://au.app.api.sinch.com` — data residency for AU customers.
- Conceptually SMS Central matured: `REFERENCE`→`metadata`, on-demand read→poll+confirm,
  forward URL→webhooks. One driver abstraction covers both thinly.
- "V2": there is **no v2 send API** — `/v2-preview/*` is reporting only (RCS-aware: channel
  filters incl. `RCS`/`WHATSAPP`, `RichMessageType`, RCS billing categories). The
  next-generation surface is the **Conversation API** (SMS/RCS/MMS/WhatsApp, channel-priority
  fallback) — hosted **US-East / EU-Ireland / Brazil only, no AU region** as of 2026-07.
  Residency makes Conversation API an RCS-lane decision, not the SMS lane. See
  [roadmap](roadmap.md).
- Sinch states existing API integrations are unaffected by the July-2026 Engage rebrand; the
  v1 messages API is the current supported surface of the platform the Sync account lands on.
- Test lane: Ray holds a **test Sync account** (unlinked; needs credit) — use it to verify
  which APIs its credentials expose (Engage v1 keys vs Sinch project/Conversation API) and to
  run send/DLR/reply round-trips before any cutover.

## Constraints that shaped the decision

1. SMS Central discontinues; the production Sync account **cannot run in parallel** with it →
   cutover is one flip, so the upstream seam must be the only place the two differ.
2. The rewrite must eventually **emulate the legacy `submitsms.dll` URLs** so existing
   customers keep working when `sms.sological.com.au` repoints; customers upgrade to the new
   API at their own pace.
3. **Customer segregation and billing records** are mandatory (multiple clients ride this
   service; billing today comes from the gateway DB's parts counting + daily reports).
4. No production infra yet: use the existing Azure PSQL server (own database) and a container
   app beside the billing service.

## Decision

Build **Sological SMS v2** (this repo): standalone service, customer/channel registry,
message store, append-only billing ledger, webhook egress to customers, upstream driver seam.
**SMS Central sub-account first** (AI-Workforce live now), **Sinch Engage v1 (AU instance)**
as the cutover driver, **Conversation API reserved for RCS** pending residency. Options A/B
die with the Delphi box; option C's shapes live on inside the SMS Central driver.

### Incidental findings for the AI-Workforce side (recorded here so they aren't lost)

- `SologicalSmsProvider.SendWithTrackingAsync` hardcodes `smsdr.sological.com.au` — the code
  comment reads "dr" as *delivery receipt* but it is the **disaster-recovery** host: every
  tracked comms send routes via DR today. Fix when re-pointing at v2.
- The send response's `OK {SMSID}` is discarded; AI-Workforce correlates on its own generated
  ref. Under v2 the correlation contract is explicit (see design §7).
- AI-Workforce's `SmsWebhookController` (POST JSON + `X-Sms-Api-Key`) was built for a push
  model the old gateway never had — it is ~the right shape for v2's webhook egress.
