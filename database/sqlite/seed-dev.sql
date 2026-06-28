-- MMProtect License Server — SQLite Beispieldaten (Entwicklung / Demos)
-- Einspielung: sqlite3 <db-datei> < database/sqlite/seed-dev.sql
-- Alle Demo-Schlüssel sind öffentlich bekannt — NIEMALS in Produktion verwenden.

PRAGMA foreign_keys=ON;

-- ── Kunden ───────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO customers (id, customer_uid, external_customer_ref, name, email, notes)
VALUES
  (1, 'cust_4833dbabcd254b84b3106e5d6831f0a9', 'cust-acme',  'Acme GmbH',         'lizenz@acme.de',      'Hauptkunde, ERP + Shop'),
  (2, 'cust_8ce5e5bcaacc4577a326f32a15b4ec3e', 'cust-beta',  'Beta Solutions AG',  'lizenzen@beta.ch',    'Schweizer Partner'),
  (3, 'cust_c1d2e3f4a5b64c7d8e9f0a1b2c3d4e5f', 'cust-gamma', 'Gamma Tech KG',      'info@gamma-tech.de',  'Hostname-Constraint aktiv'),
  (4, 'cust_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a', 'cust-delta', 'Delta Systems OHG',  'delta@example.com',   'Lizenz ausgelaufen (Demo)');

-- ── Projekte ─────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO projects (id, project_uid, project_key, name, php_min_version, description)
VALUES
  (1, 'proj_f4a8d21bd2ed49329de748a429eb1972', 'acme-erp-v1',   'Acme ERP',       '8.4', 'Unternehmensressourcen-Planung, alle Module'),
  (2, 'proj_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d', 'shopsystem-pro', 'ShopSystem Pro', '8.4', 'E-Commerce-Plattform mit Checkout und Lager');

-- ── API-Clients (dynamisch verwaltete Encoder-/Admin-Schlüssel) ───────────────
-- api_key_hash = SHA-256(key) lowercase hex
-- Schlüssel für Tests:
--   CI/CD:      mm_cicd_pipeline_encoder_2026
--   Monitoring: mm_monitoring_readonly_2026   (admin, read-only)
--   Support:    mm_support_admin_demo_2026    (widerrufen)
INSERT OR IGNORE INTO api_clients (id, client_uid, name, api_key_hash, scopes, is_active)
VALUES
  (1, 'client_aabb1122cc3344dd5566ee7788ff9900', 'CI/CD Pipeline Encoder',
      '6a8c2cf197b39d9a52e4144c730ed49203349aecbe088ebdb207191a57c70b84', 'encoder', 1),
  (2, 'client_bbcc2233dd4455ee6677ff8899aa0011', 'Monitoring (read-only)',
      'ef8d22b1c1ecd404355aa6bf69bcf36e23c1b342b480219432a55a66d468e9b7', 'admin',   1),
  (3, 'client_ccdd3344ee5566ff7788aa99bb00cc11', 'Support Admin (Demo, widerrufen)',
      '13412bef15d3a67aec88fb6ab852d0194f741c687488b6e3fbc01ceb24d6f9e4', 'admin',   0);

UPDATE api_clients SET revoked_at = '2026-03-15T10:00:00' WHERE id = 3;

-- ── Kryptoschlüssel ──────────────────────────────────────────────────────────
-- vendor_signing: öffentlicher Verifikationsschlüssel (PEM-Platzhalter)
-- build/runtime: KEK-verschlüsselter AES-256-GCM-Blob (Base64-Platzhalter)
INSERT OR IGNORE INTO crypto_keys
  (id, key_uid, key_type, algorithm, public_key_pem, encrypted_secret_key, is_active)
VALUES
  (1, 'key_vendor_signing_2026',
      'vendor_signing', 'ecdsa-p256',
      '-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXXXXXXXXXXXXXXXXXXXXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
-----END PUBLIC KEY-----',
      NULL, 1),
  (2, 'key_278340bdaf7e412091130e1abbd9882e',
      'build', 'aes-256-gcm',
      NULL,
      'AAAAAAAAAAAAAAAAAAAAAAAABBBBBBBBBBBBBBBBBBBBBBBBCCCCCCCCCCCCCCCCCCCCCCCCDDDDDDDDDDDD==',
      1),
  (3, 'key_9a8b7c6d5e4f4a3b2c1d0e9f8a7b6c5d',
      'build', 'aes-256-gcm',
      NULL,
      'EEEEEEEEEEEEEEEEEEEEEEEEFFFFFFFFFFFFFFFFFFFFFFFFGGGGGGGGGGGGGGGGGGGGGGGGHHHHHHHHHHHHHhh==',
      1);

