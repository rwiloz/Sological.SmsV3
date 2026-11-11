# Design — first step: v3 core on SMS Central (slices S1–S4)

**Created:** 2026-07-27
**Modified:** 2026-11-11
**Status:** SIGNED OFF (Ray, 2026-07-27) — S1 build started. §10 open questions remain parked with Ray.

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
ONLY — no IP allowlisting in v3 (ruled 2026-07-27)**; the legacy `SMSValidIP` mechanism dies
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
public sealed record UpstreamSubmitResult(bool Accepted, string? UpstreamId, string? ErrorCode, string? ErrorDetail, bool Retryable = false);
```

(`Retryable` was added at the S2 build: §4.1's bounded-retry codes must be distinguishable
from hard rejects without the worker learning provider-specific codes.)

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

### 4.2 MobileMessageUpstream (2026-11-11 — the default driver)

Mobile Message (MM) replaces SMS Central as the upstream for every channel; Sinch (S6) is dropped.
`SologicalSms:Upstream:Provider` picks the driver at startup (`MobileMessage` by default,
`SmsCentral` still selectable while that account lives). Each message records the provider it was
actually submitted to (`messages.upstream`, set at submit).

- One account only: MM has no sub-accounts and the account is tied to Ray's ACMA originating-telco
  registration, so the legacy app, Azure dev and local dev all send through it. The account's
  single status URL and single inbound URL point at the legacy app (always on), whose router
  forwards each environment's events (`Docs/MobileMessage-Router-Brief.md` in `C:\Code\Sological.Sms`).
- `POST https://api.mobilemessage.com.au/v1/messages`, Basic auth (`ApiUsername`/`ApiPassword`;
  a key pair per environment), one message per request. `to` = E.164 without the plus,
  `sender` = the channel's originator, `unicode: true` (the send lane has already folded every
  character with a GSM-7 equivalent, §6.1; MM would strip the rest), `custom_ref` =
  `{EnvironmentTag}:{message id N}` (`dev` on Azure, `local` on Ray's machine — the router's tag),
  `Idempotency-Key` = message id (MM replays the original result for 24 h, so a resubmit after a
  lost response or a lease reclaim never sends twice).
