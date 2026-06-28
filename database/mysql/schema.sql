CREATE DATABASE IF NOT EXISTS mm_license
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_unicode_ci;

USE mm_license;

CREATE TABLE IF NOT EXISTS customers (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  customer_uid VARCHAR(64) NOT NULL,
  external_customer_ref VARCHAR(191) NOT NULL,
  name VARCHAR(255) NOT NULL,
  email VARCHAR(255) NULL,
  notes TEXT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_customers_uid (customer_uid),
  UNIQUE KEY uq_customers_external_ref (external_customer_ref)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS projects (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  project_uid VARCHAR(64) NOT NULL,
  project_key VARCHAR(191) NOT NULL,
  name VARCHAR(255) NOT NULL,
  php_min_version VARCHAR(32) NOT NULL DEFAULT '8.4',
  description TEXT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_projects_uid (project_uid),
  UNIQUE KEY uq_projects_key (project_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS api_clients (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  client_uid VARCHAR(64) NOT NULL,
  name VARCHAR(255) NOT NULL,
  api_key_hash VARCHAR(64) NOT NULL,
  scopes JSON NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  revoked_at DATETIME NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_api_clients_uid (client_uid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS crypto_keys (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  key_uid VARCHAR(64) NOT NULL,
  key_type ENUM('vendor_signing','build','runtime') NOT NULL,
  algorithm VARCHAR(64) NOT NULL,
  public_key_pem TEXT NULL,
  encrypted_private_key TEXT NULL,
  encrypted_secret_key TEXT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  retired_at DATETIME NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_crypto_keys_uid (key_uid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS licenses (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  license_uid VARCHAR(64) NOT NULL,
  customer_id BIGINT UNSIGNED NOT NULL,
  project_id BIGINT UNSIGNED NOT NULL,
  license_key VARCHAR(191) NOT NULL,
  valid_from DATETIME NOT NULL,
  valid_until DATETIME NULL,
  max_activations INT UNSIGNED NOT NULL DEFAULT 1,
  features JSON NULL,
  constraints JSON NULL,
  status ENUM('active','suspended','expired','revoked') NOT NULL DEFAULT 'active',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_licenses_uid (license_uid),
  UNIQUE KEY uq_licenses_key (license_key),
  KEY idx_licenses_customer_project (customer_id, project_id),
  CONSTRAINT fk_licenses_customer FOREIGN KEY (customer_id) REFERENCES customers(id),
  CONSTRAINT fk_licenses_project FOREIGN KEY (project_id) REFERENCES projects(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS builds (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  build_uid VARCHAR(64) NOT NULL,
  customer_id BIGINT UNSIGNED NOT NULL,
  project_id BIGINT UNSIGNED NOT NULL,
  license_id BIGINT UNSIGNED NOT NULL,
  key_id BIGINT UNSIGNED NULL,
  version VARCHAR(64) NOT NULL,
  source_revision VARCHAR(191) NULL,
  encoder_version VARCHAR(64) NULL,
  manifest_hash VARCHAR(128) NULL,
  manifest_signature TEXT NULL,
  manifest_json MEDIUMTEXT NULL,
  download_url VARCHAR(2048) NULL,
  file_count INT UNSIGNED NOT NULL DEFAULT 0,
  status ENUM('started','files_registered','signed','revoked') NOT NULL DEFAULT 'started',
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  signed_at DATETIME NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_builds_uid (build_uid),
  KEY idx_builds_license (license_id),
  CONSTRAINT fk_builds_customer FOREIGN KEY (customer_id) REFERENCES customers(id),
  CONSTRAINT fk_builds_project FOREIGN KEY (project_id) REFERENCES projects(id),
  CONSTRAINT fk_builds_license FOREIGN KEY (license_id) REFERENCES licenses(id),
  CONSTRAINT fk_builds_key FOREIGN KEY (key_id) REFERENCES crypto_keys(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS build_files (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  build_id BIGINT UNSIGNED NOT NULL,
  file_uid VARCHAR(64) NOT NULL,
  relative_path VARCHAR(1024) NOT NULL,
  path_hash VARCHAR(128) NOT NULL,
  plain_hash VARCHAR(128) NOT NULL,
  cipher_hash VARCHAR(128) NOT NULL,
  algorithm VARCHAR(64) NOT NULL,
  kdf VARCHAR(64) NOT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_build_file_uid (build_id, file_uid),
  KEY idx_build_files_path_hash (path_hash),
  CONSTRAINT fk_build_files_build FOREIGN KEY (build_id) REFERENCES builds(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS license_activations (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  activation_uid VARCHAR(64) NOT NULL,
  license_id BIGINT UNSIGNED NOT NULL,
  machine_fingerprint VARCHAR(128) NOT NULL,
  first_seen_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_seen_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  status ENUM('active','revoked') NOT NULL DEFAULT 'active',
  notes TEXT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_activation_uid (activation_uid),
  UNIQUE KEY uq_license_machine (license_id, machine_fingerprint),
  CONSTRAINT fk_activations_license FOREIGN KEY (license_id) REFERENCES licenses(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS runtime_leases (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  lease_uid VARCHAR(64) NOT NULL,
  license_id BIGINT UNSIGNED NOT NULL,
  build_id BIGINT UNSIGNED NOT NULL,
  activation_id BIGINT UNSIGNED NOT NULL,
  nonce VARCHAR(255) NOT NULL,
  issued_at DATETIME NOT NULL,
  expires_at DATETIME NOT NULL,
  grace_until DATETIME NOT NULL,
  status ENUM('issued','revoked','expired') NOT NULL DEFAULT 'issued',
  lease_signature TEXT,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_runtime_leases_uid (lease_uid),
  KEY idx_runtime_leases_license (license_id),
  KEY idx_runtime_leases_activation (activation_id),
  CONSTRAINT fk_leases_license FOREIGN KEY (license_id) REFERENCES licenses(id),
  CONSTRAINT fk_leases_build FOREIGN KEY (build_id) REFERENCES builds(id),
  CONSTRAINT fk_leases_activation FOREIGN KEY (activation_id) REFERENCES license_activations(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS revocations (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  revocation_uid VARCHAR(64) NOT NULL,
  entity_type ENUM('license','build','activation','api_client') NOT NULL,
  entity_uid VARCHAR(64) NOT NULL,
  reason TEXT NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_revocation_uid (revocation_uid),
  KEY idx_revocations_entity (entity_type, entity_uid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS audit_log (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  event_uid VARCHAR(64) NOT NULL,
  actor_type ENUM('server','encoder','loader','admin') NOT NULL,
  actor_ref VARCHAR(191) NULL,
  event_type VARCHAR(191) NOT NULL,
  entity_type VARCHAR(191) NULL,
  entity_uid VARCHAR(64) NULL,
  ip_address VARCHAR(64) NULL,
  user_agent VARCHAR(512) NULL,
  details JSON NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  UNIQUE KEY uq_audit_event_uid (event_uid),
  KEY idx_audit_entity (entity_type, entity_uid),
  KEY idx_audit_event_type (event_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS error_reports (
  id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  build_id BIGINT UNSIGNED NULL,
  license_uid VARCHAR(64) NOT NULL,
  machine_fingerprint VARCHAR(128) NULL,
  reported_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  error_level INT NOT NULL,
  error_message TEXT NOT NULL,
  error_file VARCHAR(1024) NULL,
  error_line INT NULL,
  php_version VARCHAR(32) NULL,
  sapi VARCHAR(64) NULL,
  PRIMARY KEY (id),
  KEY idx_error_reports_license (license_uid),
  KEY idx_error_reports_build (build_id),
  KEY idx_error_reports_time (reported_at),
  CONSTRAINT fk_error_reports_build FOREIGN KEY (build_id) REFERENCES builds(id) ON DELETE SET NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Optional telemetry from EncoderCLI and PHP Loader (disabled by default on client side)
CREATE TABLE IF NOT EXISTS telemetry_events (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  source       ENUM('encoder','loader') NOT NULL,
  event_type   VARCHAR(64) NOT NULL,
  license_uid  VARCHAR(64) NULL,
  build_uid    VARCHAR(64) NULL,
  project_uid  VARCHAR(64) NULL,
  payload_json JSON NULL,
  occurred_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  client_ip    VARCHAR(64) NULL,
  PRIMARY KEY (id),
  KEY idx_telemetry_source  (source),
  KEY idx_telemetry_event   (event_type),
  KEY idx_telemetry_time    (occurred_at),
  KEY idx_telemetry_license (license_uid)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
