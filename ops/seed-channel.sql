-- Admin-less channel seeding (S2 surface; admin CRUD arrives at S7).
-- Run against the sologicalsms database after filling the placeholders.
--
-- API keys: generate a high-entropy random string and store ONLY its SHA-256 (lowercase
-- hex) here — the plaintext key goes to the customer, never to this database. PowerShell:
--   $key  = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
--   $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLower()
--
-- Sender IDs: ONE CHANNEL PER SENDER ID (Ray, 2026-07-27) — multiple sender IDs mean
-- multiple channels with their own keys. The originator must ALSO be whitelisted in
-- allowed_originators (ACMA sender-ID compliance) or every send rejects invalid_originator;
-- alphanumeric IDs only after ACMA-register listing.

INSERT INTO customers (code, name, status, created_at)
VALUES ('aiworkforce', 'AI-Workforce', 'active', now())
ON CONFLICT (code) DO NOTHING;

INSERT INTO channels (customer_id, key, description, originator, api_key_1_hash, upstream, status, created_at)
SELECT c.id, 'AIWorkforce', 'AI-Workforce comms capability', '<ORIGINATOR>', '<SHA256-HEX-OF-API-KEY>', 'smscentral', 'active', now()
FROM customers c
WHERE c.code = 'aiworkforce'
ON CONFLICT (key) DO NOTHING;

INSERT INTO allowed_originators (originator, description, created_at)
VALUES ('<ORIGINATOR>', 'AI-Workforce sender (Ray-whitelisted)', now())
ON CONFLICT (originator) DO NOTHING;
