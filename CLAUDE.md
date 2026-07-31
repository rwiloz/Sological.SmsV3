# Sological SMS v3 — agent bootstrap

All orientation lives in tool-neutral repo docs — **start with
[docs/engineering-guide.md](docs/engineering-guide.md)** (reference-repo map, coding
standards, secrets/KV-emulator convention, working rules), then the design docs it links
(feasibility → roadmap → implementation-plan → design-sms-central).

Two rules worth repeating before you touch anything:

- **Live traffic**: the Sological SMS channels carry real production SMS for real customers;
  credentials in the reference repos are live. Test sends only to Ray's test number, only
  through the AI-Workforce sub-account channel.
- **No push without Ray's explicit ask** (push = deploy once infra exists). Commit to `main`.