-- ── Lizenzen ─────────────────────────────────────────────────────────────────
-- features/constraints als JSON-String gespeichert (SQLite TEXT)
INSERT OR IGNORE INTO licenses
  (id, license_uid, customer_id, project_id, license_key,
   valid_from, valid_until, max_activations, features, constraints, status)
VALUES
  -- Acme ERP: aktiv, base+premium, 5 Aktivierungen
  (1, 'lic_da548e9ddfbd4e408368918b3204deb4', 1, 1,
   'LIC-ACME-2026-001',
   '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z', 5,
   '["base","premium"]', NULL, 'active'),

  -- Beta ERP: widerrufen
  (2, 'lic_ab943b1e175d4b749d13c03a85d209a7', 2, 1,
   'LIC-BETA-2026-001',
   '2026-01-01T00:00:00Z', '2026-07-01T00:00:00Z', 2,
   '["base"]', NULL, 'revoked'),

  -- Gamma ERP: Hostname-Constraint, 1 Aktivierung
  (3, 'lic_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f', 3, 1,
   'LIC-GAMMA-2026-001',
   '2026-01-01T00:00:00Z', '2027-01-01T00:00:00Z', 1,
   '["base"]',
   '{"allowedHostnames":["app.gamma-tech.de"]}',
   'active'),

  -- Delta ERP: abgelaufen
  (4, 'lic_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a', 4, 1,
   'LIC-DELTA-2025-001',
   '2025-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 3,
   '["base"]', NULL, 'active'),

  -- Acme ShopSystem: aktiv, base+premium+shop
  (5, 'lic_e5f6a7b8c9d04e1f2a3b4c5d6e7f8a9b', 1, 2,
   'LIC-ACME-SHOP-2026-001',
   '2026-03-01T00:00:00Z', '2027-03-01T00:00:00Z', 3,
   '["base","premium","shop"]', NULL, 'active');

-- ── Builds ───────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO builds
  (id, build_uid, customer_id, project_id, license_id, key_id,
   version, source_revision, encoder_version,
   manifest_hash, manifest_signature, file_count, status, created_at, signed_at)
VALUES
  -- Acme ERP v1.0.0 — signiert
  (1, 'build_1d2967fe74374d9fa1b259207186993e', 1, 1, 1, 2,
   '1.0.0', 'abc1234def5678', '1.0.0',
   'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
   'MEUCIQCmQtFf1B7kH5RmS8C8DF47xzFykBxb9o6PZRIokwEZvQIgXpzBFNtou6bcVu4hKFPzO5GAbMwLzvQXVGCqKJINZGE=',
   6, 'signed', '2026-06-28T06:13:24Z', '2026-06-28T06:13:36Z'),

  -- Acme ShopSystem v2.1.0 — signiert
  (2, 'build_2e3f4a5b6c7d4e8f9a0b1c2d3e4f5a6b', 1, 2, 5, 3,
   '2.1.0', 'feed4321dcba0987', '1.0.0',
   'ba7816bf8f01cfea414140de5dae2ec73b00361bbef0469f490c29f9b0e4ef72',
   'MEUCIQD9aBzKlrj4yVr6b0nnP22vMqC1PLW5L7jHhkQZYH4x4QIgRv1WuYl0qSXr2B4GCzEp6oiLtUMhDoWUK4XJhZmMkA8=',
   12, 'signed', '2026-05-10T14:22:00Z', '2026-05-10T14:25:17Z'),

  -- Delta ERP v0.9.0 — widerrufen
  (3, 'build_3f4a5b6c7d8e4f9a0b1c2d3e4f5a6b7c', 4, 1, 4, 2,
   '0.9.0', 'deadbeef12345678', '0.9.5',
   'ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb',
   'MEQCIBM3XKfaJ0sOwQHb9bQVzwPWgJ1LmNtY6IUY5bZvM9X6AiBpXyUr5Kfc0QZE3m4C2eQTlXLuRvKJPJSjqXtWFLUvIw==',
   4, 'revoked', '2025-11-20T09:00:00Z', '2025-11-20T09:05:00Z');