- Result per message: `success` → `sent` (MM's `message_id` stored as `upstream_id`); `blocked`
  (the recipient opted out, MM's account-wide list) → `rejected` / `unsubscribed`; `error` →
  `rejected` / `upstream_error` with MM's text. HTTP 401/403/409/429/5xx and an unreadable 2xx
  → bounded retry; 400/422 → `rejected`. No credentials or no valid tag → held (`not_configured`),
  never sent with a ref the router cannot route.

## 5. SMS Central ingress receivers

Both are GET, both must answer `200` body `0` fast (their retry-on-silence is a duplication
engine). Both validate `USERNAME`/`PASSWORD` against the sub-account creds (their model —
creds in query string; receivers live on HTTPS only, and the values are the sub-account's,
never a customer's). Idempotency before ack: dedupe on (`ID`, `REFERENCE`, part) natural keys.
**S3 build reality check (2026-07-27):** the legacy gateway's live captures show their
pushes carry NO creds at all (and the param is spelled `USER_NAME` in the legacy reader).
**Ruled (Ray, 2026-07-27): creds do NOT ride URLs** — verification is the `SLVERIFY`
header carrying a shared key (portal-configured webhook header; constant-time compared;
secret `SologicalSms--Ingress--VerifyKey`). Legacy creds params still validate when
present but are redacted from stored payloads; absence is tolerated until
`SologicalSms:Ingress:RequireVerification` flips strict. Receivers accept GET and POST
(form-encoded — their current portal offers POST). State changes stay gated by
REFERENCE = our unguessable uuid either way.

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
here — opt-out policy is the customer's plane (AI-Workforce comms already owns it); v3 is
honest transport. (Upstream blacklist additions arrive as 519 rejects and surface as such.)

### 5.3 Mobile Message receivers — `POST /ingress/mobilemessage/status|inbound` (2026-11-11)

The legacy router forwards our environment's events with MM's JSON body and `X-MM-Timestamp` /
`X-MM-Signature` untouched, so MM's own signature verifies here: lowercase hex HMAC-SHA256 of
`{timestamp}.{raw body}` with the account's signing secret (`SologicalSms:MobileMessage:SigningSecret`),
timestamp within 5 minutes. A request that does not verify → 401; no secret configured → 503 (a
non-2xx the router turns into MM's retry, so events wait out the fix instead of being lost).
Any 2xx acks.

- **Status** (one receipt per part: `custom_ref`, `message_id`, `status`, `part_number`,
  `total_parts`): audit row in `delivery_events` (`provider = mobilemessage`), deduped on
  (`message_id`, `part_number`, `status`) before the ack. Correlation: `custom_ref` with our tag →
  message; else `message_id` = `upstream_id`. `delivered` → delivered once every part confirmed;
  `failed` → failed (`part n of t failed`); `sent` interim. Same transitions as §5.1, first
  terminal verdict wins. A receipt under another environment's tag or for an unknown message is
  audit-only, 200, and does **not** feed the unmatched-DR breaker: the router already filtered by
  tag, so an unmatched receipt is a configuration fault, not foreign traffic on our account.
- **Inbound** (`to` = our number, `sender` = the handset, `message`, `type`, `original_custom_ref`,
  `original_message_id`): MM reassembles multipart, so one webhook is one message. No id of its own
  — dedupe on `mm:` + SHA-256 of (sender, to, received_at, type, message). Routing as §5.2:
  original send (our-tag ref, then `original_message_id`) → its channel; else the dedicated number
  replied to → its channel; else the operator quarantine channel. `type = unsubscribe` is stored
  and forwarded like any reply (opt-out policy stays the customer's plane); MM blocks further sends
  to that number, which come back `unsubscribed`.

## 6. Customer egress (webhooks) + send API

### 6.1 Send

`POST /api/v1/messages` — header `X-Api-Key` (the ONLY customer auth mechanism). Body:
`{ to, body, reference?, originator? }` → `202 { messageId, parts, status: "queued" }`.
**One channel per sender ID (Ray, 2026-07-27):** `originator` in the body is accepted only
as a confirmation of the channel's configured sender — a different value is
`400 originator_mismatch`. Multiple sender IDs = multiple channels (own API keys each).
`GET /api/v1/messages/{id}` → full status. `reference` is the CALLER's correlation handle,
echoed on every webhook; uniqueness per channel enforced (409 on reuse) **on this NEW surface
only** — the S5 legacy emulation must keep accepting duplicate refs, because legacy callers
reuse them constantly (59k dups/12m on the biggest channel —
[legacy-db-findings](legacy-db-findings.md)).

**The body is folded before anything else sees it** (`GsmFold`, ruled 2026-09-19): every character
with a GSM 03.38 equivalent — curly quotes and apostrophes, dashes, the ellipsis, Unicode spaces and
the tab, zero-width marks, fullwidth ASCII, the Latin ligatures, the backtick (the one plain-ASCII
character outside the set) — becomes that equivalent; a character with none (an emoji, another
script, an accented letter outside the set, the joiners) stays, so a body goes UCS-2 only when one
remains, and the direction marks a handset wraps a pasted number in go only when the rest is GSM-7.
The folded text is the one text that is counted (`parts` reports it), stored, sent, billed,
duplicate-guarded and matched against the delivery receipt's echo; a body that is nothing once
folded is `400 invalid_request`. The customer API never returns the body; the admin plane shows the
folded one — what the handset received.

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
- **Originator whitelist (ruled 2026-07-27 — ACMA is now enforcing sender-ID compliance)**:
  the resolved originator (channel default, or the per-message `originator` override from
  §6.1) must match an explicit Ray-controlled whitelist — data (DB rows seeded like
  channels), never appsettings. No match → terminal `rejected`, error `invalid_originator`,
  unbilled, never submitted. Alphanumeric sender IDs only enter the whitelist once
  registered on the ACMA SMS Sender ID Register; the whitelist table + guard land with S2's
  migration. (Intersects §10 Q2: whatever originator AI-Workforce gets must be whitelisted.)

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

- `SologicalSmsProvider` → v3: `POST /api/v1/messages` with `reference` = the refNo it already
  generates; store returned `messageId` as `SmsSendResult.MessageId` → `contact.ProviderRef`
  carries a REAL upstream handle at last. Delete the `smsdr` DR-host misread and the
  `UseFallBackEndPoint` flag (v3 owns upstream failover).
- `SmsWebhookController`: keep route + `X-Sms-Api-Key` (or upgrade to the HMAC header — Ray's
  call), align fields: `sms.delivery.messageId` → its `MessageId`, statuses map 1:1 onto its
  existing `NormalizeDeliveryState`; `sms.inbound` → `InboundSmsEvent` unchanged.
- `InboundSmsHandler`: when `replyTo.reference` is present, correlate the case reply EXACTLY
  (it equals the send's refNo → resolvable to the contact row) instead of the 14-day-window
  heuristic; the window stays as fallback for uncorrelated inbound. Comms-side recipient
  safety (whitelist/redirect, STOP, consent) is untouched — v3 is transport.

## 8. Config & secrets

- Secrets: `SologicalSms:MobileMessage:ApiUsername|ApiPassword|SigningSecret` (2026-11-11; a key
  pair per environment, the signing secret account-wide), `SologicalSms:SmsCentral:User|Password`, per-channel
  `SologicalSms:Webhook:{channelKey}`, DB connection (namespace renamed from `SmsV3:` to
  `SologicalSms:` when the first live secret landed — Ray, 2026-07-27). Azure: real Key Vault. **Local: the shared Azure Key Vault emulator**
  (`https://localhost:4997`, container `aiworkforce-keyvault-emulator` — the established
  local secret home across Ray's services). Config pipeline loads KV whenever
  `AzureKeyVault:VaultUri` is set, emulator-aware (non-`vault.azure.net` host ⇒ emulator
  token credential, non-fatal load); vault loads after env vars; hermetic test harnesses
  blank the URI. Pattern source: Billing `Program.cs` / AI-Workforce `cbb2994d3`.
- Channel/customer rows are data (seeded by SQL/import script until S7 admin) — no
  channel config in appsettings, ever (the registry-row lesson from AI-Workforce comms).
- Ingress hostname: needs a public HTTPS name before S3 (e.g. `smsv2.sological.com.au` on the
  container app). Dev (2026-07-27): `sms-yoga.sological.io` — Cloudflare tunnel → the local
  docker container on 127.0.0.1:5230 (`ops/run-local-docker.ps1`). ⚠ Prod name still Ray's.
- Mobile Message environment tag (not a secret): `SologicalSms:MobileMessage:EnvironmentTag` —
  `dev` on Azure (bicep env), `local` on the docker container (`ops/run-local-docker.ps1`). The
  legacy router maps `dev` → `https://sms.dev.ai-workforce.au/ingress/mobilemessage/*` and
  `local` → `https://sms-yoga.sological.io/ingress/mobilemessage/*`.

## 9. HA & DR posture (ruled 2026-07-27: platform HA replaces the sms/smsdr pair)

The old model ran TWO stateful boxes (`sms` + `smsdr`: IIS + SQL Server + processor each)
with bespoke replication between them (`ReplicateSmsData`/`ModelReplicateClient`,
`isFromDr`) because each box WAS the service. v3 separates compute from state, so that
whole apparatus retires — **HA becomes a platform concern**, provided the design keeps the
app-level invariants that make stateless scale-out true:

| Layer | Mechanism | Owner |
|---|---|---|
| Service | ≥2 Container Apps replicas, health probes, rolling deploys; safe because workers are claim-based and receivers are idempotent | infra (replica count) + design (claims/idempotency — §1, §3, §5) |
| Dispatch workers | claim columns carry a **lease expiry**; a dead replica's claims are reclaimed after timeout — no stuck messages | design (§3 messages claim columns) |
| Upstream down | messages queue and retry with backoff — degrade to delayed, never to lost | design (§4.1 retry mapping) |
| Ingress down briefly | SMS Central retries un-acked pushes (their duplication contract is our dedup contract, §5); short outages self-heal | design |
| Database | Azure PostgreSQL Flexible Server zone-redundant HA option + PITR backups; no hand-rolled replication, ever | infra/runbook |
| DNS | one stable ingress hostname; at S5 BOTH legacy names (`sms.` and `smsdr.sological.com.au`) point at the same v3 service | infra |

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
