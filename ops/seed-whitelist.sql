-- The ACMA-registered sender-ID whitelist (Ray, 2026-07-27) — all listed "Ready to use"
-- on the ACMA SMS Sender ID Register. ACMA registration matching is case-INSENSITIVE
-- ('ABC' = 'abc'), but handsets display the literal string sent, so v3 stores the
-- register's display casing and the dispatch guard matches EXACTLY — channels must be
-- seeded with these exact spellings (one channel per sender ID).
-- Idempotent: safe to re-run; run against sologicalsms (local) or the Azure DB later.

INSERT INTO allowed_originators (originator, description, created_at) VALUES
  ('AIWorkforce', 'SOLOGICAL PTY LTD', now()),
  ('connectnow',  'CONNECT NOW PTY LTD', now()),
  ('CUSAlert',    'COMPUTERSHARE TECHNOLOGY SERVICES PTY LTD', now()),
  ('CUSMyAcc',    'COMPUTERSHARE TECHNOLOGY SERVICES PTY LTD', now()),
  ('HarcourtsCn', 'HARCOURTS GROUP (AUSTRALIA) PTY LTD', now()),
  ('Horizon',     'Regional Power Corporation', now()),
  ('HunterWater', 'HUNTER WATER CORPORATION', now()),
  ('LJH Assist',  'LJ HOOKER CORPORATION PTY LIMITED', now()),
  ('LumoEnergy',  'LUMO ENERGY AUSTRALIA PTY LTD', now()),
  ('R&W Connect', 'RELIANCE REPORTING GROUP PTY LTD', now()),
  ('RedEnergy',   'RED ENERGY PTY. LIMITED', now()),
  ('SoLogical',   'SOLOGICAL PTY LTD', now()),
  ('Stanwell',    'STANWELL CORPORATION LIMITED', now()),
  ('Unitywater',  'Northern SEQ Distributor - Retailer Authority', now())
ON CONFLICT (originator) DO NOTHING;
