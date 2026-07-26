# Sological SMS v2

The rewrite of the Sological SMS gateway: a standalone .NET service that fronts customer SMS
traffic (send, inbound, delivery receipts), keeps per-customer segregation and billing records,
and talks upstream through a pluggable provider seam — **SMS Central today, Sinch (Sync) at
cutover, Conversation API for RCS later** — so the upstream migration is a config flip, not a
rewrite.

Replaces the 15-year-old Delphi gateway (`C:\Code\DelphiSms` + `C:\Code\Sological.Sms`).
First customer: AI-Workforce (its comms capability consumes the webhook egress).

## Status

- **Phase: planning/design.** No code yet — docs first.
- Docs:
  - [Feasibility](docs/feasibility.md) — the four integration options evaluated, upstream API
    facts (reverse-engineered + verified), and the decision.
  - [Roadmap](docs/roadmap.md) — phases from SMS Central today to Sinch cutover to RCS.
  - [Implementation plan](docs/implementation-plan.md) — slices S1–S7 with gates and surfaces.
  - [Design: S1–S4 (SMS Central first step)](docs/design-sms-central.md) — schema, driver seam,
    ingress/egress contracts, AI-Workforce wiring.

## Operating constraints (standing)

- SMS Central is being discontinued; migration target is the Sinch (Sync/MessageMedia) account.
  The Sync production account **cannot run in parallel** with SMS Central → cutover is a flip.
- Legacy customers keep working: this service must eventually emulate the old
  `isapi/submitsms.dll/*` surface byte-compatibly so `sms.sological.com.au` can repoint.
- Customer segregation + billing records are first-class (append-only ledger), not bolted on.
- Hosting: Azure Container Apps beside the billing service; own PostgreSQL database on the
  existing Azure PSQL server. No production infra exists yet for this service.
