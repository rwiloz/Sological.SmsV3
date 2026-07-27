# Design — first step: v2 core on SMS Central (slices S1–S4)

**Created:** 2026-07-27
**Modified:** 2026-07-27
**Status:** PROPOSED — awaiting Ray's sign-off before S1 code.

## 1. Shape

One stateless service (scale-out safe; workers claim rows) + one PostgreSQL database.
Three planes, all mediated by the store — nothing flows point-to-point:

```
customer send API ──► messages ──► dispatch worker ──► ISmsUpstream (SmsCentralUpstream)
                                                            │ REFERENCE = message id
SMS Central DLR push ──► /ingress/smscentral/delivery ──► normalized DeliveryEvent ─► message state
SMS Central MO push ───► /ingress/smscentral/inbound ───► inbound_messages (reassembled, correlated)
message state / inbound ──► webhook_outbox ──► egress worker ──► customer webhook (AI-Workforce)
every billable event ──► billing_ledger (append-only)
```

Principles carried from the AI-Workforce comms build: one writer per table set; honest
statuses (no optimistic `delivered`); receivers ack fast and enqueue, never do work inline;
idempotency at every boundary (upstream retries its pushes — duplication is the contract).

## 2. Tenancy model

`customer` = a billing party (AI-Workforce, connectnow, …). `channel` = a customer's sending
identity + credentials: legacy `ExternalID`-equivalent key, API keys (two live for rotation,
stored hashed), originator config, webhook config, upstream selection. **Auth is API keys
ONLY — no IP allowlisting in v2 (ruled 2026-07-27)**; the legacy `SMSValidIP` mechanism dies
with the Delphi box (consequence recorded at S5).
Every row in every table hangs off `channel_id` (and denormalized `customer_id` where queries
need it). Segregation is row-level + per-channel keys; there is no cross-channel read path on
any customer surface.

## 3. Schema (PostgreSQL, EF Core)

- **customers** — id, code (short, unique), name, status, created_at.
- **channels** — id, customer_id, key (unique; the auth handle), description,
  originator (≤11 alnum | `shared` | dedicated number), api_key_1_hash, api_key_2_hash,
  webhook_url, webhook_secret_name, upstream (`smscentral` now), status, created_at.
