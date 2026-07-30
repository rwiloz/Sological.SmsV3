# Upstream integration findings — SMS Central / Sinch platform (S2–S3)

**Created:** 2026-07-30
**Modified:** 2026-07-30

What we learned integrating the sub-account across S2–S3 (live-gate evidence, 2026-07-27 →
2026-07-30). Complements [design-sms-central](design-sms-central.md) (the design) and the
[implementation plan](implementation-plan.md) (slice status/gates); this is the "why it
looked broken and what turned out to be true" record.

## Architecture answer (asked 2026-07-30): ONE service, drivers not services

v2 is **one service, one database** (`Sological.Sms.Service` + `sologicalsms`). SMS Central
vs Sinch is NOT two services — upstream providers are **drivers behind the `ISmsUpstream`
seam inside the same service**, selected per channel (`channels.upstream`:
`smscentral` today, `sinch` at S6). One deployment carries both during cutover; the flip is
per-channel config, and rollback is flipping back.

Deeper finding: they aren't even two *platforms*. The SMS Central sub-account **is a Sinch
MessageMedia platform account** with two API surfaces on the same account:

| Surface | URL | Creds class | Used for |
|---|---|---|---|
| Legacy "wrapper" | `my.smscentral.com.au/wrapper/sms` | account username/password (`sological2`) | v2 sends TODAY (S2+, proven live) |
| Engage v1 REST | `au.app.api.sinch.com` (alias `api.messagemedia.com`) | separate revocable API key+secret (Basic/HMAC) — self-service in the Hub, NOT yet issued for the sub-account | S6 send driver + webhook management; docs: https://docs.app.api.sinch.com/ |

The webhook push engine serving our S3 receivers is the platform's (Engage) engine — it
serves BOTH surfaces. "Cutover" for v2's own traffic is therefore a driver flip on the same
account, not an account migration. The separate "new Sinch (starter) account" is a sandbox:
REST keys work there (`SologicalSms--SinchTest--*`), no credit/sender IDs/sub-accounts; used
to validate auth + webhook definitions; later the S6 rehearsal rig.

## The issue log (chronological, each with the resolution)

1. **Webhook pushes arrived EMPTY (S3 gate, 2026-07-27→30).** Transport was perfect from the
   first attempt (their engine POSTs within seconds; retries on non-ack) but bodies were
   zero bytes. Cause: portal-created webhooks had **no payload template**. Fix: the portal's
   webhook UI has a **template-parameter picker** (Ray found it) — payload fields are
   chosen/named there. Lesson hardened into code: receivers never reduce a push to `{}` —
   unparseable/empty bodies persist `_raw`/`_contentType` in the payload jsonb.
2. **REST API 401s with the account credentials.** The portal labels `sological2` as "API
   credentials", but that pair is **legacy-class** (wrapper/SOAP lineage). The REST surface
   only accepts its own **API key+secret** class (`WWW-Authenticate: Basic realm="service"`,
   right scheme, wrong class). Verified locally byte-for-byte before concluding. Sub-account
   REST keys still pending with the account manager — NOT blocking anything since the portal
   picker solved the webhook templates.
3. **`$metadata` does NOT round-trip the wrapper REFERENCE.** The delivery/inbound template
   field `reference > $metadata.get('REFERENCE')` came back as the *unresolved Velocity
   literal* — wrapper sends put nothing in platform metadata. The designed REFERENCE
   round-trip only exists on the LEGACY forward format (per the Delphi-era captures) and on
   S6 REST sends (`metadata` is ours to set).
4. **The correlation chain that actually works (proven live).** Modern pushes carry
   **`mtId`** (platform message id) on BOTH delivery reports and replies:
   `REFERENCE uuid (legacy format) → upstream_id == mtId → unique content-match
   (mtContent + handset, 72h, ambiguity = audit-only + loud)`. The first receipt
   content-matches and backfills `messages.upstream_id = mtId`; later receipts and replies
   then match mtId directly — giving **exact reply→send correlation even on alpha-sender
   channels**. S6 REST sends return mtId at submit, deleting the content-match rung.
5. **Receiver auth**: pushes carry no creds (2018 captures + live confirmation). Ruling: no
   creds in URLs — the **`SLVERIFY` header** (shared key, constant-time compare) is set on
   the webhooks; present+matching on every live push, so the deployment now runs
   `RequireVerification=true` (401 without the header). Creds params, if ever present, are
   redacted before payload storage. Upgrade path at S6: their HMAC signature keys.
6. **Payload dialects.** One receiver pair serves two wire formats: the legacy GET/query
   format (REFERENCE/ID/ORIGINATOR/MESSAGE_TEXT/UDH+BINARY — kept for S5 and as fallback)
   and the portal-picker JSON (dtId/mtId/status/statusCode/sourceAddress/destinationAddress/
   moContent…), resolved via case-insensitive alias lookup. Multipart inbound arrives
   **platform-preassembled** on the modern format (476-char reply = one push); the UDH
   parts buffer remains for the legacy format.
7. **RCS**: the platform surfaces RCS receive events (portal webhook picker lists `MO RCS`)
   and productizes RCS send with SMS fallback — possibly no Conversation API (and its
   US/EU/BR-only residency) needed. Parked in [roadmap](roadmap.md) Phase 3; needs an RCS
   agent registration either way.

## Cleanliness / revisit-at-S6 list (known, deliberate)

- The ingress routes are named `/ingress/smscentral/*` and `delivery_events.provider` is
  `'smscentral'`, but the modern pushes really come from the platform (Engage) webhook
  engine. Naming is honest enough for now (it's the SMS Central account's traffic);
  revisit when S6 adds its receiver — they may turn out to be the same endpoint.
- The content-match rung (finding 4) is wrapper-era scaffolding; remove after S6 flips
  sends to REST.
- Two payload dialects in one receiver is alias-table complexity, tested but worth a
  tidy-up once the legacy format is S5-emulation-only.
- Per-channel AI-Workforce secret naming for future dev channels: prefer
  `Sms--Sological--{channel}--ApiKey` (decided at mint time; see plan S4 note).

## Current state (2026-07-30)

S1 ✅ S2 ✅ S3 ✅ — all gates passed live. One service, 115/115 tests, deployed locally in
Docker behind `sms-yoga.sological.io` (Cloudflare tunnel), strict SLVERIFY. Next: S4
(customer webhook egress + AI-Workforce repoint). Pending externally: sub-account REST
keys (account manager) — needed for S6, not for S4/S5.
