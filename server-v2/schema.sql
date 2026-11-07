PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS meta (
  k TEXT PRIMARY KEY,
  v TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS user (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  email        TEXT NOT NULL COLLATE NOCASE UNIQUE,
  nickname     TEXT NOT NULL,
  avatar       TEXT NOT NULL DEFAULT '',
  signature    TEXT NOT NULL DEFAULT '',
  pass_hash    TEXT NOT NULL DEFAULT '',
  invite_code  TEXT NOT NULL DEFAULT '',
  rayskin_uuid TEXT NOT NULL DEFAULT '',
  status       TEXT NOT NULL DEFAULT 'active',
  created_at   INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS auth_token (
  token      TEXT PRIMARY KEY,
  user_id    INTEGER NOT NULL,
  device     TEXT NOT NULL DEFAULT '',
  created_at INTEGER NOT NULL DEFAULT 0,
  expires_at INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_token_user ON auth_token(user_id);

CREATE TABLE IF NOT EXISTS auth_code (
  email      TEXT NOT NULL,
  purpose    TEXT NOT NULL DEFAULT 'register',
  code       TEXT NOT NULL,
  expires_at INTEGER NOT NULL,
  used       INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (email, purpose)
);

CREATE TABLE IF NOT EXISTS invite (
  code       TEXT PRIMARY KEY,
  max_uses   INTEGER NOT NULL DEFAULT 0,
  used       INTEGER NOT NULL DEFAULT 0,
  created_by INTEGER NOT NULL DEFAULT 0,
  disabled   INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS friend_request (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  from_id    INTEGER NOT NULL,
  to_id      INTEGER NOT NULL,
  wording    TEXT NOT NULL DEFAULT '',
  source     TEXT NOT NULL DEFAULT 'search',
  remark     TEXT NOT NULL DEFAULT '',
  group_id   INTEGER NOT NULL DEFAULT 0,
  status     TEXT NOT NULL DEFAULT 'pending',
  read_at    INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0,
  handled_at INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_req_to ON friend_request(to_id, status);
CREATE INDEX IF NOT EXISTS idx_req_from ON friend_request(from_id, status);

CREATE TABLE IF NOT EXISTS friendship (
  user_id    INTEGER NOT NULL,
  friend_id  INTEGER NOT NULL,
  remark     TEXT NOT NULL DEFAULT '',
  group_id   INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, friend_id)
);

CREATE TABLE IF NOT EXISTS friend_group (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id    INTEGER NOT NULL,
  name       TEXT NOT NULL,
  sort       INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_fg_name ON friend_group(user_id, name);

CREATE TABLE IF NOT EXISTS block (
  user_id    INTEGER NOT NULL,
  blocked_id INTEGER NOT NULL,
  created_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, blocked_id)
);

CREATE TABLE IF NOT EXISTS conversation (
  user_id     INTEGER NOT NULL,
  conv_type   INTEGER NOT NULL,
  conv_key    TEXT NOT NULL,
  last_msg_id INTEGER NOT NULL DEFAULT 0,
  last_body   TEXT NOT NULL DEFAULT '',
  last_at     INTEGER NOT NULL DEFAULT 0,
  unread      INTEGER NOT NULL DEFAULT 0,
  pinned      INTEGER NOT NULL DEFAULT 0,
  muted       INTEGER NOT NULL DEFAULT 0,
  hidden      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, conv_type, conv_key)
);

CREATE TABLE IF NOT EXISTS message (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  conv_type     INTEGER NOT NULL,
  conv_key      TEXT NOT NULL,
  from_id       INTEGER NOT NULL,
  to_id         INTEGER NOT NULL DEFAULT 0,
  group_id      INTEGER NOT NULL DEFAULT 0,
  client_msg_id TEXT NOT NULL DEFAULT '',
  body          TEXT NOT NULL DEFAULT '',
  kind          TEXT NOT NULL DEFAULT 'text',
  created_at    INTEGER NOT NULL DEFAULT 0,
  recalled_at   INTEGER NOT NULL DEFAULT 0,
  tim_msg_id    TEXT NOT NULL DEFAULT ''
);
CREATE UNIQUE INDEX IF NOT EXISTS idx_msg_client ON message(from_id, client_msg_id)
  WHERE client_msg_id <> '';
CREATE INDEX IF NOT EXISTS idx_msg_conv ON message(conv_type, conv_key, id);

CREATE TABLE IF NOT EXISTS chat_group (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  name         TEXT NOT NULL,
  owner_id     INTEGER NOT NULL,
  avatar       TEXT NOT NULL DEFAULT '',
  notice       TEXT NOT NULL DEFAULT '',
  tim_group_id TEXT NOT NULL DEFAULT '',
  created_at   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS group_member (
  group_id  INTEGER NOT NULL,
  user_id   INTEGER NOT NULL,
  role      INTEGER NOT NULL DEFAULT 0,
  joined_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id, user_id)
);
CREATE INDEX IF NOT EXISTS idx_gm_user ON group_member(user_id);

CREATE TABLE IF NOT EXISTS event_log (
  seq     INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id INTEGER NOT NULL,
  type    TEXT NOT NULL,
  payload TEXT NOT NULL,
  ts      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_evt_user ON event_log(user_id, seq);

CREATE TABLE IF NOT EXISTS presence (
  user_id      INTEGER PRIMARY KEY,
  conn_count   INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);

INSERT OR REPLACE INTO meta (k, v) VALUES ('schema', '2');
