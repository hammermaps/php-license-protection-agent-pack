-- MMProtect License Server — SQLite schema (for integration tests and embedded deployments)
-- Compatible with SQLite 3.24+ (ON CONFLICT DO UPDATE syntax)

PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS customers (
  id                    INTEGER PRIMARY KEY AUTOINCREMENT,
  customer_uid          TEXT NOT NULL UNIQUE,
  external_customer_ref TEXT NOT NULL UNIQUE,
  name                  TEXT NOT NULL,
  email                 TEXT,
  notes                 TEXT,
  created_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS projects (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  project_uid     TEXT NOT NULL UNIQUE,
  project_key     TEXT NOT NULL UNIQUE,
  name            TEXT NOT NULL,
  php_min_version TEXT NOT NULL DEFAULT '8.4',
  description     TEXT,
  created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS api_clients (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  client_uid     TEXT NOT NULL UNIQUE,
  name           TEXT NOT NULL,
  api_key_hash   TEXT NOT NULL,
  scopes         TEXT,
  is_active      INTEGER NOT NULL DEFAULT 1,
  created_at     TEXT NOT NULL DEFAULT (datetime('now')),
  revoked_at     TEXT
);

CREATE TABLE IF NOT EXISTS crypto_keys (
  id                    INTEGER PRIMARY KEY AUTOINCREMENT,
  key_uid               TEXT NOT NULL UNIQUE,
  key_type              TEXT NOT NULL CHECK(key_type IN ('vendor_signing','build','runtime')),
  algorithm             TEXT NOT NULL,
  public_key_pem        TEXT,
  encrypted_private_key TEXT,
  encrypted_secret_key  TEXT,
  is_active             INTEGER NOT NULL DEFAULT 1,
  created_at            TEXT NOT NULL DEFAULT (datetime('now')),
  retired_at            TEXT
);

CREATE TABLE IF NOT EXISTS licenses (
  id               INTEGER PRIMARY KEY AUTOINCREMENT,
  license_uid      TEXT NOT NULL UNIQUE,
  customer_id      INTEGER NOT NULL REFERENCES customers(id),
  project_id       INTEGER NOT NULL REFERENCES projects(id),
  license_key      TEXT NOT NULL UNIQUE,
  valid_from       TEXT NOT NULL,
  valid_until      TEXT,
  max_activations  INTEGER NOT NULL DEFAULT 1,
  features         TEXT,
  constraints      TEXT,
  status           TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','suspended','expired','revoked')),
  created_at       TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_licenses_customer_project ON licenses(customer_id, project_id);

CREATE TABLE IF NOT EXISTS builds (
  id                 INTEGER PRIMARY KEY AUTOINCREMENT,
  build_uid          TEXT NOT NULL UNIQUE,
  customer_id        INTEGER NOT NULL REFERENCES customers(id),
  project_id         INTEGER NOT NULL REFERENCES projects(id),
  license_id         INTEGER NOT NULL REFERENCES licenses(id),
  key_id             INTEGER REFERENCES crypto_keys(id),
  version            TEXT NOT NULL,
  source_revision    TEXT,
  encoder_version    TEXT,
  manifest_hash      TEXT,
  manifest_signature TEXT,
  manifest_json      TEXT,
  download_url       TEXT,
  file_count         INTEGER NOT NULL DEFAULT 0,
  status             TEXT NOT NULL DEFAULT 'started' CHECK(status IN ('started','files_registered','signed','revoked')),
  created_at         TEXT NOT NULL DEFAULT (datetime('now')),
  signed_at          TEXT
);

CREATE INDEX IF NOT EXISTS idx_builds_license ON builds(license_id);

CREATE TABLE IF NOT EXISTS build_files (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  build_id      INTEGER NOT NULL REFERENCES builds(id) ON DELETE CASCADE,
  file_uid      TEXT NOT NULL,
  relative_path TEXT NOT NULL,
  path_hash     TEXT NOT NULL,
  plain_hash    TEXT NOT NULL,
  cipher_hash   TEXT NOT NULL,
  algorithm     TEXT NOT NULL,
  kdf           TEXT NOT NULL,
  created_at    TEXT NOT NULL DEFAULT (datetime('now')),
  UNIQUE(build_id, file_uid)
);

CREATE INDEX IF NOT EXISTS idx_build_files_path_hash ON build_files(path_hash);

CREATE TABLE IF NOT EXISTS license_activations (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  activation_uid      TEXT NOT NULL UNIQUE,
  license_id          INTEGER NOT NULL REFERENCES licenses(id),
  machine_fingerprint TEXT NOT NULL,
  first_seen_at       TEXT NOT NULL DEFAULT (datetime('now')),
  last_seen_at        TEXT NOT NULL DEFAULT (datetime('now')),
  status              TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active','revoked')),
  notes               TEXT,
  UNIQUE(license_id, machine_fingerprint)
);

CREATE TABLE IF NOT EXISTS runtime_leases (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  lease_uid     TEXT NOT NULL UNIQUE,
  license_id    INTEGER NOT NULL REFERENCES licenses(id),
  build_id      INTEGER NOT NULL REFERENCES builds(id),
  activation_id INTEGER NOT NULL REFERENCES license_activations(id),
  nonce         TEXT NOT NULL,
  issued_at     TEXT NOT NULL,
  expires_at    TEXT NOT NULL,
  grace_until   TEXT NOT NULL,
  status          TEXT NOT NULL DEFAULT 'issued' CHECK(status IN ('issued','revoked','expired')),
  lease_signature TEXT,
  created_at    TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_runtime_leases_license    ON runtime_leases(license_id);
CREATE INDEX IF NOT EXISTS idx_runtime_leases_activation ON runtime_leases(activation_id);

CREATE TABLE IF NOT EXISTS revocations (
  id              INTEGER PRIMARY KEY AUTOINCREMENT,
  revocation_uid  TEXT NOT NULL UNIQUE,
  entity_type     TEXT NOT NULL CHECK(entity_type IN ('license','build','activation','api_client')),
  entity_uid      TEXT NOT NULL,
  reason          TEXT,
  created_at      TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_revocations_entity ON revocations(entity_type, entity_uid);

CREATE TABLE IF NOT EXISTS audit_log (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  event_uid   TEXT NOT NULL UNIQUE,
  actor_type  TEXT NOT NULL CHECK(actor_type IN ('server','encoder','loader','admin')),
  actor_ref   TEXT,
  event_type  TEXT NOT NULL,
  entity_type TEXT,
  entity_uid  TEXT,
  ip_address  TEXT,
  user_agent  TEXT,
  details     TEXT,
  created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_audit_entity     ON audit_log(entity_type, entity_uid);
CREATE INDEX IF NOT EXISTS idx_audit_event_type ON audit_log(event_type);

CREATE TABLE IF NOT EXISTS error_reports (
  id                  INTEGER PRIMARY KEY AUTOINCREMENT,
  build_id            INTEGER REFERENCES builds(id) ON DELETE SET NULL,
  license_uid         TEXT NOT NULL,
  machine_fingerprint TEXT,
  reported_at         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
  error_level         INTEGER NOT NULL,
  error_message       TEXT NOT NULL,
  error_file          TEXT,
  error_line          INTEGER,
  php_version         TEXT,
  sapi                TEXT
);

CREATE INDEX IF NOT EXISTS idx_error_reports_license ON error_reports(license_uid);
CREATE INDEX IF NOT EXISTS idx_error_reports_build   ON error_reports(build_id);
CREATE INDEX IF NOT EXISTS idx_error_reports_time    ON error_reports(reported_at);

-- Optional telemetry from EncoderCLI and PHP Loader (disabled by default on client side)
CREATE TABLE IF NOT EXISTS telemetry_events (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  source       TEXT NOT NULL CHECK(source IN ('encoder','loader')),
  event_type   TEXT NOT NULL,
  license_uid  TEXT,
  build_uid    TEXT,
  project_uid  TEXT,
  payload_json TEXT,
  occurred_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
  client_ip    TEXT
);

CREATE INDEX IF NOT EXISTS idx_telemetry_source ON telemetry_events(source);
CREATE INDEX IF NOT EXISTS idx_telemetry_event  ON telemetry_events(event_type);
CREATE INDEX IF NOT EXISTS idx_telemetry_time   ON telemetry_events(occurred_at);
CREATE INDEX IF NOT EXISTS idx_telemetry_license ON telemetry_events(license_uid);
