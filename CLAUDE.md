# Sological SMS v2 — agent orientation

Standalone rewrite of Ray's SMS gateway. **Read `docs/` before doing anything**:
[feasibility](docs/feasibility.md) (decision + upstream API facts) →
[roadmap](docs/roadmap.md) (SMS Central → Sinch cutover → RCS) →
[implementation-plan](docs/implementation-plan.md) (slices S1–S7, gates, surfaces) →
[design-sms-central](docs/design-sms-central.md) (S1–S4 design). Plans/progress live in
these docs — update them as slices land; tick items visibly, mark open work OPEN.

## Reference material (read-only — never modify these repos)

| Where | What it is | Why you'd read it |
|---|---|---|
| `C:\Code\DelphiSms` | The LIVE 15-year-old Delphi gateway (ISAPI `submitsms.dll`) | `uSubmitSMS.pas` = every legacy client action (sendsms/checkstatus/getsms) **and** the SMS Central DLR/inbound callback handling incl. real captured payloads in comments; `uDMExetelSMS.pas` = processor internals; `SMSWork.sql` = schema archaeology. S5 legacy emulation must match this byte-for-byte. |
| `C:\Code\Sological.Sms` | C# management/replication stack for the gateway DB | `SmsRepository/*.cs` = the legacy table shapes (SMSMessage/SMSIn/SMSDeliveryStatus/SMSChannel/SMSValidIP); `ServiceSms/Models/ModelReplicate.cs` = DR replication; **`Docs/API-Reference-January2013.pdf` = the SMS Central API reference** (send/DLR/inbound/read/balance + response codes). |
| `C:\Code\New.Sms\openapi.json` | Sinch Engage 2.1.0 OpenAPI spec ("Sync") | The S6 driver's contract: `/v1/messages`, `/v1/delivery_reports`+confirm, `/v1/replies`+confirm, `/v1/webhooks/messages`, signature keys. AU instance: `au.app.api.sinch.com`. |
| `C:\Code\AI-Workforce` | Customer #1 (its comms capability consumes our webhooks) | The S4 contract counterpart: `backend/03.Services/AIWorkforce.Services.System/Features/Sms/SmsWebhookController.cs`, `EventHandlers/InboundSmsHandler.cs`, `Features/Comms/CommsDeliveryStatusHandler.cs`, `backend/02.Infrastructure/.../Services/Sms/SologicalSmsProvider.cs`. AI-Workforce-side changes are made in THAT repo, never from here. |

## Live-traffic warning

The Sological SMS channels carry **real production SMS for real customers**. Any credentials
found in the reference repos or the gateway DB are live. Test sends go ONLY to Ray's own test
number (ask, don't guess), only through the AI-Workforce sub-account channel once it exists.
Never point this service's ingress/config at the production SMS Central account.

## Coding standards

Ray's authoritative standards live in `C:\Code\AI-Workforce\docs_memory\coding-standards.md`
(+ `testing-standards.md`, `documentation-standards.md`). Much of that file is
AI-Workforce-platform-specific (SecureApiController, tenant JWT plane, Cortex actor rules,
YARP, seed ratchet) — **does not apply here**. What DOES apply in this repo:

- **Error envelope**: every API error returns `{ error, message }` (short code + human text).
  Never plain-string errors, never `ex.Message`/stack traces to clients.
- **Type safety**: nullable enabled everywhere; no `dynamic`; config via `IOptions<T>` (no
  `Configuration["magic:string"]` outside Program wiring); DTOs are `record`s with `required`;
  jsonb columns map to typed objects (raw-string jsonb needs a documented ruling).
- **Auth here is per-channel API keys** (hashed, two live for rotation) + optional IP
  allowlist + HMAC-signed outbound webhooks — there is no user JWT plane. Endpoints are
  secure-by-default: anything not a customer surface or a provider ingress route requires
  explicit justification for being reachable.
- **Background workers**: inherit `BackgroundService`, scope per iteration via
  `IServiceScopeFactory`, `Task.Delay(interval, stoppingToken)`, catch `OperationCanceledException`
  separately, log-never-swallow. Workers claim rows (claim columns), safe for scale-out.
- **EF migrations**: any entity/index change ships its migration in the same commit;
  service runs `Database.Migrate()` on start; review generated migrations before committing.
- **Logging**: Serilog, structured properties (never string-interpolate phone numbers/bodies
  into templates); mask phone numbers in log output (`***last4`); message bodies at Debug only.
- **Tests**: no fixed-delay-then-assert — poll with a ceiling; integration tests run locally,
  not in hosted CI (Ray's standing rule); two consecutive failures = real bug, not flake.
- **Docs**: `**Created:**`/`**Modified:**` on lines 3–4, ISO dates, no dates in filenames;
  Mermaid for diagrams (never ASCII art).
- **Honesty rules** (carried from the comms build): statuses are never optimistic; drops/skips
  on correctness paths log loudly, never metric-only; no silent fallbacks — one canonical
  standard, migrate all sides, never alias/dual-read shims.

## Secrets & config

- **Local dev secret home = the Azure Key Vault emulator** (shared with AI-Workforce/Billing):
  container `aiworkforce-keyvault-emulator` on `https://localhost:4997`, data in
  `C:\AIWorkforce\KeyVaultEmulator` (SQLite). Config pipeline: load KV whenever
  `AzureKeyVault:VaultUri` is set, emulator-aware (non-`vault.azure.net` host ⇒ emulator token
  credential + `DisableChallengeResourceVerification`, non-fatal load) — copy the pattern from
  Billing `Program.cs` or AI-Workforce commit `cbb2994d3`. Vault loads AFTER env vars (vault
  wins on overlap); **hermetic test harnesses must blank `AzureKeyVault__VaultUri`**.
  Ops scripts to crib: AI-Workforce `setup_keyvault_emulator.ps1` / `seed_keyvault_emulator.ps1`.
- Secret names: `SmsV2:SmsCentral:Username|Password`, `SmsV2:Sinch:ApiKey` (S6),
  `SmsV2:Webhook:{channelKey}`, DB connection. Azure = real Key Vault, same names.
- Channel/customer config is **data** (DB rows), never appsettings. No secrets in tracked
  files, ever — appsettings carry secret NAMES/references only.

## Working rules

- Branch: `main`, commit directly. **No remote push without Ray's explicit ask** (his standing
  rule; push = deploy once infra exists).
- Slice discipline: one slice at a time per the implementation plan; each lands complete
  (suite green + the slice's named gate) or is explicitly parked with Ray's OK.
- Hosting target: Azure Container Apps beside the Billing service + own database
  (`sologicalsms`) on the existing Azure PSQL server. No prod infra exists yet.
