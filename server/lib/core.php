<?php
/**
 * 基础设施：配置、数据库、JSON 响应、日志。
 * 目标环境是 PHP 8.0+ 的普通虚拟主机，不依赖 Composer、Redis、常驻进程。
 */

declare(strict_types=1);

class ApiError extends Exception
{
    public string $errorCode;
    public int $httpStatus;
    public array $payload;

    public function __construct(string $errorCode, string $message, int $httpStatus = 400, array $payload = [])
    {
        parent::__construct($message);
        $this->errorCode = $errorCode;
        $this->httpStatus = $httpStatus;
        $this->payload = $payload;
    }
}

function cfg(?string $key = null, $default = null)
{
    static $config = null;
    if ($config === null) {
        $config = require __DIR__ . '/../config.php';
        date_default_timezone_set($config['timezone'] ?? 'Asia/Shanghai');
    }
    if ($key === null) {
        return $config;
    }
    return array_key_exists($key, $config) ? $config[$key] : $default;
}

function data_dir(): string
{
    $dir = rtrim(cfg('data_dir'), '/\\');
    if (!is_dir($dir)) {
        @mkdir($dir, 0775, true);
    }
    return $dir;
}

function release_dir(): string
{
    $dir = data_dir() . '/releases';
    if (!is_dir($dir)) {
        @mkdir($dir, 0775, true);
    }
    return $dir;
}

/** 数据库连接（WAL + 忙等待，避免并发下载时锁死） */
function db(): PDO
{
    static $pdo = null;
    if ($pdo instanceof PDO) {
        return $pdo;
    }
    $file = data_dir() . '/rmcl.sqlite';
    $isNew = !file_exists($file);
    try {
        $pdo = new PDO('sqlite:' . $file, null, null, [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]);
        $pdo->exec('PRAGMA journal_mode = WAL');
        $pdo->exec('PRAGMA busy_timeout = 5000');
        $pdo->exec('PRAGMA foreign_keys = ON');
    } catch (Throwable $e) {
        throw new ApiError('E_DB', '数据库无法打开：' . $e->getMessage(), 500);
    }
    db_migrate($pdo);
    if ($isNew) {
        @chmod($file, 0660);
    }
    return $pdo;
}

