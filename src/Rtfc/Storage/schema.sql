-- The schema from spec §13, verbatim. Private keys are never stored here (§4).
-- Every statement is idempotent so the file can be applied on every open.

CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS self (
  person_id           TEXT PRIMARY KEY,
  handle              TEXT NOT NULL,
  person_ca_cert      BLOB NOT NULL,              -- DER; private key lives in keys/
  device_id           TEXT NOT NULL,
  device_name         TEXT NOT NULL,
  device_cert         BLOB NOT NULL,              -- DER
  device_list_version INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS contacts (
  person_id           TEXT PRIMARY KEY,
  handle              TEXT NOT NULL,              -- local petname
  person_ca_cert      BLOB NOT NULL,              -- DER; pinned trust anchor
  status              TEXT NOT NULL,              -- active | removed | blocked
  accepted_at         TEXT,
  inbound_mode        TEXT NOT NULL DEFAULT 'park', -- park | auto_headless | auto_session
  auto_scope          TEXT,
  auto_owner_device   TEXT,                       -- which of MY devices auto-answers
  read_receipts       INTEGER NOT NULL DEFAULT 1,
  device_list_version INTEGER NOT NULL DEFAULT 0,
  rev                 INTEGER NOT NULL DEFAULT 0, -- own-device sync, LWW
  auto_session        TEXT                        -- the Claude Code session that answers them in auto_session mode (spec §7.3, schema v5)
);

CREATE TABLE IF NOT EXISTS devices (             -- contacts' devices AND my own
  device_id   TEXT PRIMARY KEY,
  person_id   TEXT NOT NULL,
  name        TEXT NOT NULL,
  cert        BLOB NOT NULL,                      -- DER device cert, issued by the person CA
  status      TEXT NOT NULL,                      -- active | revoked
  endpoints   TEXT NOT NULL DEFAULT '[]'          -- JSON EndpointHint[]
);

CREATE TABLE IF NOT EXISTS invites (
  nonce       TEXT PRIMARY KEY,
  created_at  TEXT NOT NULL,
  expires_at  TEXT NOT NULL,
  used_by     TEXT,
  used_at     TEXT
);

CREATE TABLE IF NOT EXISTS inbox (
  id          TEXT NOT NULL,
  to_device   TEXT NOT NULL,
  kind        TEXT NOT NULL,                      -- person | source | notice (a local note from rtfc itself, schema v3)
  project_id  TEXT,                               -- NULL = the shared inbox; set for source items and project-addressed messages (spec §7.6)
  -- person messages
  from_person TEXT,
  from_device TEXT,
  seq         INTEGER,
  reply_to    TEXT,
  origin      TEXT,                               -- human | auto
  hop         INTEGER,
  -- source items (one row per entity, coalesced)
  subscription_id TEXT,
  entity_key  TEXT,                               -- "jira:PAY-123"
  url         TEXT,
  events      TEXT,                               -- JSON history of SourceEvent, newest last
  draft       TEXT,                               -- output of a `prepare` run, or an auto-answer that could not be delivered
  note        TEXT,                               -- a line for the human: why it was not auto-answered, what happened to the reply (v2, renamed v3)
  auto_attempts INTEGER NOT NULL DEFAULT 0,       -- auto-answer runs started for this message (v2)
  -- common
  thread      TEXT,
  title       TEXT,
  body        TEXT NOT NULL,                      -- message text, or latest event summary
  sent_at     TEXT,
  received_at TEXT NOT NULL,
  updated_at  TEXT NOT NULL,
  state       TEXT NOT NULL,  -- parked | read | answered | dismissed | auto_running | auto_done | auto_failed
  handled_by  TEXT,
  handled_at  TEXT,
  PRIMARY KEY (id, to_device)
);
CREATE UNIQUE INDEX IF NOT EXISTS inbox_entity ON inbox (project_id, entity_key) WHERE kind = 'source';

CREATE TABLE IF NOT EXISTS accounts (            -- secrets live in the keychain / keys dir
  name        TEXT PRIMARY KEY,
  type        TEXT NOT NULL,                      -- jira | confluence | bitbucket | github
  base_url    TEXT,
  created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS projects (
  id          TEXT PRIMARY KEY,
  root_path   TEXT NOT NULL UNIQUE,               -- normalized path key (lower case on Windows)
  name        TEXT
);

CREATE TABLE IF NOT EXISTS subscriptions (
  id          TEXT PRIMARY KEY,
  project_id  TEXT NOT NULL,
  account     TEXT NOT NULL,
  selector    TEXT NOT NULL,                      -- JSON: repo / jql / space …
  events      TEXT NOT NULL,                      -- JSON array
  mode        TEXT NOT NULL DEFAULT 'park',       -- park | prepare
  status      TEXT NOT NULL,                      -- pending_approval | active | disabled | error
  config_hash TEXT NOT NULL                       -- detects edits that need re-approval
);

CREATE TABLE IF NOT EXISTS source_cursors (      -- one per (account, selector); shared across projects
  poll_key    TEXT PRIMARY KEY,
  cursor      TEXT NOT NULL,
  boundary_ids TEXT NOT NULL DEFAULT '[]',
  next_poll_at TEXT,
  last_error  TEXT
);

CREATE TABLE IF NOT EXISTS outbox (
  id          TEXT PRIMARY KEY,
  to_person   TEXT NOT NULL,
  to_device   TEXT,                               -- NULL = any active device
  kind        TEXT NOT NULL,                      -- reply | receipt | handled | contact_sync | device_list
  envelope    TEXT NOT NULL,
  created_at  TEXT NOT NULL,
  expires_at  TEXT NOT NULL,
  attempts    INTEGER NOT NULL DEFAULT 0,
  state       TEXT NOT NULL                       -- pending | delivered | expired
);

CREATE TABLE IF NOT EXISTS sent (                -- what left this device, so receipts and expiries have somewhere to land (schema v3)
  id          TEXT PRIMARY KEY,
  to_person   TEXT NOT NULL,
  to_device   TEXT,                               -- NULL = whichever device took it
  thread      TEXT,
  reply_to    TEXT,                               -- the inbox message this answered, if any
  origin      TEXT NOT NULL,                      -- human | auto
  kind        TEXT NOT NULL,                      -- message | reply
  body        TEXT NOT NULL,
  sent_at     TEXT NOT NULL,
  delivered_at TEXT,
  read_at     TEXT,
  expires_at  TEXT,                               -- while queued in the outbox
  state       TEXT NOT NULL,                      -- queued | delivered | read | expired
  project_id  TEXT                                -- where a reply to this lands (spec §7.6, schema v4); NULL = the shared inbox
);

CREATE TABLE IF NOT EXISTS seq_out (to_device   TEXT PRIMARY KEY, next_seq INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS seq_in  (from_device TEXT PRIMARY KEY, max_seq  INTEGER NOT NULL);

INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '5');
