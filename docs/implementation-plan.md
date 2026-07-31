# Implementation plan — slices S1–S7

**Created:** 2026-07-27
**Modified:** 2026-07-27
**Status:** design signed off (Ray, 2026-07-27). **S1 DONE (2026-07-27)** — S2 is next; S5–S7 planned.

Rules of the road: each slice lands complete and verified (suite green + the slice's named
gate) before the next starts; open work carries an explicit OPEN marker here. Surfaces are
listed per slice — scope-narrowing is Ray's call.

## S1 — Skeleton + schema ✅ DONE 2026-07-27

Landed as designed; notes for whoever picks up S2:

- Solution `Sological.Sms.sln`: `src/Sological.Sms.Core` (entities/enums, dependency-free),
  `src/Sological.Sms.Service` (host + `Data/SmsDbContext` + migrations), `tests/`.
- Migration `20260727004703_InitialSchema` creates all eight design-§3 tables (snake_case via
  EFCore.NamingConventions, enums stored lowercase, payloads jsonb). Claim columns exist on
  BOTH `messages` and `webhook_outbox` (design §3 named them for messages; §3/§9 name the
  outbox worker claim-based, so the same pair ships there).
- Gate evidence: integration tests (Testcontainers, postgres:17-alpine) prove clean apply on
  an EMPTY database + idempotent re-apply + full-table round-trip incl. jsonb; live boot
  against a throwaway empty PG migrated on start and served `/health` 200 twice (first +
  second boot). Unit suite 6/6 with the CI filter; full suite 8/8. CI workflow
  (`.github/workflows/ci.yml`) runs unit-only per the standing rule — first hosted run
  happens whenever Ray first pushes.
- A `unit test` reminder for later slices: `ModelTests.Model_HasNoPendingChanges…` fails the
  suite if an entity change ships without its migration.

Original slice text (for reference):

.NET 10 solution: `Sological.Sms.Service` (ASP.NET minimal API host), `Sological.Sms.Core`
(domain), `Sological.Sms.Tests`. EF Core + Npgsql; own database `sologicalsms` on the existing
Azure PSQL server (local dev: local PostgreSQL). Migrations run on service start (same pattern
as AI-Workforce System). Health endpoint. Dockerfile + container-app-ready config
(Key Vault-backed secrets in Azure; the shared KV **emulator** locally — design §8).

Schema (design §3): `customers`, `channels`, `messages`, `delivery_events`,
`inbound_messages`, `inbound_parts`, `billing_ledger`, `webhook_outbox`.

- **Surfaces:** `GET /health`. No customer surface yet.
- **Gate:** migrations apply clean on empty DB; CI (GitHub Actions) builds + tests green.

## S2 — Send lane (SMS Central driver) — ✅ DONE 2026-07-27 (gate passed)