- **messages** — id uuid (≡ upstream REFERENCE), channel_id, customer_ref (caller's own
  reference, optional, ≤64), to_number, originator_used, body, parts smallint,
  status (`queued → submitting → sent → delivered | failed | rejected | expired`, plus
  terminal `duplicate` — §6.1a guard verdict, never dispatched, never billed),
  error_code, error_detail, upstream, upstream_id (SMS Central `ID`), requested_at,
  submitted_at, delivered_at, failed_at, claim columns for the dispatch worker
  (claimed_by, claimed_at; claims LEASE-expire so a dead replica's work is reclaimed — §9).
  Index (channel_id, customer_ref), (status) partial on non-terminal.
- **delivery_events** — id, message_id, raw_result, raw_status, raw_description, provider,
  received_at, payload jsonb. Append-only audit; the message row is the distilled truth.
- **inbound_messages** — id, channel_id, from_number, to_number, body, upstream_id,
  reply_to_message_id (nullable FK), received_at, complete bool (multipart), payload jsonb,
  delivered_to_customer_at (set by egress; NULL = owed).
- **inbound_parts** — reassembly buffer: (channel_id, from_number, group_ref) key, part_no,
  total_parts, body_fragment, dcs, received_at. Swept into `inbound_messages` on completion
  or 60s timeout (deliver what arrived, ordered, `complete=false`).
- **billing_ledger** — id, customer_id, channel_id, ref_type (`message` | `inbound`), ref_id,
  direction, units (parts), occurred_at. Append-only; no updates, ever. Corrections are
  compensating rows.
- **webhook_outbox** — id, channel_id, event_type, payload jsonb, attempts, next_attempt_at,
  state (`pending | delivered | dead`), created_at. Claim-based worker; backoff 1m→5m→30m→2h
  ×3 then `dead` (dead rows are visible, not silent).

## 4. Upstream seam

```csharp
public interface ISmsUpstream
{
    // Returns the upstream's accept/reject verdict for ONE message.
    Task<UpstreamSubmitResult> SubmitAsync(OutboundSms sms, CancellationToken ct);
}
public sealed record OutboundSms(Guid MessageId, string Originator, string To, string Body);
public sealed record UpstreamSubmitResult(bool Accepted, string? UpstreamId, string? ErrorCode, string? ErrorDetail);
```

Ingress is NOT on the interface — receivers are provider-specific endpoints that translate
into two normalized internal events, and everything downstream of those is driver-agnostic:

- `DeliveryEvent(MessageRef, Verdict: Sent|Delivered|Failed|Rejected|Expired, RawCode, RawText, At)`
- `InboundSms(ChannelHint, From, To, Body, UpstreamId, ReplyToMessageRef?, At, Complete)`

The Sinch driver (S6) adds `SinchUpstream` + its webhook receiver emitting the same two
events. That is the whole cutover surface.

### 4.1 SmsCentralUpstream

- `POST https://my.smscentral.com.au/wrapper/sms` form-encoded: `USERNAME`, `PASSWORD`
  (sub-account creds from secret store), `ACTION=send`, `ORIGINATOR` (channel config),
  `RECIPIENT` (E.164 without `+`, international), `REFERENCE` = message uuid ("N" format —
  32 chars, unique by construction, satisfies the 513-duplicate rule), `MESSAGE_TEXT`
  (url-encoded).
- Response body `0` → accepted (`sent` pending receipts). `{code} {text}` → map:
  511/514/519/531/534/535 → `rejected` (no retry); 500/536 → retryable (bounded, then
  `failed`); 513 → treat as accepted-duplicate (our uuid collided ⇒ prior submit succeeded —
  reconcile, don't resend).
- Batch (`RECIPIENTMESSAGES`) deliberately NOT used in S2: all-or-nothing rejection semantics
  fight per-message honesty; revisit only if volume demands it.

## 5. SMS Central ingress receivers

Both are GET, both must answer `200` body `0` fast (their retry-on-silence is a duplication
engine). Both validate `USERNAME`/`PASSWORD` against the sub-account creds (their model —
creds in query string; receivers live on HTTPS only, and the values are the sub-account's,
never a customer's). Idempotency before ack: dedupe on (`ID`, `REFERENCE`, part) natural keys.

### 5.1 `GET /ingress/smscentral/delivery`

Params seen in the wild (Jan-2013 ref + Delphi gateway live captures): `ORIGINATOR`,
`RECIPIENT`, `REFERENCE`, `ID`, `RESULT`, `STATUS`, `STATUSDESCRIPTION`, `UDH`, `PROVIDER`,
`MESSAGE_TEXT`. Mapping (RESULT first, STATUS refines — same precedence the Delphi gateway
proved out):

| Signal | Verdict |
|---|---|
| `RESULT=1` or `STATUS=DELIVRD` | delivered |
| `RESULT=0` or `536` or `STATUS=BUFFRED` | sent (interim) |
| `RESULT` 503..535, 550 or `STATUS=FAILED` | failed (carry code+description) |
| unparseable | log + store raw `delivery_events` row, no state change |

Multipart: receipts arrive per part (`UDH` last two octets = total/part-no). A message is
`delivered` when all parts confirm; any part failing ⇒ `failed` (partial delivery recorded in
`delivery_events`). `REFERENCE` = our uuid → message row; unknown reference → audit row only
(some other account traffic must never 500).

### 5.2 `GET /ingress/smscentral/inbound`

Params: `ORIGINATOR` (sender), `RECIPIENT` (our number), `REFERENCE` (original send's ref when
it's a reply), `MESSAGE_TEXT` **or** `UDH`+`BINARY` (+`DCS`; 8 = UCS-2) for multipart, `ID`.

Routing precedence: `REFERENCE` → message → channel (authoritative for replies) ·
`RECIPIENT` = a channel's dedicated number · else quarantine row on the operator channel
(never dropped, never 500). Reassembly per §3 `inbound_parts`. STOP/opt-out is NOT handled
here — opt-out policy is the customer's plane (AI-Workforce comms already owns it); v2 is
honest transport. (Upstream blacklist additions arrive as 519 rejects and surface as such.)

## 6. Customer egress (webhooks) + send API

### 6.1 Send

`POST /api/v1/messages` — header `X-Api-Key` (the ONLY customer auth mechanism). Body:
`{ to, body, reference?, originator? }` → `202 { messageId, parts, status: "queued" }`.
`GET /api/v1/messages/{id}` → full status. `reference` is the CALLER's correlation handle,
echoed on every webhook; uniqueness per channel enforced (409 on reuse) **on this NEW surface
only** — the S5 legacy emulation must keep accepting duplicate refs, because legacy callers
reuse them constantly (59k dups/12m on the biggest channel —
[legacy-db-findings](legacy-db-findings.md)).

### 6.1a Pre-dispatch guards (product features carried from the legacy processor)

Run in the dispatch worker BEFORE any upstream submit; both verdicts are terminal, honest,
and **excluded from the billing ledger**:

- **Duplicate detection** (Ray: a product feature, not an accident): a queued message whose
  (reference, recipient, body) matches an earlier message inside the window → status
  `duplicate`. Legacy rule preserved exactly for the S5 surface (ExtRef + MobileNo + Message,
  1-hour window, submission still ACCEPTED at the API); on the new API the window is
  per-channel config (default 1h). Caught ~0.4% of live traffic in 12m — the double-submit
  safety net. Source: `uDMExetelSMS.pas` "Mark Duplicates" sweep.
- **Local recipient validation**: AU-mobile shape (`04` + 10 digits) or `+`-international,
  `0400000000` sentinel rejected → status `rejected`, error `invalid_recipient` — saves the
  upstream round-trip (legacy `I` before the upstream's 525 ever fires).

### 6.2 Webhooks (signed: `X-Sms-Signature: hmac-sha256=<hex>` over the raw body, per-channel secret)

`sms.delivery`:
```json
{ "event": "sms.delivery", "messageId": "…", "reference": "…", "to": "+61…",
  "status": "delivered | sent | failed | rejected | expired",
  "errorCode": "503", "errorDetail": "…", "timestamp": "2026-07-27T…Z" }
```
`sms.inbound`:
```json
{ "event": "sms.inbound", "inboundId": "…", "from": "+61…", "to": "+61…", "body": "…",
  "complete": true, "receivedAt": "…",
  "replyTo": { "messageId": "…", "reference": "…" } }   // null when uncorrelated
```
Delivery contract: at-least-once, ordered per message best-effort, receiver acks 2xx;
non-2xx/timeouts retry per outbox backoff then `dead` (visible in ops queries + S7 report).

## 7. AI-Workforce wiring (S4's second half — lives in the AI-Workforce repo)

- `SologicalSmsProvider` → v2: `POST /api/v1/messages` with `reference` = the refNo it already
  generates; store returned `messageId` as `SmsSendResult.MessageId` → `contact.ProviderRef`
  carries a REAL upstream handle at last. Delete the `smsdr` DR-host misread and the
  `UseFallBackEndPoint` flag (v2 owns upstream failover).
- `SmsWebhookController`: keep route + `X-Sms-Api-Key` (or upgrade to the HMAC header — Ray's
  call), align fields: `sms.delivery.messageId` → its `MessageId`, statuses map 1:1 onto its
  existing `NormalizeDeliveryState`; `sms.inbound` → `InboundSmsEvent` unchanged.
- `InboundSmsHandler`: when `replyTo.reference` is present, correlate the case reply EXACTLY
  (it equals the send's refNo → resolvable to the contact row) instead of the 14-day-window
  heuristic; the window stays as fallback for uncorrelated inbound. Comms-side recipient
  safety (whitelist/redirect, STOP, consent) is untouched — v2 is transport.

## 8. Config & secrets

- Secrets: `SmsV2:SmsCentral:Username|Password`, per-channel `SmsV2:Webhook:{channelKey}`,
  DB connection. Azure: real Key Vault. **Local: the shared Azure Key Vault emulator**
  (`https://localhost:4997`, container `aiworkforce-keyvault-emulator` — the established
  local secret home across Ray's services). Config pipeline loads KV whenever
  `AzureKeyVault:VaultUri` is set, emulator-aware (non-`vault.azure.net` host ⇒ emulator
  token credential, non-fatal load); vault loads after env vars; hermetic test harnesses
  blank the URI. Pattern source: Billing `Program.cs` / AI-Workforce `cbb2994d3`.
- Channel/customer rows are data (seeded by SQL/import script until S7 admin) — no
  channel config in appsettings, ever (the registry-row lesson from AI-Workforce comms).
- Ingress hostname: needs a public HTTPS name before S3 (e.g. `smsv2.sological.com.au` on the
  container app). ⚠ Decision for Ray.

## 9. HA & DR posture (ruled 2026-07-27: platform HA replaces the sms/smsdr pair)

The old model ran TWO stateful boxes (`sms` + `smsdr`: IIS + SQL Server + processor each)
with bespoke replication between them (`ReplicateSmsData`/`ModelReplicateClient`,
`isFromDr`) because each box WAS the service. v2 separates compute from state, so that
whole apparatus retires — **HA becomes a platform concern**, provided the design keeps the
app-level invariants that make stateless scale-out true:

| Layer | Mechanism | Owner |
|---|---|---|
| Service | ≥2 Container Apps replicas, health probes, rolling deploys; safe because workers are claim-based and receivers are idempotent | infra (replica count) + design (claims/idempotency — §1, §3, §5) |
| Dispatch workers | claim columns carry a **lease expiry**; a dead replica's claims are reclaimed after timeout — no stuck messages | design (§3 messages claim columns) |
| Upstream down | messages queue and retry with backoff — degrade to delayed, never to lost | design (§4.1 retry mapping) |
| Ingress down briefly | SMS Central retries un-acked pushes (their duplication contract is our dedup contract, §5); short outages self-heal | design |
| Database | Azure PostgreSQL Flexible Server zone-redundant HA option + PITR backups; no hand-rolled replication, ever | infra/runbook |
| DNS | one stable ingress hostname; at S5 BOTH legacy names (`sms.` and `smsdr.sological.com.au`) point at the same v2 service | infra |

Decision left with Ray at S1: which PSQL HA tier to pay for now (dev can ride
single-zone + PITR; flip to zone-redundant before real customer migration at S5).

## 10. Open questions (Ray)

1. **Sub-account callbacks** — confirm at creation that the sub-account gets its own DLR +
   inbound forward URLs (roadmap risk; fallback design: shared receiver + discriminate by
   REFERENCE/RECIPIENT, which §5 already supports).
2. **AI-Workforce originator**: dedicated number (enables reply-to-number routing, needed for
   inbound not tied to a recent send) vs `shared` pool (replies correlate by REFERENCE only)?
   Recommend a dedicated number for the comms capability.
3. **Ingress + API hostnames** and which container-apps environment (beside billing?).
4. Webhook auth for AI-Workforce: keep `X-Sms-Api-Key` shared-secret (zero AI-Workforce code
   change) or adopt the HMAC signature from day one (recommended — it's already in this design)?
