# Legacy SMSDB findings — mined 2026-07-27

**Created:** 2026-07-27
**Modified:** 2026-07-27
**Source:** `SMSDB_copy_20260727.bak` (COPY_ONLY off the live SQL Express box, restored into a
local throwaway container). No message bodies, recipient numbers, or credential values were
extracted — flags/aggregates only. Re-mine any time: restore the bak and re-run the queries.

## Scale (what v2 must comfortably carry)

| Fact | Value |
|---|---|
| Outbound messages | 841,931 rows kept (2023-07 → today; ~3-year retention on the box) |
| Last 12 months | ~320,431 sends · ~92% `D`elivered · 4.0% `F`ailed · `E` 1,338 · `I` 439 |
| **Multipart** | **only 9.6% single-part** — 2 parts 32.6%, 3 parts 35.2%, 4 parts 22.6% (≈2.7 parts/send avg → ~870k parts/yr). Parts = billing; the ledger MUST count parts, not messages |
| Delivery receipts | 817k rows; `SMSMessage.DeliveredDT` is **unused (0 rows)** — the status letter + `SMSDeliveryStatus` rows are the only delivery evidence |
| Inbound | tiny: 9,687 rows over 5 years; last 24m almost all on ONE dedicated number channel (`0418726844`, 2,252 msgs) + 26 unrouted rows on ChannelID 0 (the quarantine lane — design §5.2's shape is right) |
| Upstream | 100% `SMSCentral` provider for 12+ months (SMSWholesale is history) |

## Channels (67 rows) — the migration inventory

- **Every legacy channel (IDs 1–53) is NO-KEY**: auth is IP-allowlist only (`SMSValidIP`,
  739 rows). The newer `CUS*` (54–72) and AI channels all have API keys.
- **Active in the last 12 months** (the real migration set — everything else is dead):

| ChannelID | ExternalID | Client | 12m sends | Keys |
|---|---|---|---|---|
| 2 | `info@connectnow.com.au` | CN | 195,106 | **NO-KEY** |
| 47 | `0418726844` (CN Reply — the inbound number) | CN | 63,039 | **NO-KEY** |
| 7 | `harcourts@connectnow.com.au` | CN | 18,227 | **NO-KEY** |
| 56 | `CusProdUnitywater` | CUS | 16,570 | has-key |
| 57 | `CusProdHunterWater` | CUS | 11,653 | has-key |
| 52 | `info@ljhookerassist.com.au` | CN | 9,936 | **NO-KEY** |
| 8 | `randw@connectnow.com.au` | CN | 2,682 | **NO-KEY** |
| 61/62 | `CusProdRedEnergy` / `CusProdLumoEnergy` | CUS | 1,069 / 507 | has-key |
| 46 | `0417678492` (CN Reply Test) | CN | 441 | **NO-KEY** |
| 58/68/72 | Stanwell / MyAlert / Horizon prod | CUS | ≤121 each | has-key |
| 63/64 | `AIDemoX` / `AIWorkforceDevX` (AI-Workforce) | SL | 21 / 22 | has-key |
| + dev/test | `Test`(1), `CusDev*` | | small | mixed |

- **Key-issuance sweep (S5 prerequisite, from the API-keys-only ruling): channels 2, 7, 8,
  46, 47, 52 (+ `Test` 1)** — six live Connectnow-family channels authenticate by IP alone
  today and need keys issued before their repoint.
- The trailing-`X` ExternalID convention does NOT mean disabled — `AIDemoX`/`AIWorkforceDevX`
  send through those exact strings (the ExternalID is just the opaque channel key callers
  pass; several are email addresses).
- ⚠ AI-Workforce note: its configured channel value is the exact string `AIDemoX` (DB fact;
  update any doc/memory that says `AIDemo`).

## Reference (ExtRef) behaviour — design corrections

- Legacy callers **reuse refs heavily**: 12-month duplicate counts — 59,687 on channel 2,
  13,543 on 47, 5,386 on 7. Max observed length 9 chars, mostly numeric, never empty.
- ⇒ **S5 legacy emulation must ACCEPT duplicate refs** (the Delphi gateway always did);
  ref-uniqueness (409) applies to the NEW `/api/v1/messages` surface only. The upstream
  513-duplicate rule never bites either way because the upstream REFERENCE is v2's message
  uuid, not the caller's ref — same trick the Delphi gateway used (it passed its SMSID).
- New-style customers (`Cus*`) already use short numeric refs with near-zero duplication.

## Status letters (legacy alphabet, confirmed meanings)

`N` new/queued · `S` sent (carrier-accepted, receipt pending) · `D` delivered · `F` failed ·
`I` invalid = upstream submit-reject (e.g. `525 Recipient address not valid`) ·
`E` error (no notes recorded) · rare NULL. 12m distribution: D 295,637 · F 12,844 · S 10,170 ·
E 1,338 · I 439.

## Implications folded back into the build

1. Billing ledger counts **parts** (S2) — at ~2.7 parts/send this is the dominant billing
   dimension; reconcile monthly against the SMS Central invoice (S7 gate).
2. Inbound volume is trivial today (≈3/day) and concentrated on one dedicated number —
   the AI-Workforce dedicated-number recommendation (design §10 Q2) matches how CN Reply
   already works; the webhook egress needs no volume engineering.
3. Import script (S5): bring `SMSChannel` rows for the ACTIVE set only (table above);
   62 legacy channels are dead — archive, don't migrate.
4. Delivery evidence maps from status letters + `SMSDeliveryStatus`, never `DeliveredDT`.
5. Ref-uniqueness enforcement is new-API-only (above).
