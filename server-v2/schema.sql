PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- 体验版能力范围（Lite）：好友列表 + 申请 + 单聊/群聊 + 会话
-- 不含：在线状态、好友分组、备注、黑名单、关系链镜像、漫游

CREATE TABLE IF NOT EXISTS meta (
  k TEXT PRIMARY KEY,
  v TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS user (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  email        TEXT NOT NULL COLLATE NOCASE UNIQUE,
  nickname     TEXT NOT NULL,
  avatar       TEXT NOT NULL DEFAULT '',
  pass_hash    TEXT NOT NULL DEFAULT '',
  invite_code  TEXT NOT NULL DEFAULT '',
  rayskin_uuid TEXT NOT NULL DEFAULT '',
  status       TEXT NOT NULL DEFAULT 'active',
  created_at   INTEGER NOT NULL DEFAULT 0
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

-- 好友申请：pending / accepted / rejected
CREATE TABLE IF NOT EXISTS friend_request (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  from_id    INTEGER NOT NULL,
  to_id      INTEGER NOT NULL,
  wording    TEXT NOT NULL DEFAULT '',
  source     TEXT NOT NULL DEFAULT 'search',
  status     TEXT NOT NULL DEFAULT 'pending',
  created_at INTEGER NOT NULL DEFAULT 0,
  handled_at INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_req_to ON friend_request(to_id, status);
CREATE INDEX IF NOT EXISTS idx_req_from ON friend_request(from_id, status);

-- 好友关系：双向两行，只保留关系本身
CREATE TABLE IF NOT EXISTS friendship (
  user_id    INTEGER NOT NULL,
  friend_id  INTEGER NOT NULL,
  created_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, friend_id)
);

-- 会话：每人一份，按最后消息时间排序
CREATE TABLE IF NOT EXISTS conversation (
  user_id     INTEGER NOT NULL,
  conv_type   INTEGER NOT NULL,
  conv_key    TEXT NOT NULL,
  last_msg_id INTEGER NOT NULL DEFAULT 0,
  last_body   TEXT NOT NULL DEFAULT '',
  last_at     INTEGER NOT NULL DEFAULT 0,
  unread      INTEGER NOT NULL DEFAULT 0,
  hidden      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (user_id, conv_type, conv_key)
);

-- 消息：唯一事实来源
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

-- 事件：断线补发
CREATE TABLE IF NOT EXISTS event_log (
  seq     INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id INTEGER NOT NULL,
  type    TEXT NOT NULL,
  payload TEXT NOT NULL,
  ts      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_evt_user ON event_log(user_id, seq);

INSERT OR REPLACE INTO meta (k, v) VALUES ('schema', '3');