Everything below is built and green (58/58; integration suite runs the whole lane on a fake
upstream — the real SMS Central API is NEVER called by tests, Ray's standing rule). Landed
with the build: migration `S2SendLane` (`allowed_originators` whitelist + per-channel
`duplicate_window_seconds` default 3600 + retry columns), the ACMA originator-whitelist
guard, the one-channel-per-sender-ID rule (`400 originator_mismatch`), guard order
recipient-normalize → duplicate → whitelist, `Retryable` on `UpstreamSubmitResult`,
`ops/seed-channel.sql` template. Sub-account username is in the local KV emulator
(`SologicalSms--SmsCentral--User`).

**OPEN — the gate needs Ray:**
- ✅ sub-account password in the KV emulator (`SologicalSms--SmsCentral--Password`, Ray,
  2026-07-27) — creds complete.
- ✅ whitelist seeded: the 14 ACMA-registered "Ready to use" sender IDs
  (`ops/seed-whitelist.sql`; ACMA matching is case-insensitive, v3 stores/enforces the
  register's display casing). `AIWorkforce` (SOLOGICAL PTY LTD) is registered — the likely
  §10 Q2 answer for SENDING; note an alpha sender ID cannot receive replies, so inbound for
  AI-Workforce still needs the dedicated-number decision by S3/S4.
- ✅ AI-Workforce channel seeded (customer `aiworkforce`, channel `AIWorkforce`, originator
  `AIWorkforce`, hashed API key; plaintext handed to Ray in-session).
- ✅ **GATE PASSED 2026-07-27** (Ray approved: "smoke it"): live SMS to Ray's test number
  through the sub-account — 202 queued → worker → upstream accept → `sent` on the first
  attempt (`smoke-001`, msg `019fa182-a3b8-7c1a-a698-00f7aed09439`), ledger row 1 unit
  outbound/message, **handset receipt confirmed by Ray** (sender displayed as the
  registered `AIWorkforce` ID), **and the message is visible in the SMS Central
  SUB-account portal** — confirming the isolation model: v3 traffic rides the sub-account,
  the old gateway's account untouched. Status stays `sent` until S3 builds the DLR
  receiver — `delivered` is S3's gate, not S2's.

Original slice text:

Customer send API (`POST /api/v1/messages`, per-channel API key auth) → message row →
dispatch worker: **pre-dispatch guards first** (duplicate detection + local recipient
validation + **originator whitelist** — design §6.1a, terminal + unbilled; whitelist ruled
2026-07-27, ACMA sender-ID enforcement, table ships in this slice's migration) →
`ISmsUpstream` seam → **SmsCentralUpstream**
(POST `wrapper/sms`, `REFERENCE` = our message id) → status transitions + billing ledger row
(parts counted by GSM7/UCS-2 rules). Send status query API.

- **Surfaces:** `POST /api/v1/messages` · `GET /api/v1/messages/{id}` · admin-less channel
  seeding via config/SQL for now (admin CRUD is S7).
- **Gate:** live smoke — one real SMS to Ray's number through the sub-account, message row
  reaches `sent`, ledger row correct. ⚠ Prereq on Ray: SMS Central **sub-account created**
  (creds + originator/number decision for AI-Workforce).

## S3 — Upstream ingress (DLR + inbound receivers) — ✅ DONE 2026-07-30 (gate passed)

**GATE PASSED 2026-07-30**, all legs live on the sub-account webhooks (Ray's portal
template-picker config + our receivers):
- send `s3-gate-004` → DR chain fired (enroute→submitted→delivered) → message
  **`delivered` automatically** (first receipt content-matched, mtId `d9867fc8…`
  backfilled to `upstream_id`, delivered receipt matched by mtId).
- Ray's reply → inbound row on the right channel with **exact `reply_to_message_id`
  correlation via mtId** — works even for alpha-originator channels.
- >160-char reply (476 chars, prior cycle) arrived **platform-preassembled, complete** —
  the multipart leg; our UDH parts buffer stays as the legacy-path fallback.
- SLVERIFY arrived+matched on every live push → **`RequireVerification=true` deployed**
  (verified 401 without the header through the tunnel).
- Correlation ruling (proven): `$metadata` does NOT round-trip the wrapper REFERENCE
  (unresolved Velocity literal came back). Chain instead: REFERENCE uuid (legacy format)
  → `upstream_id`==mtId → unique content-match (mtContent+handset, 72h) → audit-only.
  S6's REST sends will know mtId at submit time and skip the content-match rung entirely.

Built, tested (102/102; ingress integration suite replays the exact query shapes from the
legacy live captures), and DEPLOYED to the local container behind the Cloudflare tunnel —
receivers respond on `https://sms-yoga.sological.io/ingress/smscentral/{delivery,inbound}`
(verified: bad creds → 401 through the tunnel). Implementation notes:

- **Receiver auth (Ray's ruling 2026-07-27: no creds in URLs)**: pushes verify via the
  `SLVERIFY` header — a shared key (`SologicalSms--Ingress--VerifyKey` in the secret
  store) configured on the portal's webhook headers, constant-time compared; wrong key →
  401. Legacy `USERNAME`/`USER_NAME`+`PASSWORD` params still validate when present but any
  creds params are REDACTED (`***`) before the payload persists. Nothing present is
  tolerated until `SologicalSms:Ingress:RequireVerification=true` — flip it once the
  portal is confirmed sending SLVERIFY on every push. Receivers take GET or POST
  (form-encoded).
- Mapping refinements over the design table: `RESULT=503` → `expired` (their documented
  meaning; the webhook contract already exposes it), `DELIVRD`/`DELIVERD` both accepted
  (their docs use both spellings). BINARY decodes UTF-16BE for DCS 8, Latin-1 otherwise
  (their spec — no GSM7 bit-packing).
- Multipart DLR: per-part receipts tracked via UDH; delivered only when every part
  confirms; any part failing ⇒ failed; first terminal verdict wins.
- Inbound: REFERENCE (our uuid) → RECIPIENT (dedicated number) → operator quarantine
  channel (system row, created at startup, paused, key `Operator`). Reassembly buffers in
  `inbound_parts` under a pg advisory lock; the sweeper flushes stale groups partial
  (`complete=false`) after 60s.

**OPEN — the gate needs Ray:**
- ⚠ set the sub-account **forward URLs** (both POST per the portal, form-encoded assumed —
  if the portal turns out to send JSON bodies, the receivers need a JSON parser first):
  delivery → `https://sms-yoga.sological.io/ingress/smscentral/delivery`, inbound →
  `https://sms-yoga.sological.io/ingress/smscentral/inbound`, both with the `SLVERIFY`
  header (key: `SologicalSms--Ingress--VerifyKey`). Sub-accounts DO have their own webhook
  config (§10 Q1 answered — Ray found the screens; independence confirmed at first push).
- ✅ replyable lane ready: sub-account dedicated number **+61 438 887 301** (Ray,
  2026-07-27) → channel `AIWorkforceReply` (originator `0438887301`, whitelisted, own API
  key held by Ray). The gate's reply/multipart legs send FROM this channel so Ray's phone
  can reply; `AIWorkforce` (alpha ID) stays the outbound-only lane.
- ⚠ **live gate, Ray-approved only**: send from `AIWorkforceReply` → DLR arrives →
  `delivered`; reply from Ray's phone → `inbound_messages` row correlated via REFERENCE;
  a >160-char reply reassembles from parts. Confirm SLVERIFY arrives on real pushes, then
  flip `SologicalSms:Ingress:RequireVerification=true`.
- **Gate status 2026-07-29 — partially proven, BLOCKED on account manager.** Proven live:
  send → sent, DR webhooks arrive within seconds, SLVERIFY header arrives AND matches.
  Blocked: webhook pushes have EMPTY bodies (portal webhooks carry no payload template),
  so no status/reference/reply content lands. Fix requires either a template field in the
  portal webhook UI (unconfirmed), the legacy Rules&Triggers-style forwarding (REFERENCE
  round-trip format — what production's gateway still receives), or REST API keys to
  manage webhook templates via the Management API. Credential taxonomy settled
  (docs.app.api.sinch.com + hub API-settings layout): `sological2`+password = LEGACY-class
  creds (wrapper); REST wants a separate revocable Basic/HMAC key pair — self-service in
  the Hub for the test account, not exposed (yet) on the sub-account portal. Ray is
  waiting on his account manager for: sub-account REST keys, webhook-template capability,
  test-account credit/sender-ID, and the RCS-on-AU questions (roadmap Phase 3). Also
  noted: the Hub's "billing units in Delivery Reports and Callbacks" toggle feeds S7
  reconciliation — enable on production when available.
- **2026-07-30 — REST auth + webhook definitions PROVEN on the new Sinch (test) account.**
  Ray minted a Basic key pair ([AIDev] → `SologicalSms--SinchTest--ApiKey|ApiSecret`);
  read-only probes 200 on `au.app.api.sinch.com`. Both webhook definitions (JSON-encoded
  Velocity templates + SLVERIFY header; DR id `1a1464d6…`, MO id `12bb6439…`) were created
  via the Management API and read back verbatim — the empty-body problem is solved in
  principle; the definitions just need to exist on the PRODUCTION sub-account. Remaining
  account-manager ask narrows to: REST key pair for `sological2` (then I apply the same
  two definitions there myself), test-account credit + sender ID for S6 rehearsal, RCS
  questions. Test account cannot have sub-accounts yet; sends need manual credit.

Original slice text:

The two GET receivers SMS Central pushes to (design §5): delivery (`RESULT`/`STATUS` →
normalized event → message state + `delivery_events` row) and inbound (multipart reassembly
via `UDH`/`BINARY`/DCS, reply correlation via `REFERENCE`, channel routing via `RECIPIENT`
then referenced message). Public HTTPS hostname for the receivers.

- **Surfaces:** `GET /ingress/smscentral/delivery` · `GET /ingress/smscentral/inbound`
  (creds-validated, `200`+`0` ack contract).
- **Gate:** live smoke — send → DLR arrives → message `delivered`; reply from Ray's phone →
  `inbound_messages` row correlated to the sent message. Multipart inbound (>160 chars)
  reassembles. ⚠ Prereq on Ray: sub-account forward URLs set to the receivers.

## S4 — Customer webhook egress + AI-Workforce wiring — ⏳ v3 SIDE BUILT 2026-07-30

v3's half is built, tested (125/125) and deployed: outbox rows are written in the SAME
transaction as every state change (dispatch: sent/rejected/failed; DLR ingress:
delivered/failed/expired/rejected; inbound ingress + sweeper: sms.inbound with `replyTo`
when correlated — explicit `null` otherwise, per contract); the egress worker claims
(FOR UPDATE SKIP LOCKED, lease-expiring), signs the exact bytes sent
(`X-Sms-Signature: hmac-sha256=<hex>`, per-channel secret resolved by NAME from config/KV),
POSTs, retries 1m→5m→30m→2h on the DB clock, then `dead` LOUDLY; sms.inbound success
stamps `delivered_to_customer_at`. `GET /api/v1/inbound?since=` poll-parity endpoint live.
`duplicate` is deliberately not a webhook status (§6.2 vocabulary); it stays visible on
the status API. No schema change needed — S1's outbox table carried everything.

**AI-Workforce half BUILT 2026-07-30** (AI-Workforce repo `df90cde34` + `774a3b6fb`,
suites green): provider repointed to `POST /api/v1/messages` (202 messageId = real
provider_ref; smsdr/UseFallBackEndPoint/SmsFallbackEnabled deleted), unified
`POST /api/sms/webhook` with **HMAC verification over the raw body (Q4 RULED: HMAC,
2026-07-30; X-Sms-Api-Key retired)**, `expired` → Undeliverable, and exact reply
correlation via `provider_ref` (window heuristic kept as fallback). Config seeded in the
shared KV emulator: `Sms:Sological:BaseUrl` (http://localhost:5230),
`Sms:Sological:WebhookSecret` == `SologicalSms:Webhook:AIWorkforce` (one shared HMAC
secret). Both `AIWorkforce` and `AIWorkforceReply` channels subscribed to
`https://system-yoga.sological.io/api/sms/webhook`. **The `AIWorkforce` channel is PAUSED**
— the first-class kill-switch replacing the AIDemo mismatch, now that AI-Workforce holds a
live key (a dev send gets an honest 403 channel_paused).

**OPEN — the gate run (needs Ray):**
- ⚠ stand up the `system-yoga.sological.io` tunnel → the AI-Workforce gateway.
- ⚠ decide the gate send lane: unpause `AIWorkforce` (alpha sender, delivery leg only —
  not replyable) and/or swap AI-Workforce's `Sms:Sological:ApiKey` to the
  `AIWorkforceReply` channel key so case sends ride the dedicated number and the reply leg
  works end-to-end (recommended for the gate; §10 Q2's dedicated-number answer in action).
- ⚠ **Gate (Ray-approved)**: case SMS send → delivery signal lands on the case; customer
  reply → case-targeted inbox signal via exact `replyTo` correlation. **Closes the
  capability gap that started the project.**
- **2026-07-31: TRANSPORT LOOP CLOSED LIVE.** AIW test-send → v3 → SMS from 0438887301 →
  Ray's phone; DR chain → `delivered` (mtId); reply → exact `reply_to`; all three signed
  webhooks delivered (after fixing the container's missing webhook-secret env — `941be92`);
  AIW's contact row settled `status=delivered` with `provider_ref` = the v3 message id
  (verified in comms.contacts), reply correctly non-case (source=test). **Remaining for
  the formal gate: one MEDULLA/case send → reply → case-targeted inbox signal.**
- **Gate-run status 2026-07-30:** receive pipe FULLY verified (signed probe →
  system-yoga tunnel → AIW gateway → HMAC ✓ → 200). Key rotated to the reply channel via
  the AIW Keys & Secrets UI (write-through verified in the vault; binds on next System
  restart). No send has entered the pipeline yet: the 403 in the AIW logs was the
  TEMPLATE SAVE endpoint, and the SMS send-test isn't enabled in the current AIW build —
  both AI-Workforce-UI matters, being taken up in that repo's own session. v3 side needs
  nothing and is watching. Recipient-gate note: dev REDIRECTS all SMS to Ray's number, so
  no whitelist rule is needed (redirect still sends; only block stops).

> Staged already (2026-07-27): the v3 `AIWorkforce` channel's API key is in AI-Workforce's
> local KV emulator secret `Sms:Sological:ApiKey` — the repoint will use it as `X-Api-Key`.
> The v3 API has NO Channel parameter (the key IS the channel identity), and this v3
> channel is the SUCCESSOR of legacy `AIDemo`/`AIDemoX` — same concept/use (AI-Workforce's
> SMS lane), renamed `AIWorkforce` (Ray, 2026-07-27). The AIDemo kill-switch mismatch stays
> in place (and keeps blocking dev sends at the old gateway) until the S4 repoint, when
> channel `status=paused` becomes the first-class control. More local dev channels are
> expected (Ray, 2026-07-27 — candidates `ElecDemoAi`, `ElecDemoHuman`). RULED (Ray,
> 2026-07-27): at this stage they ALL send as the `AIWorkforce` sender ID — sharing an
> originator across channels is allowed (the one-channel-per-sender-ID rule means a channel
> has exactly ONE sender, not the converse); in PROD, different tenants get different
> registered sender IDs. Still to decide at mint time: (a) AI-Workforce secret naming —
> prefer `Sms--Sological--{channel}--ApiKey` so `ApiKey` doesn't become both leaf and
> section; (b) dev channels seed as `status=paused` by default — un-pause deliberately.

Webhook outbox worker: per-channel `webhook_url` + secret; `sms.inbound` + `sms.delivery`
JSON POSTs, HMAC-signed, retry with backoff + dead-letter marking. Poll-parity endpoint for
debugging. Then the AI-Workforce side (in the AI-Workforce repo, its own commit set): point
`SologicalSmsProvider` at v3's send API, align `SmsWebhookController` to the v3 payloads,
delete the `smsdr` misread, upgrade `InboundSmsHandler` correlation to use `replyTo` when
present (window heuristic stays as fallback).

- **Surfaces:** outbound webhooks (contract in design §6) · `GET /api/v1/inbound?since=` ·
  AI-Workforce: `Sms:*` config repoint + webhook payload alignment.
- **Gate:** end-to-end through AI-Workforce comms — case SMS send → delivery signal lands on
  the case; customer reply → case-targeted inbox signal via exact `replyTo` correlation.
  **This closes the capability gap that started the project.**

## Azure DEV deployment — ⚠ ACTIVE, requested by Ray 2026-07-31

The AI-Workforce Azure dev instance needs v3 reachable in Azure (local docker + tunnels
serves only the local dev loop). Target per feasibility/§9: Container App beside the
Billing service, own database `sologicalsms` on the existing Azure PSQL server, real Key
Vault carrying the same secret names (`SologicalSms--*`), migrations-on-start already
built for it. **RULED (Ray, 2026-07-31): ALL SHARED resources** — the existing resource
group/Container Apps environment, the existing Azure PSQL server (new `sologicalsms`
DATABASE only, no new server; single-zone + PITR for dev per §9), the existing Key Vault
and registry. Still to decide: dev ingress hostname, image shipping (CI vs manual push —
CI implies the repo's first git push), and which channels the Azure instance serves (own
AIW dev channels — the ElecDemo* idea — vs sharing local; webhook URLs differ per
instance). SMS Central webhook forward URLs stay pointed at sms-yoga (local) until Ray
decides which instance owns upstream ingress in dev.

**DEPLOYED 2026-07-31** — v3 is live in Azure dev: **`https://sms.dev.ai-workforce.au`**
(custom hostname, ACA managed cert auto-renewing; CNAME + `asuid.sms.dev` TXT at ClouDNS;
underlying FQDN `ca-sologicalsms.orangesky-625283b8.australiaeast.azurecontainerapps.io`
also still answers). `/health` = Healthy; migrations ran on first boot.
`Sms--Sological--BaseUrl` = the custom hostname; the second sub-account's webhook
forwards should use it too. `ops/infra/container-apps.bicep`
+ `.bicepparam` (modeled on Billing's), image `craiworkforcedev.azurecr.io/sologicalsms:ff6bae5`
built+pushed manually (no git push — per standing rule). Seeded: 14-row ACMA whitelist,
customer `aiworkforce`, channel `AIWorkforce` (originator `AIWorkforce`, upstream smscentral,
**status=paused** — the kill-switch stays on until SMS Central creds exist and Ray gates a
smoke). Operator channel auto-bootstrapped paused. Real KV (`kv-aiworkforce-dev`) carries:
`SologicalSms--ConnectionStrings--DefaultConnection`, `--Ingress--VerifyKey` (fresh),
`--Webhook--AIWorkforce` (fresh; same value stored as `Sms--Sological--WebhookSecret`),
`Sms--Sological--ApiKey` (the Azure channel's plaintext key), `Sms--Sological--BaseUrl`
(the FQDN). NO `SologicalSms--SmsCentral--User/--Password` in Azure yet — deliberate.
Verified live: POST /api/v1/messages with the channel key → `403 channel_paused`
(auth + DB + KV chain proven, zero upstream contact).

**Still open to go live**:
1. ~~Second sub-account~~ DONE 2026-07-31: sub-account `AIWorkforce-dev` created, wrapper
   creds in `kv-aiworkforce-dev` (`SologicalSms--SmsCentral--User/--Password`), revision
   restarted Healthy with them bound. Webhook forwards configured by Ray and PROVEN LIVE
   2026-07-31: gated smoke msg `019fb65e-fa26-…` → wrapper accepted → TWO DRs pushed to
   `sms.dev.ai-workforce.au` through strict SLVERIFY, template params fully populated,
   mtId-correlated, statuses mapped honestly (`enroute`/101 then `rejected`/333).
   **BLOCKER (carrier-side): wrapper/DR code 333 = alpha sender `AIWorkforce` not
   ACMA-registered FOR THIS SUB-ACCOUNT** (SMS Central emailed Ray confirming: unregistered
   alpha sender IDs are not delivered). The register entry (SOLOGICAL PTY LTD) authorizes
   the ORIGINAL sub-account only — Ray to add `AIWorkforce-dev` to the nomination via
   portal/account manager. Channel re-PAUSED; no sends without Ray's explicit go.
   Same `reference`=`$metadata.get('REFERENCE')` unresolved-literal quirk as local —
   harmless (mtId chain does correlation). No dedicated inbound number on this
   sub-account yet — Azure = send + delivered-DLRs, replies stay local-dev.
2. ~~AIW Azure pre-S4~~ DONE 2026-07-31: AIW CD run 30602404379 deployed the S4 commits;
   probe of `https://ca-gateway.orangesky-….azurecontainerapps.io/api/sms/webhook`
   returned `401 invalid signature` — S4 controller live, vault WebhookSecret bound.
   v3's `AIWorkforce` channel `webhook_url` now points at that gateway URL. Both
   directions wired: AIW→v3 (BaseUrl+ApiKey from vault) and v3→AIW (webhook_url+HMAC).
   Azure loop now blocked ONLY by the sender-ID registration in item 1.

## Public-surface security slice (discussed 2026-07-31, awaiting Ray's go)

Context: `sms.dev.ai-workforce.au` AND `sms-yoga.sological.io` are internet-facing; the
API is single-message by DESIGN (no batch — upstream RECIPIENTMESSAGES rejected all-or-
nothing, which fights per-message honesty; a bulk endpoint fanning out to N messages is
the S7-shaped answer if a real need appears). NO rate limiting exists today. Agreed
threat framing: a leaked CHANNEL key = full channel compromise (smishing as the ACMA-
registered sender at our cost) until paused/rotated; a compromised SUB-ACCOUNT credential
shows up as DRs that correlate to nothing (v3 never sent them — only portal test sends
legitimately produce these).

The slice (contained: guard-chain/ingress work + config, no UI dependency):
1. **Rate limits + body cap** — ASP.NET built-in limiter: token bucket per API key on
   send (~5/s burst 10, 429+Retry-After), per-IP window globally (~100/min; ingress
   routes ~300/min — DR bursts clump), ~64KB body cap. Config `SologicalSms:RateLimits`.
2. **Per-channel daily part quota** — nullable `daily_part_limit`, dispatch guard,
   terminal `quota_exceeded` (loud). Caps total damage/day from a polite attacker.
3. **Dev-channel recipient allowlist** — per-channel `allowed_recipients` (null =
   unrestricted): dev channels can ONLY text 0408004199 → leaked dev key = nuisance,
   not incident.
4. **Unknown-DR circuit breaker** — windowed count of uncorrelated delivery_events
   (~10 in 5 min, configurable) → auto-pause ALL channels on that upstream, LATCHING
   (human re-arms). Detects sub-account cred abuse in minutes; remediation is still
   rotating the SMS Central password — the breaker stops co-mingled spend and raises
   the flag. Portal test sends stay under threshold by design.
   **+ operator alert SMS (Ray, 2026-07-31)**: on trip, ONE best-effort SMS to
   `SologicalSms:Operator:AlertNumber` (0408004199) via the Operator channel with a
   breaker exemption — the latch makes it one-shot (no storm). Caveats accepted: it
   rides the suspect upstream (protection is the latch, SMS is only notification;
   S6's second driver can route alerts around the suspect upstream later), and the
   alert originator must be REGISTERED on the sub-account (today's 333 lesson —
   register `SoLogical` alongside `AIWorkforce` when doing the AIWorkforce-dev
   nomination, or the alert borrows the registered sender).

**Monitoring/alerting: LATER (Ray, 2026-07-31)** — detection layer beyond the breaker
(ledger anomaly checks, dead outbox rows, breaker state, uptime). Natural home: S7
management API exposes ops state, AIW admin UI surfaces it; alert delivery TBD.

**Earlier discovery notes** (setup begun, then paused for the v2→v3 repo rename):
- Shared-infra names confirmed (from `Billing\ops\infra\container-apps.bicepparam` +
  `AI-Workforce\ops\infra\main.bicep`): subscription `0f2cd63f…` / `rg-aiworkforce-dev` /
  `cae-aiworkforce-dev` / `craiworkforcedev.azurecr.io` / `kv-aiworkforce-dev` /
  identity `id-aiworkforce-dev` (client `9488f698…`) / `psql-aiworkforce-dev`, australiaeast.
- **DONE: `sologicalsms` database created** on `psql-aiworkforce-dev` (az CLI, name is
  version-neutral — unaffected by the rename). Nothing else exists in Azure yet.
- `Program.cs` needs NO changes: real-KV loading via `AzureKeyVault:VaultUri` +
  `DefaultAzureCredential`/`AZURE_CLIENT_ID` already in place (Billing pattern).
- Plan agreed with Ray: manual image push (no git push), default ACA FQDN for now,
  bicep modeled on Billing's `ca-billing-api` (external ingress → 8080, probes on
  `/health`, minReplicas 1). Deploy creds-less with channels paused.
- **Sub-account ruling pending**: recommended a SECOND SMS Central sub-account for the
  Azure instance (own webhook-forward config → Azure owns its DLR chain without touching
  local's; separate creds). Azure = send + delivered-DLRs; replies need a second dedicated
  inbound number (cost) — deferred. Ray to create the sub-account + provide wrapper creds;
  nothing blocks on it (slot creds into KV after, restart revision).

## S7 note (pulled-forward proposal, 2026-07-31 — awaiting Ray's ruling)

Ray proposed: v3 grows a MANAGEMENT API and the admin UI lives in AI-Workforce's admin
dashboard. Recommended shape: v3 stays the authority (standalone management API, operator-
class credential `SologicalSms:Admin:ApiKey` — never per-channel keys); AIW's System
service proxies server-side so the key never reaches a browser and the platform login is
inherited. Suggested pull-forward: a minimal ops slice (dead-letter view/requeue, channel
CRUD + key rotation, whitelist CRUD) ahead of full S7 reporting — current admin story is
hand SQL. Customer self-service, if ever, is a separate channel-scoped surface.

## S5 — Legacy emulation (before customer migration; not gating S6 design)

> ⚠ **S5 approach needs Ray's review before build (2026-07-31):** at least one live legacy
> caller supports only SSL 1.0-era protocols — no modern edge (Cloudflare, Container Apps
> ingress) will terminate that, so the plain repoint of `sms.sological.com.au` breaks that
> client. Likely shape: the old box (or a small shim on it) stays as a PROTOCOL-DOWNGRADE
> PROXY, accepting the ancient TLS and forwarding to v3's legacy-emulation routes; DNS
> repoint then only affects modern callers. Inventory which channels' callers have this
> constraint during the S5 planning pass.

Byte-compatible `isapi/submitsms.dll/sendsms`, `checkstatus`, `getsms` routes mapped onto the
v3 store (status letters N/S/D/F/E/I; `getsms` consume-once semantics preserved; tab-separated
response shapes exact). Channel = legacy `ExternalID`; ApiKey1/2 honored. **No IP allowlisting
in v3 (ruled 2026-07-27)** — `SMSValidIP` is not carried, so any legacy channel that
authenticates by IP alone today (the Delphi code only checks ApiKey when one is set) MUST be
issued API keys before its repoint. That key-issuance sweep is a migration prerequisite on the
customer checklist, not code — **the sweep list is known: channels 2, 7, 8, 46, 47, 52 (+ Test)**,
per [legacy-db-findings](legacy-db-findings.md). Import script: ACTIVE `SMSChannel` rows only
(the findings table; 62 dead channels archive, don't migrate; `AIDemoX`/`AIWorkforceDevX`
do NOT import either — superseded by the v3-native `AIWorkforce` channel, Ray 2026-07-27). Legacy emulation ACCEPTS
duplicate refs (legacy callers reuse them heavily) — ref-uniqueness is new-API-only.

- **Surfaces:** the three legacy routes on the legacy hostnames (`sms.sological.com.au`,
  `smsdr.sological.com.au` both → v3).
- **Gate:** a captured corpus of real legacy request/response pairs replays identically;
  one pilot customer channel repointed and observed for a week before the rest.

## S6 — Sinch Engage v1 driver + cutover

`SinchUpstream` against `au.app.api.sinch.com`: send (`metadata.ref` = message id,
`delivery_report: true`), webhook receivers (HMAC signature keys) as primary DLR/reply lane,
poll+confirm sweeper as catch-up. Same normalized events as S3 → zero change downstream.
Contract tests for both drivers from shared fixtures. Cutover runbook: freeze window, flip
per-channel upstream default, smoke suite, rollback = flip back (only while SMS Central still
lives — after discontinuation rollback is dead, hence the freeze window).

- **Surfaces:** `POST /ingress/sinch/messages` (webhook receiver) · channel config gains
  `upstream` (`smscentral` | `sinch`).
- **Gate:** full round-trip (send/DLR/reply) against the **credited test Sync account**;
  cutover rehearsal executed against test account before the real flip.
  ⚠ Prereq on Ray: credit the test account; account questions to Sinch (roadmap Phase 3 Q a/b
  can ride the same conversation).

## S7 — Billing reports + admin

Monthly per-customer rollups from `billing_ledger` (parts × direction × channel), daily
anomaly report (replaces the Delphi `DailyReport` emails), simple admin surface (channels
CRUD, key rotation, webhook config, message search). Auth for admin TBD (likely the existing
Azure AD tenant).

- **Surfaces:** admin API/UI · report emails.
- **Gate:** one month's ledger reconciles against SMS Central invoice within tolerance.

## Out of scope (named, not forgotten)

- RCS / Conversation API driver — [roadmap](roadmap.md) Phase 3.
- MMS/TTS on Engage v1 — schema fields exist (`format`), driver work deferred until a customer
  wants it.
- Customer self-service portal — S7 admin is operator-facing only.
