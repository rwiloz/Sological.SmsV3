# S7 spec — SMS management API (v3) + SMS Service page (AI-Workforce admin)

**Created:** 2026-08-01
**Status:** SPEC — approved shape per Ray's rulings 2026-07-31/2026-08-01 (management API in v3,
UI in AIW admin, **dedicated page**). Phase 1 is v3-side work; Phase 2 is a work order for an
agent in the AI-Workforce repo.

## 0. Principles (settled)

- **v3 stays the authority.** All SMS entity state lives in the `sologicalsms` database and is
  changed ONLY through v3's management API. The AIW admin UI is a client, never a second writer.
- **The browser never holds v3 credentials.** AIW System proxies server-side; the v3 admin key
  lives in Key Vault and is read by AIW System only.
- **Three tiers of configuration, three homes** (§4 answers "should appsettings move to psql"):
  1. **Entity/operational state** (pause, quotas, allowlists, webhook URLs, whitelist, breaker)
     → v3 DATABASE, managed via the management API. This is v3's equivalent of AIW's
     `system.config_entries` — it already exists as first-class columns; no key-value table needed.
  2. **Secrets + deploy-scoped config** (creds, keys, conn string, BaseUrl) → KEY VAULT
     (`SologicalSms--*` in the shared vault), surfaced through AIW's existing **Keys & Secrets**
     tab by adding entries to `ConfigManifest` (that tab is manifest-driven — never edit its UI).
     Applies on v3 restart (see the restart endpoint, §2.9).
  3. **Service tuning** (rate limits, breaker thresholds/alert number, retry backoffs) →
     **KV VALUES** (tier 2), NOT appsettings and NOT bicep env (AMENDED per Ray, 2026-08-01:
     "a deployment will override them and I'll want different settings in different
     environments" — bicep/appsettings values are clobbered/frozen by deploys; KV values
     survive every deploy and differ per environment by construction, one vault each).
     appsettings keeps SAFE DEFAULTS only; the vault overrides where present (v3's config
     binder already loads KV over appsettings — zero new code); applies on v3 restart (§2.9
     button). `SologicalSms__Breaker__AlertNumber` MOVES out of bicep env into KV
     accordingly. Still NOT a psql config table — its only remaining advantages (hot reload
     without restart, editing from the SMS page instead of Keys & Secrets) don't justify
     AIW's `system.config_entries` machinery for one service; trigger to revisit unchanged
     (§4).

## 1. Auth (v3 side)

- New KV secret **`SologicalSms--Admin--ApiKey`** (32B base64). Config path `SologicalSms:Admin:ApiKey`.
- All `/api/admin/*` routes require header **`X-Admin-Key`** — constant-time compare against the
  configured value. Missing/wrong → `401 {"error":"invalid_admin_key"}`. If the service has NO
  admin key configured, every admin route → `503 {"error":"admin_disabled"}` (fail closed).
