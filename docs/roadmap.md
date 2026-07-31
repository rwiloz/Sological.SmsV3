# Roadmap — SMS Central today → Sinch cutover → RCS

**Created:** 2026-07-27
**Modified:** 2026-07-27
**Status:** AGREED direction (Ray, 2026-07-27 sitting); dates indicative except the hard one:
SMS Central discontinues → Sinch migration must complete **early 2027**.

## Phase 0 — Stand up v3 with SMS Central (now)

Slices S1–S4 in the [implementation plan](implementation-plan.md). Outcome: AI-Workforce
(customer #1) sends through v3 and receives **inbound + delivery webhooks** — the capability
gap that triggered this project. Upstream = SMS Central **sub-account** (isolated callback
URLs; the old gateway's account untouched). The old Delphi gateway keeps serving existing
customers unchanged.

## Phase 1 — Legacy emulation + customer migration (any time; before cutover)

Slice S5: byte-compatible `isapi/submitsms.dll/sendsms|checkstatus|getsms` surface, then
repoint `sms.sological.com.au` (and the DR name) at v3. Existing customers notice nothing;
their traffic now flows v3 → SMS Central. Per-customer channels, API keys and billing ledger
rows exist from day one, so segregation/billing continuity is automatic (v3 auth is API keys
only — IP-only legacy channels get keys issued before their repoint, see plan S5).
The Delphi box and the SLSmsApiV2 replication stack retire at the end of this phase.

## Phase 2 — Sinch Engage v1 driver + cutover (before early 2027)

**Evidence 2026-07-27 (S3 setup):** the NEW sub-account's portal shows "API base URL
https://au.app.api.sinch.com/" (legacy alias api.messagemedia.com still honored) — the
sub-account is natively a Sinch MessageMedia platform account exposing the Engage v1 REST
surface alongside the wrapper API. Consequences: the S6 driver can be built and
contract-tested against THIS account (the credited-Sync-test-account prerequisite likely
falls away), and cutover for v3's own traffic may reduce to a per-channel driver flip on
the SAME account (same numbers/sender IDs/webhooks) — the "no parallel run" constraint
belonged to the legacy MAIN account's migration. Staging when convenient: mint REST API
key+secret in the portal → `SologicalSms--Sinch--ApiKey|ApiSecret` in the secret store.

Slice S6: the second upstream driver against **Engage v1 on the AU instance**
(`au.app.api.sinch.com`) — near-free after the SMS Central driver (same concepts:
`metadata`≈`REFERENCE`, webhooks + poll+confirm catch-up, statuses map 1:1 onto v3's message
states). Verified empirically against the **test Sync account** (credit it first): confirm
which APIs the account exposes, run send/DLR/reply round-trips, confirm dedicated-number /
sender-address arrangements. Cutover = flip the per-channel (default global) upstream config;
since Sync cannot run parallel with SMS Central, plan a cutover window, not a gradual drift.
Contract tests for both drivers derive from the same normalized fixtures so the flip is
evidence-backed, not faith-backed.

## Phase 3 — RCS (the emerging channel)

Facts (2026-07): RCS send is NOT on Engage v1 (`format` = SMS|TTS|MMS). Sinch puts rich
messaging on the **Conversation API** — SMS/RCS/MMS/WhatsApp in one contract with
channel-priority fallback (RCS→SMS built in) — but it is hosted **US/EU/BR only, no AU
region**, while Engage v1 has an AU instance. Sinch's reporting API is already RCS-aware
(RCS billing categories dated Feb-2026), so the platform carries RCS today; only the surface
and residency questions remain.

**New evidence (2026-07-27, S3 setup):** the SMS Central sub-account's webhook config
offers a "receive RCS message" event, and current Sinch MessageMedia docs productize RCS
send/receive/reply with SMS/MMS smart fallback on the MessageMedia platform surface
(campaigns + third-party integration sending) — i.e. RCS may be reachable on THIS
account's platform without the Conversation API and its residency problem. Still gated on
an RCS agent registration either way; the webhook event never fires without one.

Plan:
1. **Ask Sinch three questions** when the account conversation happens: (a) can RCS
   send/receive be enabled on the SMS Central / MessageMedia account surface for AU (and
   what's the agent registration path)? (b) is RCS send on the AU instance otherwise
   Conversation-API-only? (c) can Conversation API share the Engage sender numbers/agent?
2. **RCS agent onboarding has lead time** (brand verification with the carriers/Google) —
   start the registration when the first customer wants RCS, not when the build is ready.
3. Build the Conversation API driver as a **third driver behind the same seam**, scoped to
   rich formats. Two fallback topologies, chosen per residency ruling:
   - *Platform fallback*: Conversation API does RCS→SMS itself (simplest; all traffic rides
     its region), or
   - *Service fallback*: v3 tries RCS via Conversation API, falls back to SMS via Engage AU
     (keeps plain SMS in-region; our message store already models per-message driver choice).
4. Customer-facing: the v3 send API gains `format` (default `sms`) + rich-content fields; the
   webhook egress gains rich payload types. Existing SMS-only customers are untouched.

## Phase 4 — beyond (not planned, named)

WhatsApp (same Conversation API driver family), MMS via Engage v1, TTS. The AI-Workforce side
has its own standing ruling (WhatsApp via Telnyx, RCS→SMS fallback chain, same registry,
different pipelines) — v3's job is to expose channels honestly, not to decide AI-Workforce's
channel strategy.

## Standing risks

- **Sinch driver tested only against the test account, not the migrated production account** —
  mitigate with contract tests + a smoke suite that runs at cutover.
- **Sub-account callback independence** (Phase 0 gate): if SMS Central sub-accounts can't have
  their own forward URLs, Phase 0 needs a shared-receiver discriminator instead (route by
  RECIPIENT/REFERENCE) — design §5 covers both.
- **Residency**: if AU residency is ruled mandatory for RCS traffic too, RCS waits on Sinch
  shipping an AU Conversation region — flag to customers before promising RCS dates.