function db_migrate(PDO $pdo): void
{
    // 每次请求都跑一遍建表语句是浪费：用 user_version 标记版本，只有首次或升级时才建
    $version = (int) $pdo->query('PRAGMA user_version')->fetchColumn();
    if ($version >= 1) {
        return;
    }

    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS codes (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    code         TEXT    NOT NULL UNIQUE,
    note         TEXT    NOT NULL DEFAULT '',
    mode         TEXT    NOT NULL DEFAULT 'day',      -- day(激活起算) | date(固定到期日)
    days         INTEGER NOT NULL DEFAULT 30,
    fixed_expire TEXT    NOT NULL DEFAULT '',          -- mode=date 时的 YYYY-MM-DD
    device_limit INTEGER NOT NULL DEFAULT 1,
    status       TEXT    NOT NULL DEFAULT 'unused',    -- unused | active | disabled
    activated_at INTEGER NOT NULL DEFAULT 0,
    expire_at    INTEGER NOT NULL DEFAULT 0,
    created_at   INTEGER NOT NULL DEFAULT 0,
    last_seen_at INTEGER NOT NULL DEFAULT 0,
    last_ip      TEXT    NOT NULL DEFAULT '',
    last_device  TEXT    NOT NULL DEFAULT '',
    unbind_count INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS devices (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    code_id     INTEGER NOT NULL REFERENCES codes(id) ON DELETE CASCADE,
    device_id   TEXT    NOT NULL,
    device_name TEXT    NOT NULL DEFAULT '',
    first_seen  INTEGER NOT NULL DEFAULT 0,
    last_seen   INTEGER NOT NULL DEFAULT 0,
    ip          TEXT    NOT NULL DEFAULT '',
    app_version TEXT    NOT NULL DEFAULT '',
    UNIQUE (code_id, device_id)
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS tokens (
    token      TEXT    PRIMARY KEY,
    code_id    INTEGER NOT NULL REFERENCES codes(id) ON DELETE CASCADE,
    device_id  TEXT    NOT NULL,
    created_at INTEGER NOT NULL DEFAULT 0,
    expire_at  INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS tickets (
    ticket     TEXT    PRIMARY KEY,
    code_id    INTEGER NOT NULL,
    device_id  TEXT    NOT NULL,
    release_id INTEGER NOT NULL,
    created_at INTEGER NOT NULL DEFAULT 0,
    expire_at  INTEGER NOT NULL DEFAULT 0,
    uses       INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS releases (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    version      TEXT    NOT NULL,
    sort_key     TEXT    NOT NULL,                    -- 0002.0019.0000.0000，用于按版本排序
    title        TEXT    NOT NULL DEFAULT '',
    notes        TEXT    NOT NULL DEFAULT '',
    file_name    TEXT    NOT NULL,                    -- 展示给用户的文件名
    stored_name  TEXT    NOT NULL,                    -- data/releases 下的实际文件名（随机）
    size         INTEGER NOT NULL DEFAULT 0,
    sha256       TEXT    NOT NULL DEFAULT '',
    channel      TEXT    NOT NULL DEFAULT 'stable',
    status       TEXT    NOT NULL DEFAULT 'published', -- published | draft
    published_at INTEGER NOT NULL DEFAULT 0,
    downloads    INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS logs (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ts        INTEGER NOT NULL DEFAULT 0,
    event     TEXT    NOT NULL,
    code_id   INTEGER NOT NULL DEFAULT 0,
    device_id TEXT    NOT NULL DEFAULT '',
    ip        TEXT    NOT NULL DEFAULT '',
    detail    TEXT    NOT NULL DEFAULT ''
);
SQL
    );
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_logs_ts ON logs(ts DESC)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_tokens_code ON tokens(code_id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_tickets_expire ON tickets(expire_at)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_releases_sort ON releases(channel, status, sort_key DESC)');
    $pdo->exec('PRAGMA user_version = 1');
}

function db_one(string $sql, array $params = []): ?array
{
    $st = db()->prepare($sql);
    $st->execute($params);
    $row = $st->fetch();
    return $row === false ? null : $row;
}

function db_all(string $sql, array $params = []): array
{
    $st = db()->prepare($sql);
    $st->execute($params);
    return $st->fetchAll();
}

function db_exec(string $sql, array $params = []): int
{
    $st = db()->prepare($sql);
    $st->execute($params);
    return $st->rowCount();
}

function json_out(array $payload, int $status = 200): void
{
    if (!headers_sent()) {
        http_response_code($status);
        header('Content-Type: application/json; charset=utf-8');
        header('Cache-Control: no-store');
        header('X-Content-Type-Options: nosniff');
    }
    echo json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

function json_ok(array $data = []): void
{
    json_out(['ok' => true, 'data' => $data]);
}

function json_fail(string $code, string $message, int $status = 400, array $extra = []): void
{
    json_out(['ok' => false, 'error' => array_merge(['code' => $code, 'message' => $message], $extra)], $status);
}

/** 客户端 IP（兼容常见反代头） */
function client_ip(): string
{
    foreach (['HTTP_CF_CONNECTING_IP', 'HTTP_X_FORWARDED_FOR', 'HTTP_X_REAL_IP', 'REMOTE_ADDR'] as $key) {
        if (!empty($_SERVER[$key])) {
            $ip = trim(explode(',', (string) $_SERVER[$key])[0]);
            if (filter_var($ip, FILTER_VALIDATE_IP) !== false) {
                return $ip;
            }
        }
    }
    return '';
}

/** 推断站点根地址，用于拼下载链接 */
function base_url(): string
{
    $configured = trim((string) cfg('base_url', ''));
    if ($configured !== '') {
        return rtrim($configured, '/');
    }
    $https = (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off')
        || (($_SERVER['HTTP_X_FORWARDED_PROTO'] ?? '') === 'https')
        || (($_SERVER['SERVER_PORT'] ?? '') === '443');
    $host = $_SERVER['HTTP_HOST'] ?? 'localhost';
    $dir = rtrim(str_replace('\\', '/', dirname($_SERVER['SCRIPT_NAME'] ?? '/')), '/');
    if ($dir === '.' || $dir === '/') {
        $dir = '';
    }
    return ($https ? 'https://' : 'http://') . $host . $dir;
}

function log_event(string $event, array $context = []): void
{
    if (!cfg('enable_log', true)) {
        return;
    }
    try {
        db_exec(
            'INSERT INTO logs (ts, event, code_id, device_id, ip, detail) VALUES (?, ?, ?, ?, ?, ?)',
            [
                time(),
                $event,
                (int) ($context['code_id'] ?? 0),
                (string) ($context['device_id'] ?? ''),
                client_ip(),
                (string) ($context['detail'] ?? ''),
            ]
        );
    } catch (Throwable $e) {
        // 日志失败不能影响主流程
    }
}

/** 把 "2.19.0.0" 变成可排序的 "0002.0019.0000.0000" */
function version_sort_key(string $version): string
{
    $parts = preg_split('/[.\-+]/', trim($version)) ?: [];
    $parts = array_slice(array_pad($parts, 4, '0'), 0, 4);
    $out = [];
    foreach ($parts as $part) {
        $n = preg_replace('/\D/', '', $part);
        $out[] = str_pad($n === '' ? '0' : $n, 4, '0', STR_PAD_LEFT);
    }
    return implode('.', $out);
}

/** 清理过期令牌、票据与旧日志，顺手调用即可，不必精确 */
function gc_expired(): void
{
    $now = time();
    try {
        db_exec('DELETE FROM tickets WHERE expire_at < ?', [$now - 3600]);
        db_exec('DELETE FROM tokens WHERE expire_at < ?', [$now - 30 * 86400]);
        // 日志只留 90 天，避免虚拟主机上的 SQLite 无限膨胀
        db_exec('DELETE FROM logs WHERE ts < ?', [$now - 90 * 86400]);
    } catch (Throwable $e) {
        // 忽略
    }
}
