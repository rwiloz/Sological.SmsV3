# Implementation plan — slices S1–S7

**Created:** 2026-07-27
**Modified:** 2026-07-27
**Status:** S1–S4 designed ([design-sms-central.md](design-sms-central.md)); S5–S7 planned.

Rules of the road: each slice lands complete and verified (suite green + the slice's named
gate) before the next starts; open work carries an explicit OPEN marker here. Surfaces are
listed per slice — scope-narrowing is Ray's call.

## S1 — Skeleton + schema

.NET 10 solution: `Sological.Sms.Service` (ASP.NET minimal API host), `Sological.Sms.Core`
(domain), `Sological.Sms.Tests`. EF Core + Npgsql; own database `sologicalsms` on the existing
Azure PSQL server (local dev: local PostgreSQL). Migrations run on service start (same pattern
as AI-Workforce System). Health endpoint. Dockerfile + container-app-ready config
(Key Vault-backed secrets in Azure; the shared KV **emulator** locally — design §8).

Schema (design §3): `customers`, `channels`, `messages`, `delivery_events`,
`inbound_messages`, `inbound_parts`, `billing_ledger`, `webhook_outbox`.

- **Surfaces:** `GET /health`. No customer surface yet.
- **Gate:** migrations apply clean on empty DB; CI (GitHub Actions) builds + tests green.

## S2 — Send lane (SMS Central driver)

Customer send API (`POST /api/v1/messages`, per-channel API key auth) → message row →
dispatch worker: **pre-dispatch guards first** (duplicate detection + local recipient
validation — design §6.1a, terminal + unbilled) → `ISmsUpstream` seam → **SmsCentralUpstream**
(POST `wrapper/sms`, `REFERENCE` = our message id) → status transitions + billing ledger row
(parts counted by GSM7/UCS-2 rules). Send status query API.

- **Surfaces:** `POST /api/v1/messages` · `GET /api/v1/messages/{id}` · admin-less channel
  seeding via config/SQL for now (admin CRUD is S7).
- **Gate:** live smoke — one real SMS to Ray's number through the sub-account, message row
  reaches `sent`, ledger row correct. ⚠ Prereq on Ray: SMS Central **sub-account created**
  (creds + originator/number decision for AI-Workforce).

## S3 — Upstream ingress (DLR + inbound receivers)

The two GET receivers SMS Central pushes to (design §5): delivery (`RESULT`/`STATUS` →
normalized event → message state + `delivery_events` row) and inbound (multipart reassembly
via `UDH`/`BINARY`/DCS, reply correlation via `REFERENCE`, channel routing via `RECIPIENT`
then referenced message). Public HTTPS hostname for the receivers.

- **Surfaces:** `GET /ingress/smscentral/delivery` · `GET /ingress/smscentral/inbound`
  (creds-validated, `200`+`0` ack contract).
- **Gate:** live smoke — send → DLR arrives → message `delivered`; reply from Ray's phone →
  `inbound_messages` row correlated to the sent message. Multipart inbound (>160 chars)
  reassembles. ⚠ Prereq on Ray: sub-account forward URLs set to the receivers.

## S4 — Customer webhook egress + AI-Workforce wiring

Webhook outbox worker: per-channel `webhook_url` + secret; `sms.inbound` + `sms.delivery`
JSON POSTs, HMAC-signed, retry with backoff + dead-letter marking. Poll-parity endpoint for
debugging. Then the AI-Workforce side (in the AI-Workforce repo, its own commit set): point
`SologicalSmsProvider` at v2's send API, align `SmsWebhookController` to the v2 payloads,
delete the `smsdr` misread, upgrade `InboundSmsHandler` correlation to use `replyTo` when
present (window heuristic stays as fallback).

- **Surfaces:** outbound webhooks (contract in design §6) · `GET /api/v1/inbound?since=` ·
  AI-Workforce: `Sms:*` config repoint + webhook payload alignment.
- **Gate:** end-to-end through AI-Workforce comms — case SMS send → delivery signal lands on
  the case; customer reply → case-targeted inbox signal via exact `replyTo` correlation.
  **This closes the capability gap that started the project.**

## S5 — Legacy emulation (before customer migration; not gating S6 design)

Byte-compatible `isapi/submitsms.dll/sendsms`, `checkstatus`, `getsms` routes mapped onto the
v2 store (status letters N/S/D/F/E/I; `getsms` consume-once semantics preserved; tab-separated
response shapes exact). Channel = legacy `ExternalID`; ApiKey1/2 honored. **No IP allowlisting
in v2 (ruled 2026-07-27)** — `SMSValidIP` is not carried, so any legacy channel that
authenticates by IP alone today (the Delphi code only checks ApiKey when one is set) MUST be
issued API keys before its repoint. That key-issuance sweep is a migration prerequisite on the
customer checklist, not code — **the sweep list is known: channels 2, 7, 8, 46, 47, 52 (+ Test)**,
per [legacy-db-findings](legacy-db-findings.md). Import script: ACTIVE `SMSChannel` rows only
(the findings table; 62 dead channels archive, don't migrate). Legacy emulation ACCEPTS
duplicate refs (legacy callers reuse them heavily) — ref-uniqueness is new-API-only.

- **Surfaces:** the three legacy routes on the legacy hostnames (`sms.sological.com.au`,
  `smsdr.sological.com.au` both → v2).
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