-- ── Build-Dateien ─────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO build_files
  (build_id, file_uid, relative_path, path_hash, plain_hash, cipher_hash, algorithm, kdf)
VALUES
  -- Build 1: Acme ERP
  (1, 'file_001', 'src/App/Controller/IndexController.php',
   '2c624232cdd221771294dfbb310acbc5b2b0e7c5', 'aabbcc001122', 'ddeeff334455', 'aes-256-gcm', 'hkdf-sha256'),
  (1, 'file_002', 'src/App/Model/UserModel.php',
   '3e23e8160039594a33894f6564e1b1348bbd7a0d', 'bbccdd112233', 'eeff00445566', 'aes-256-gcm', 'hkdf-sha256'),
  (1, 'file_003', 'src/App/Model/OrderModel.php',
   '4e07408562bedb8b60ce05c1decfe3ad16ce0790', 'ccddee223344', 'ff0011556677', 'aes-256-gcm', 'hkdf-sha256'),
  (1, 'file_004', 'src/App/Service/InvoiceService.php',
   '6b86b273ff34fce19d6b804eff5a3f5747ada4ea', 'ddeeff334455', '00112266778', 'aes-256-gcm', 'hkdf-sha256'),
  (1, 'file_005', 'src/App/Service/ReportService.php',
   '4355a46b19d348dc2f57c046f8ef63d4538ebb93', 'eeff00445566', '11223377889', 'aes-256-gcm', 'hkdf-sha256'),
  (1, 'file_006', 'src/Bootstrap.php',
   '53c234e5e8472b6ac51c1ae1cab3fe06fad053be', 'ff0011556677', '223344889900', 'aes-256-gcm', 'hkdf-sha256'),

  -- Build 2: ShopSystem Pro
  (2, 'file_101', 'src/Shop/Cart/CartService.php',
   '1121cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'aabbcc010101', 'ddeeff020202', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_102', 'src/Shop/Cart/CartRepository.php',
   '2222cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'bbccdd020202', 'eeff00030303', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_103', 'src/Shop/Checkout/CheckoutController.php',
   '3333cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ccddee030303', 'ff0011040404', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_104', 'src/Shop/Payment/StripeGateway.php',
   '4444cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ddeeff040404', '00112205050', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_105', 'src/Shop/Inventory/StockManager.php',
   '5555cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'eeff00050505', '11223306060', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_106', 'src/Shop/Catalog/ProductRepository.php',
   '6666cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ff0011060606', '22334407070', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_107', 'src/Shop/Catalog/CategoryRepository.php',
   '7777cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'aabbcc070707', 'ddeeff080808', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_108', 'src/Shop/Email/OrderConfirmation.php',
   '8888cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'bbccdd080808', 'eeff00090909', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_109', 'src/Shop/Email/ShippingNotification.php',
   '9999cfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ccddee090909', 'ff00110a0a0a', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_110', 'src/Shop/Analytics/Dashboard.php',
   'aaaacfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ddeeff0a0a0a', '00112200b0b0', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_111', 'src/Bootstrap.php',
   'bbbbcfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'eeff000b0b0b', '1122330c0c0c', 'aes-256-gcm', 'hkdf-sha256'),
  (2, 'file_112', 'config/routes.php',
   'ccccfd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2000', 'ff00110c0c0c', '2233440d0d0d', 'aes-256-gcm', 'hkdf-sha256'),

  -- Build 3: Delta ERP (widerrufen)
  (3, 'file_201', 'src/App/Controller.php',
   'ddddcfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'aabbcc0d0d0d', 'ddeeff0e0e0e', 'aes-256-gcm', 'hkdf-sha256'),
  (3, 'file_202', 'src/App/Model.php',
   'eeeecfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'bbccdd0e0e0e', 'eeff000f0f0f', 'aes-256-gcm', 'hkdf-sha256'),
  (3, 'file_203', 'src/Core/Database.php',
   'ffffcfccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ccddee0f0f0f', 'ff0011101010', 'aes-256-gcm', 'hkdf-sha256'),
  (3, 'file_204', 'src/Bootstrap.php',
   '00001ccd5913f0a63fec40a6ffd44ea64f9dc135c66634ba001d10bcf4302a2', 'ddeeff101010', '001122111111', 'aes-256-gcm', 'hkdf-sha256');

-- ── Lizenz-Aktivierungen ─────────────────────────────────────────────────────
-- machine_fingerprint = SHA-256(machine-id + hostname), 64 Hex-Zeichen
INSERT OR IGNORE INTO license_activations
  (id, activation_uid, license_id, machine_fingerprint,
   first_seen_at, last_seen_at, status, notes)
VALUES
  -- Acme ERP: Produktionsserver
  (1, 'act_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d', 1,
   'b94d27b9934d3e08a52e52d7da7dabfac484efe04294e576f3b2c7f23a0e3a6a',
   '2026-06-01T08:00:00Z', '2026-06-28T06:13:00Z', 'active', 'prod-server-01 (Berlin)'),

  -- Acme ERP: Staging-Server
  (2, 'act_b2c3d4e5f6a74b8c9d0e1f2a3b4c5d6e', 1,
   'c3ae4f5b2d6e8a1c9f7b3e5d2a4c6f8b0e2d4f6a8c0e2d4f6a8c0e2d4f6a8c0',
   '2026-06-10T10:30:00Z', '2026-06-27T15:00:00Z', 'active', 'staging-server-01'),

  -- Beta ERP: widerrufen (zusammen mit Lizenz)
  (3, 'act_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f', 2,
   'd4bf3c2a1e0f9d8c7b6a5e4f3d2c1b0a9e8f7d6c5b4a3e2f1d0c9b8a7e6f5d4',
   '2026-02-15T12:00:00Z', '2026-04-30T18:00:00Z', 'revoked', 'beta-prod (Kundenkündigung)'),

  -- Gamma ERP: eigener Produktionsserver mit Hostname-Constraint
  (4, 'act_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a', 3,
   'e5c4d3b2a1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f2e1d0c9b8a7f6e5',
   '2026-04-01T09:00:00Z', '2026-06-20T11:45:00Z', 'active', 'app.gamma-tech.de'),

  -- Acme ShopSystem: Shop-Produktionsserver
  (5, 'act_e5f6a7b8c9d04e1f2a3b4c5d6e7f8a9b', 5,
   'f6d5e4c3b2a1f0e9d8c7b6a5f4e3d2c1b0a9f8e7d6c5b4a3f2e1d0c9b8a7f6',
   '2026-05-15T07:00:00Z', '2026-06-28T04:00:00Z', 'active', 'shop.acme.de (prod)');

-- ── Runtime Leases ───────────────────────────────────────────────────────────
INSERT OR IGNORE INTO runtime_leases
  (id, lease_uid, license_id, build_id, activation_id, nonce,
   issued_at, expires_at, grace_until, status)
VALUES
  -- Acme ERP Produktionsserver: aktuell gültig
  (1, 'lease_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d', 1, 1, 1,
   'dGVzdC1ub25jZS0wMDAxMjM0NTY3ODk=',
   '2026-06-28T04:00:00Z', '2026-06-28T16:00:00Z', '2026-06-29T04:00:00Z',
   'issued'),

  -- Acme ERP Staging: abgelaufen
  (2, 'lease_b2c3d4e5f6a74b8c9d0e1f2a3b4c5d6e', 1, 1, 2,
   'dGVzdC1ub25jZS0wMDAyMzQ1Njc4OTA=',
   '2026-06-27T04:00:00Z', '2026-06-27T16:00:00Z', '2026-06-28T04:00:00Z',
   'expired'),

  -- Gamma ERP: aktuell gültig
  (3, 'lease_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f', 3, 1, 4,
   'dGVzdC1ub25jZS0wMDAzNDU2Nzg5MDI=',
   '2026-06-28T00:00:00Z', '2026-06-28T12:00:00Z', '2026-06-29T00:00:00Z',
   'issued'),

  -- Acme ShopSystem: aktuell gültig
  (4, 'lease_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a', 5, 2, 5,
   'dGVzdC1ub25jZS0wMDA0NTY3ODkwMTI=',
   '2026-06-28T02:00:00Z', '2026-06-28T14:00:00Z', '2026-06-29T02:00:00Z',
   'issued');

-- ── Widerrufe ────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO revocations (id, revocation_uid, entity_type, entity_uid, reason)
VALUES
  (1, 'rev_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d',
   'license', 'lic_ab943b1e175d4b749d13c03a85d209a7',
   'Vertrag mit Beta Solutions AG zum 30.04.2026 gekündigt'),

  (2, 'rev_b2c3d4e5f6a74b8c9d0e1f2a3b4c5d6e',
   'activation', 'act_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f',
   'Aktivierung ungültig nach Lizenzentzug'),

  (3, 'rev_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f',
   'build', 'build_3f4a5b6c7d8e4f9a0b1c2d3e4f5a6b7c',
   'Sicherheitslücke in Delta ERP v0.9.0 entdeckt (CVE-Demo)'),

  (4, 'rev_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a',
   'api_client', 'client_ccdd3344ee5566ff7788aa99bb00cc11',
   'Support-Zugang nach Mitarbeiterwechsel deaktiviert');

-- ── Audit-Log ────────────────────────────────────────────────────────────────
INSERT OR IGNORE INTO audit_log
  (id, event_uid, actor_type, actor_ref, event_type, entity_type, entity_uid, ip_address, details)
VALUES
  (1,  'evt_001', 'encoder', 'qa-encoder-key-123', 'customer_created',  'customer', 'cust_4833dbabcd254b84b3106e5d6831f0a9', '192.168.1.10', '{"name":"Acme GmbH"}'),
  (2,  'evt_002', 'encoder', 'qa-encoder-key-123', 'customer_created',  'customer', 'cust_8ce5e5bcaacc4577a326f32a15b4ec3e', '192.168.1.10', '{"name":"Beta Solutions AG"}'),
  (3,  'evt_003', 'encoder', 'qa-encoder-key-123', 'customer_created',  'customer', 'cust_c1d2e3f4a5b64c7d8e9f0a1b2c3d4e5f', '10.0.0.5',     '{"name":"Gamma Tech KG"}'),
  (4,  'evt_004', 'encoder', 'qa-encoder-key-123', 'customer_created',  'customer', 'cust_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a', '10.0.0.5',     '{"name":"Delta Systems OHG"}'),
  (5,  'evt_005', 'encoder', 'qa-encoder-key-123', 'project_created',   'project',  'proj_f4a8d21bd2ed49329de748a429eb1972',  '192.168.1.10', '{"projectKey":"acme-erp-v1"}'),
  (6,  'evt_006', 'encoder', 'qa-encoder-key-123', 'project_created',   'project',  'proj_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d',  '192.168.1.10', '{"projectKey":"shopsystem-pro"}'),
  (7,  'evt_007', 'encoder', 'qa-encoder-key-123', 'license_created',   'license',  'lic_da548e9ddfbd4e408368918b3204deb4',  '192.168.1.10', '{"licenseKey":"LIC-ACME-2026-001","maxActivations":5}'),
  (8,  'evt_008', 'encoder', 'qa-encoder-key-123', 'license_created',   'license',  'lic_ab943b1e175d4b749d13c03a85d209a7',  '192.168.1.10', '{"licenseKey":"LIC-BETA-2026-001","maxActivations":2}'),
  (9,  'evt_009', 'encoder', 'qa-encoder-key-123', 'license_created',   'license',  'lic_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f',  '10.0.0.5',     '{"licenseKey":"LIC-GAMMA-2026-001","maxActivations":1}'),
  (10, 'evt_010', 'encoder', 'qa-encoder-key-123', 'license_created',   'license',  'lic_e5f6a7b8c9d04e1f2a3b4c5d6e7f8a9b',  '192.168.1.10', '{"licenseKey":"LIC-ACME-SHOP-2026-001","maxActivations":3}'),
  (11, 'evt_011', 'encoder', 'qa-encoder-key-123', 'build_started',     'build',    'build_1d2967fe74374d9fa1b259207186993e', '192.168.1.10', '{"buildId":"build_1d2967fe74374d9fa1b259207186993e","version":"1.0.0"}'),
  (12, 'evt_012', 'encoder', 'qa-encoder-key-123', 'build_signed',      'build',    'build_1d2967fe74374d9fa1b259207186993e', '192.168.1.10', '{"buildId":"build_1d2967fe74374d9fa1b259207186993e","fileCount":6}'),
  (13, 'evt_013', 'encoder', 'mm_cicd_pipeline_encoder_2026', 'build_started', 'build', 'build_2e3f4a5b6c7d4e8f9a0b1c2d3e4f5a6b', '10.10.1.50', '{"buildId":"build_2e3f4a5b6c7d4e8f9a0b1c2d3e4f5a6b","version":"2.1.0"}'),
  (14, 'evt_014', 'encoder', 'mm_cicd_pipeline_encoder_2026', 'build_signed',  'build', 'build_2e3f4a5b6c7d4e8f9a0b1c2d3e4f5a6b', '10.10.1.50', '{"buildId":"build_2e3f4a5b6c7d4e8f9a0b1c2d3e4f5a6b","fileCount":12}'),
  (15, 'evt_015', 'loader',  'mmloader/0.1.0',    'lease_issued',      'license',  'lic_da548e9ddfbd4e408368918b3204deb4',  '203.0.113.10', '{"leaseId":"lease_a1b2c3d4e5f64a7b8c9d0e1f2a3b4c5d","host":"prod-server-01"}'),
  (16, 'evt_016', 'loader',  'mmloader/0.1.0',    'lease_issued',      'license',  'lic_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f',  '198.51.100.5', '{"leaseId":"lease_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f","host":"app.gamma-tech.de"}'),
  (17, 'evt_017', 'loader',  'mmloader/0.1.0',    'lease_issued',      'license',  'lic_e5f6a7b8c9d04e1f2a3b4c5d6e7f8a9b',  '203.0.113.55', '{"leaseId":"lease_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a","host":"shop.acme.de"}'),
  (18, 'evt_018', 'admin',   'admin-api',         'license_revoked',   'license',  'lic_ab943b1e175d4b749d13c03a85d209a7',  '127.0.0.1',    '{"reason":"Vertrag gekündigt"}'),
  (19, 'evt_019', 'admin',   'admin-api',         'build_revoked',     'build',    'build_3f4a5b6c7d8e4f9a0b1c2d3e4f5a6b7c', '127.0.0.1',    '{"reason":"CVE-Demo Sicherheitslücke"}'),
  (20, 'evt_020', 'admin',   'admin-api',         'activation_revoked','activation','act_c3d4e5f6a7b84c9d0e1f2a3b4c5d6e7f',  '127.0.0.1',    '{"reason":"Aktivierung ungültig nach Lizenzentzug"}'),
  (21, 'evt_021', 'loader',  'mmloader/0.1.0',    'lease_denied',      'license',  'lic_ab943b1e175d4b749d13c03a85d209a7',  '185.1.2.3',    '{"reason":"LICENSE_REVOKED"}'),
  (22, 'evt_022', 'loader',  'mmloader/0.1.0',    'lease_denied',      'license',  'lic_d4e5f6a7b8c94d0e1f2a3b4c5d6e7f8a',  '10.5.5.5',    '{"reason":"LICENSE_EXPIRED"}');

-- ── Zusammenfassung ───────────────────────────────────────────────────────────
SELECT '=== Beispieldaten eingespielt ===' AS info;
SELECT 'Kunden:        ' || COUNT(*) AS stat FROM customers;
SELECT 'Projekte:      ' || COUNT(*) AS stat FROM projects;
SELECT 'Lizenzen:      ' || COUNT(*) AS stat FROM licenses;
SELECT 'Builds:        ' || COUNT(*) AS stat FROM builds;
SELECT 'Build-Dateien: ' || COUNT(*) AS stat FROM build_files;
SELECT 'Aktivierungen: ' || COUNT(*) AS stat FROM license_activations;
SELECT 'Leases:        ' || COUNT(*) AS stat FROM runtime_leases;
SELECT 'Widerrufe:     ' || COUNT(*) AS stat FROM revocations;
SELECT 'Audit-Events:  ' || COUNT(*) AS stat FROM audit_log;
SELECT 'API-Clients:   ' || COUNT(*) AS stat FROM api_clients;
