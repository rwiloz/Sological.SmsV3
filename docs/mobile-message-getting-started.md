# Mobile Message upstream — getting started

**Created:** 2026-11-11
**Modified:** 2026-11-11

A starting brief, not a design. Ray wants v3 to send through **Mobile Message (MM)** instead of
SMS Central. Read this, then work out the design with Ray and record it in the usual docs.

## Why

The legacy gateway switched to MM as its primary provider on 2026-11-11 (repo `C:\Code\Sological.Sms`).
SMS Central is expected to be dropped soon. MM is an Australian provider with a REST/JSON API, so it may
replace the planned Sinch driver (S6). Confirm with Ray whether Sinch is still needed before
touching the roadmap.

## Read first

- [mobile-message-api.md](mobile-message-api.md) — MM's API reference (copied from the
  legacy repo). Key sections: Send SMS Messages, Message History, Inbound SMS, Webhooks
  (payloads, retries, signing), Testing Your Integration (the sandbox account).
- MM's OpenAPI spec: https://mobilemessage.com.au/assets/openapi.json. It has exact schemas,
  including the `error` field on each send result and the webhook payloads.
- Legacy repo, for what was done there and what went wrong (read-only, as with the other
  reference repos):
  - `Docs/MobileMessage-Design.md` — the legacy design, decisions and cutover approach.
  - `SmsProcessor/Providers/MobileMessageProvider.cs` — send and result mapping.
  - `ServiceSms/Models/MobileMessageWebhooks.cs` — delivery and inbound webhooks, signature check.

## Where it plugs in here

The seams are already shaped for a second provider:

- `Core/Upstream/ISmsUpstream.cs` — add a `MobileMessageUpstream` beside `SmsCentralUpstream`.
- `UpstreamProvider` enum and `Channel.Upstream` / `Message.Upstream` — add a value (needs a
  migration). The dispatch worker currently injects one `ISmsUpstream`; selecting the provider
  per channel is new work.
- `Ingress/` — new MM receivers beside the SMS Central ones, translating into the existing
  `DeliveryVerdict` and inbound flow. MM pushes **JSON POSTs**, not query strings.
- `UpstreamBreakerService` counts unmatched receipts per upstream. MM receipts carry your
  `custom_ref`, so matching should be exact (no content-match fallback needed).
- Secrets follow the existing convention: e.g. `SologicalSms:MobileMessage:User|Password`
  and a webhook signing secret.

## MM facts that matter

- **Send:** `POST https://api.mobilemessage.com.au/v1/messages`, Basic auth, JSON.
  - Set `custom_ref` to the message id, so receipts and replies match back to it.
  - Send an `Idempotency-Key` header (kept 24 h) so retries are safe.
  - Each result is `success`, `error` or `blocked` (unsubscribed), with an `error` text field.
    HTTP 401/403/429/5xx are account-wide problems.
- **Australian mobiles only.** MM rejects anything else.
- **Senders:** Ray manages sender IDs; MM is setting his account up as an originating telco.
  Replies need an MM dedicated number.
- **Delivery receipts:** one webhook per message part (`part_number`/`total_parts`), status
  `delivered` or `failed`. The legacy repo maps `failed` to a terminal failure.
- **Inbound:** carries `original_custom_ref` and `original_message_id` of the message being
  replied to. `type` can be `unsubscribe`.
- **Webhook delivery:** MM needs a 2xx within 5 s, otherwise it retries (up to 10 times over
  about 4 h, not in order). Handlers must be idempotent. Optional HMAC signing uses
  `X-MM-Timestamp` and `X-MM-Signature`; the doc has a test vector.
- **Unicode:** off by default, so non-GSM characters are stripped. The `GsmFold` send lane
  here already handles this.
- **Sandbox account:** separate API keys and webhooks, no real sends, simulated receipts
  about 5 s after a send. Numbers ending in `000` fail, and `POST /v1/test-inbound`
  simulates a reply. This is the natural contract-test target.

## Lessons from the legacy switch

- **The webhook URL is per account.** MM has one status URL and one inbound URL. Don't point
  the sandbox's webhooks at a production endpoint, or sandbox receipts will update real rows.
- **The first webhook can time out on a cold start.** MM's retry covers it.
- Moving channels **one at a time** made cutover and rollback simple.

## Standing rules still apply

Never call the real MM API from tests, and never run live sends without Ray's approval.
Automated tests use the fake upstream or a stub handler. Test sends go only to Ray's test
number. Commit to `main`, and never push without Ray's ask.

## Questions settled with Ray (2026-11-11) — built the same day

1. **MM replaces S6 (Sinch)** — S6 is dropped.
2. **Globally**: `SologicalSms:Upstream:Provider` (default `MobileMessage`), one driver for every channel.
3. **The one live account** (it is tied to Ray's ACMA originating-telco registration; MM has no
   sub-accounts). No sandbox: dev needs real MFA and one-time-code sends. Its two webhook URLs
   point at the legacy app, whose router forwards by `custom_ref` tag — `dev` (Azure) and `local`
   (Ray's machine, `sms-yoga.sological.io`). A key pair per environment.
4. **`blocked` → `rejected` with error code `unsubscribed`**, final.

The build is recorded in the implementation plan (Mobile Message section) and design §4.2/§5.3.