- Admin routes sit under the global per-IP rate limit (already shipped); no send-policy.
- **Internal path only (Ray's question, 2026-08-01: "is an API key sufficient?").** The key is
  cryptographically fine (256-bit, vault-held, machine-only, fail-closed) — the exposure to remove
  is REACHABILITY. `/api/admin/*` is served only to requests arriving on a non-public host:
  config `SologicalSms:Admin:AllowedHosts` (default `ca-sologicalsms,localhost`); requests whose
  Host is the public hostname → `404` (indistinguishable from no-such-route). The ACA-internal
  DNS name is unreachable and unspoofable from the internet (public ingress routes bound
  hostnames only), so the admin surface simply does not exist externally; the key is the second
  layer, not the only one.
- **Why not Entra/managed-identity tokens now:** all three container apps share ONE managed
  identity (`id-aiworkforce-dev`) and one vault — a token proving "caller is that identity"
  admits Billing too; ceremony, not isolation. UPGRADE TRIGGER (recorded): when apps get per-app
  identities (production hardening), switch admin auth to audience-bound Entra tokens and retire
  the shared key.
- Every mutating admin call logs a structured Serilog line (`AdminAction` + route + payload keys).
  A dedicated `admin_audit` table is FUTURE (mirror AIW's config-audit tab when it earns its keep).

## 2. Management API (v3) — all JSON, `ApiError` envelope on failure, cursor pagination (`?limit=&before=<id>`)

### 2.1 Overview
- `GET /api/admin/overview` → `{ healthy, counts24h: {queued, sent, delivered, failed, rejected,
  expired, duplicate}, inbound24h, outbox: {pending, dead}, breaker: {active: bool, trippedAt?,
  unmatchedCount?}, channels: [{key, status, originator}] }`. One round trip for the Overview tab.

### 2.2 Customers
- `GET /api/admin/customers` → list (id, code, name, status, channel count).
- `POST /api/admin/customers` `{code, name}` → 201.
- `PATCH /api/admin/customers/{id}` `{name?, status?}` (status: active|disabled).

### 2.3 Channels
- `GET /api/admin/channels` → list incl. status, originator, upstream, webhook_url set?,
  daily_part_limit, allowed_recipients, duplicate_window_seconds, key slots configured (bool each,
  never hashes).
- `POST /api/admin/channels` `{customerCode, key, originator, description?, webhookUrl?,
  dailyPartLimit?, allowedRecipients?}` → 201 `{channel, apiKey}` — **plaintext key returned
  exactly once**, generated server-side (32B base64), stored as SHA-256 in slot 1. Originator must
  be whitelisted or the call fails `originator_not_whitelisted` (guard parity — never seed a
  channel that can only reject).
- `PATCH /api/admin/channels/{key}` — any of `{status (active|paused), description, webhookUrl,
  webhookSecretName, dailyPartLimit, allowedRecipients, duplicateWindowSeconds}`. Pausing is the
  kill-switch; activating a channel while its upstream breaker is latched fails `breaker_active`.
- `POST /api/admin/channels/{key}/rotate-key` `{slot: 1|2}` → `{apiKey}` once. Two-slot model:
  rotate the unused slot, move the client, then rotate the old slot — zero-downtime rotation.

### 2.4 Sender-ID whitelist
- `GET /api/admin/originators` / `POST {originator, description}` / `DELETE /{originator}`.
- DELETE fails `originator_in_use` if any non-disabled channel sends as it.

### 2.5 Messages (read-only explorer)
- `GET /api/admin/messages?channel=&status=&to=&since=&until=&limit=&before=` → newest-first page.
- `GET /api/admin/messages/{id}` → full row + `deliveryEvents[]` (raw status/code/payload
  timestamps) + `outboxEntries[]` (state, attempts, next_attempt_at, last_error) — the DR timeline
  the detail drawer renders.

### 2.6 Inbound (read-only)
- `GET /api/admin/inbound?channel=&since=&until=&limit=&before=` — incl. reply_to_message_id and
  quarantined (Operator-channel) rows.

### 2.7 Webhook outbox
- `GET /api/admin/outbox?state=pending|dead&channel=` → rows incl. attempts, last_error.
- `POST /api/admin/outbox/{id}/retry` → dead|pending row: reset to pending, `next_attempt_at=now()`,
  attempts preserved. (This is the manual "nudge" we've done by SQL until now.)

### 2.8 Breaker
- `GET /api/admin/breakers` → active + history (tripped_at, unmatched_count, alert_sent_at,
  alert_error, re_armed_at).
- `POST /api/admin/breakers/{id}/re-arm` → sets `re_armed_at`. Deliberately does NOT unpause
  channels — the operator re-activates each channel explicitly (§2.3) after investigating.

### 2.9 Service
- `GET /api/admin/usage?customerCode=&month=YYYY-MM` → ledger rollup per channel per day
  (outbound/inbound units) — the billing view.
- `POST /api/admin/service/restart` → 202, logs loudly, then graceful `IHostApplicationLifetime.
  StopApplication()`. ACA (min replicas 1) / docker (`--restart unless-stopped`) revive it with
  fresh KV values — this is the "apply config changes" button. UI must confirm twice.

## 3. Phase 2 — AI-Workforce work order (agent-implementable)

### 3.1 Server-side proxy (System service)
- New `Features/Comms/SmsAdmin/SmsAdminController.cs`: routes `/api/sms-admin/{**path}` (GET/POST/
  PATCH/DELETE) → forward verbatim to `{Sms:Sological:AdminBaseUrl}/api/admin/{path}` with
  `X-Admin-Key: {Sms:Sological:AdminApiKey}`. `[Authorize(Roles = SystemRoles.AdminOwnerOrService)]`.
  **`Sms:Sological:AdminBaseUrl` is the INTERNAL address** (`http://ca-sologicalsms` in Azure,
  `http://localhost:5230` locally — new non-secret manifest/KV value + emulator seed entry) —
  never the public custom domain, which refuses admin routes (§1).
  30s timeout; pass status + body through unmodified (the UI renders v3's ApiError envelopes);
  502 `{"error":"sms_service_unreachable"}` on transport failure. NO caching, NO local state.
- Config: `Sms:Sological:AdminApiKey` read via IConfiguration (KV-backed; AIW's push-reload makes
  rotations live in ~1-2s on the AIW side).

### 3.2 Dedicated page (admin-dashboard)
Ruling (Ray, 2026-08-01): **own page**, not a System Settings tab — this is an operational
workbench over entities, not settings. Left nav: **"SMS Service"** beside the existing Comms
entry. Route `/system/sms`. Tabs:

| Tab | Backed by | Notes |
|---|---|---|
| Overview | `GET overview` | Health tile, 24h status counts, outbox pending/dead tiles, **breaker banner** (red, sticky, with Re-arm button + double confirm) |
| Channels | §2.3 | Table; create modal (shows the key ONCE with copy button + "you will not see this again"); edit drawer (pause/activate toggle, quota, allowlist chips, webhook URL, duplicate window); rotate-key flow (slot picker → key shown once) |
| Messages | §2.5 | Filters (channel, status, recipient, date); detail drawer with DR timeline + outbox delivery state |
| Inbound | §2.6 | Same explorer pattern; quarantine rows flagged |
| Whitelist | §2.4 | Add/remove ACMA sender IDs; blocked-delete explains which channel uses it |
| Outbox | §2.7 | Pending + dead lists; Retry button per dead row |
| Usage | §2.9 usage | Month picker, per-channel/day units table |

Reuse existing admin table/drawer/tab components and the System Settings visual patterns. All
calls via `/api/sms-admin/*` (never direct to v3).

### 3.3 ConfigManifest additions (Keys & Secrets tab — NOT the new page)
Add a "SMS Service (Sological v3)" group. These live in the SAME shared vault, so drift checking
and write-only rotation work unchanged; entries consumed by v3 note "applies on SMS service
restart" in their description:

| Manifest key | Secret | Rotatable | Consumer |
|---|---|---|---|
| `Sms:Sological:AdminApiKey` | yes | yes | AIW System (proxy) + v3 (`SologicalSms--Admin--ApiKey` — see §5 pairing) |
| `SologicalSms:SmsCentral:User` | yes | yes | v3 |
| `SologicalSms:SmsCentral:Password` | yes | yes | v3 |
| `SologicalSms:Ingress:VerifyKey` | yes | yes | v3 (+ SMS Central portal header — rotate together) |
| `SologicalSms:Webhook:AIWorkforce` | yes | yes | v3 signer — **must equal** `Sms:Sological:WebhookSecret` (already in manifest) |
| `SologicalSms:ConnectionStrings:DefaultConnection` | yes | **no** (`rotation_disabled` — out-of-band with a migration plan, like AIW's conn strings) | v3 |
| `Sms:Sological:BaseUrl` | no (value) | yes | AIW (already in manifest via 2026-07-26 audit — verify) |
| `Sms:Sological:AdminBaseUrl` | no (value) | yes | AIW System proxy — INTERNAL address (§3.1) |
| `SologicalSms:RateLimits:*` (SendPerSecond, SendBurst, GlobalPerMinute, IngressPerMinute, MaxRequestBodyBytes) | no (values) | yes | v3 — per-env tuning, survives deploys; appsettings holds safe defaults |
| `SologicalSms:Breaker:*` (UnmatchedThreshold, WindowMinutes, AlertNumber, AlertOriginator) | no (values) | yes | v3 — ditto; `AlertNumber` moves here FROM bicep env |

- `ops/seed_keyvault_emulator.ps1`: add `Sms:Sological:AdminApiKey` to `$secretKeys`.

## 4. Ruling: no psql config table for v3

Compared against AIW's model (docs/infrastructure/config/configuration-reference.md): AIW moved
runtime-tunables to `system.config_entries` because SEVEN services need the same values with
30s-sentinel/push hot-reload. v3 is one service; its genuinely operational knobs are per-entity
columns already in its own DB (tier 1 above), and its tuning globals live as KV VALUES (tier 3
as amended 2026-08-01) — which already deliver Ray's two hard requirements: deploys can never
override them (bicep carries no tuning), and every environment differs by construction (one
vault each). What a psql table would still add — hot reload without restart, editing from the
SMS page instead of Keys & Secrets — doesn't justify AIW's config machinery for one service.
**Decision: no table; tunables in KV.** Trigger to revisit: operators tuning limits more than
~monthly, hot-reload becoming a real need, or a second v3 replica/service appears.

## 5. Secrets runbook (add / update / rotate)

**Add now (Phase 1 prerequisite):**
| Where | Name | Value |
|---|---|---|
| `kv-aiworkforce-dev` + local KV emulator | `SologicalSms--Admin--ApiKey` | fresh 32B base64 |
| both vaults | `Sms--Sological--AdminApiKey` | SAME value (AIW-side read; keeps both key families in their owners' namespaces) |

**Rotation procedures (steady state):**
| Secret | Procedure |
|---|---|
| Channel API keys | Management API two-slot rotation (§2.3) — v3 stores hashes only, KV never involved on the v3 side; update the consumer's copy (e.g. AIW's `Sms--Sological--ApiKey`) between slot rotations |
| `SologicalSms--Admin--ApiKey` + `Sms--Sological--AdminApiKey` | Rotate as a pair; v3 restart applies; AIW picks up via push-reload |
| `SologicalSms--SmsCentral--User/--Password` | New password in SMS Central portal → both KV entries → v3 restart. Brief send-outage window; do while channels are quiet |
| `SologicalSms--Ingress--VerifyKey` | New value in KV → v3 restart → update SLVERIFY header on the sub-account's webhook forwards. Pushes with the old key 401 between restart and portal update — SMS Central retries cover the gap |
| `SologicalSms--Webhook--AIWorkforce` + `Sms--Sological--WebhookSecret` | MUST move together (signer/verifier pair): set both → v3 restart (AIW hot-reloads). Outbox retries absorb any brief HMAC mismatch |
| Connection string | Out-of-band, non-rotatable via UI |

**Cadence:** dev = on suspicion only; prod (later) = 90-day cadence for SmsCentral + VerifyKey +
webhook pair, two-slot channel keys per customer policy.

## 6. Build order & acceptance

**Phase 1 (this repo):** admin auth + §2 endpoints + tests (auth 401/503 fail-closed, create/rotate
returns key once, retry resets outbox row, re-arm doesn't unpause, restart endpoint stops host).
Seed `SologicalSms--Admin--ApiKey` in both vaults. Deploy both environments.
**Phase 2 (AI-Workforce repo, separate agent):** §3 proxy + page + manifest + emulator seed.
Acceptance: from the AIW admin UI — pause/unpause a channel, create a channel and copy its key,
whitelist an originator, browse a message's DR timeline, retry a dead outbox row, re-arm a test-
tripped breaker, rotate the VerifyKey from Keys & Secrets and Apply via the restart button — all
without touching psql or az.
