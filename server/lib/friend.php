<?php
/**
 * 好友服务：账号、邀请码、邮箱验证、好友关系、群组、消息与历史记录。
 *
 * 设计目标：
 * - 与 RMCL 更新服务共用一个 SQLite 与单入口，部署成本为零；
 * - 腾讯云 IM 只承担实时转发，所有聊天记录都在这里落库（可恢复、可检索）；
 * - 客户端只做增量拉取（since_id），单次轮询即可拿到全部会话的新消息；
 * - 所有写接口都做归属校验，令牌只存哈希。
 */

declare(strict_types=1);

require_once __DIR__ . '/tencent_im.php';

// ---------------- 基础设施 ----------------

/** 建表。用 friend_meta 记录自己的结构版本，避免污染 core 的 user_version */
function friend_tables(): void
{
    static $done = false;
    if ($done) {
        return;
    }
    $done = true;
    $pdo = db();

    // 已经建过表就直接返回，避免每个请求都跑一遍 CREATE TABLE
    $version = 0;
    try {
        $row = db_one("SELECT v FROM friend_meta WHERE k = 'schema'");
        if ($row !== null) {
            $version = (int) $row['v'];
        }
    } catch (Throwable $e) {
        // friend_meta 还不存在，继续建表
    }
    if ($version >= 2) {
        return;
    }
    if ($version >= 1) {
        // v1 -> v2：补上撤回时间。CREATE TABLE IF NOT EXISTS 不会给旧表加列，必须 ALTER
        try {
            $pdo->exec('ALTER TABLE friend_message ADD COLUMN recalled_at INTEGER NOT NULL DEFAULT 0');
        } catch (Throwable $e) {
            // 列已存在则忽略
        }
        db_exec("INSERT OR REPLACE INTO friend_meta (k, v) VALUES ('schema', '2')");
        return;
    }
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_user (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    email         TEXT    NOT NULL COLLATE NOCASE UNIQUE,
    nickname      TEXT    NOT NULL DEFAULT '',
    pass_hash     TEXT    NOT NULL DEFAULT '',
    invite_code   TEXT    NOT NULL DEFAULT '',
    invited_by    INTEGER NOT NULL DEFAULT 0,
    rayskin_email TEXT    NOT NULL DEFAULT '',
    rayskin_uuid  TEXT    NOT NULL DEFAULT '',
    email_verified INTEGER NOT NULL DEFAULT 0,
    status        TEXT    NOT NULL DEFAULT 'active',
    created_at    INTEGER NOT NULL DEFAULT 0,
    last_seen_at  INTEGER NOT NULL DEFAULT 0,
    last_ip       TEXT    NOT NULL DEFAULT ''
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_invite (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    code       TEXT    NOT NULL UNIQUE,
    created_by INTEGER NOT NULL DEFAULT 0,
    note       TEXT    NOT NULL DEFAULT '',
    max_uses   INTEGER NOT NULL DEFAULT 1,
    used       INTEGER NOT NULL DEFAULT 0,
    expire_at  INTEGER NOT NULL DEFAULT 0,
    disabled   INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_token (
    token_hash TEXT    PRIMARY KEY,
    user_id    INTEGER NOT NULL,
    device     TEXT    NOT NULL DEFAULT '',
    created_at INTEGER NOT NULL DEFAULT 0,
    expire_at  INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_code (
    email      TEXT    NOT NULL,
    purpose    TEXT    NOT NULL,
    code_hash  TEXT    NOT NULL,
    tries      INTEGER NOT NULL DEFAULT 0,
    created_at INTEGER NOT NULL DEFAULT 0,
    expire_at  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (email, purpose)
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_relation (
    user_id    INTEGER NOT NULL,
    friend_id  INTEGER NOT NULL,
    created_at INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, friend_id)
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_request (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    from_id    INTEGER NOT NULL,
    to_id      INTEGER NOT NULL,
    message    TEXT    NOT NULL DEFAULT '',
    state      TEXT    NOT NULL DEFAULT 'pending',
    created_at INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_group (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT    NOT NULL DEFAULT '',
    owner_id    INTEGER NOT NULL DEFAULT 0,
    notice      TEXT    NOT NULL DEFAULT '',
    im_group_id TEXT    NOT NULL DEFAULT '',
    created_at  INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_group_member (
    group_id  INTEGER NOT NULL,
    user_id   INTEGER NOT NULL,
    role      INTEGER NOT NULL DEFAULT 0,
    joined_at INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (group_id, user_id)
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_message (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    conv_type  INTEGER NOT NULL DEFAULT 0,
    conv_key   TEXT    NOT NULL DEFAULT '',
    from_id    INTEGER NOT NULL DEFAULT 0,
    to_id      INTEGER NOT NULL DEFAULT 0,
    group_id   INTEGER NOT NULL DEFAULT 0,
    body       TEXT    NOT NULL DEFAULT '',
    kind       TEXT    NOT NULL DEFAULT 'text',
    client_id  TEXT    NOT NULL DEFAULT '',
    created_at INTEGER NOT NULL DEFAULT 0,
    im_msg_id  TEXT    NOT NULL DEFAULT '',
    recalled_at INTEGER NOT NULL DEFAULT 0
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_conversation (
    user_id    INTEGER NOT NULL,
    conv_type  INTEGER NOT NULL DEFAULT 0,
    conv_key   TEXT    NOT NULL DEFAULT '',
    title      TEXT    NOT NULL DEFAULT '',
    last_body  TEXT    NOT NULL DEFAULT '',
    last_at    INTEGER NOT NULL DEFAULT 0,
    unread     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, conv_type, conv_key)
);
SQL
    );
    $pdo->exec(<<<'SQL'
CREATE TABLE IF NOT EXISTS friend_meta (
    k TEXT PRIMARY KEY,
    v TEXT NOT NULL DEFAULT ''
);
SQL
    );
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_msg_conv ON friend_message(conv_type, conv_key, id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_msg_to ON friend_message(to_id, id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_msg_from ON friend_message(from_id, id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_msg_group ON friend_message(group_id, id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_token_user ON friend_token(user_id)');
    $pdo->exec('CREATE INDEX IF NOT EXISTS idx_friend_req_to ON friend_request(to_id, state)');
    db_exec("INSERT OR REPLACE INTO friend_meta (k, v) VALUES ('schema', '2')");
}

function friend_ok(array $data = []): void
{
    json_out(['ok' => true, 'data' => $data]);
}

/** 业务错误统一返回 200，客户端只看 ok 字段，避免各平台对 4xx 响应体处理不一致 */
function friend_err(string $code, string $message): void
{
    json_out(['ok' => false, 'error' => ['code' => $code, 'message' => $message]]);
}

function friend_now(): int
{
    return time();
}

function friend_ip(): string
{
    return client_ip();
}

/** 归一化邮箱 */
function friend_email(string $email): string
{
    return strtolower(trim($email));
}

/** 生成随机串（不含易混字符） */
function friend_rand(int $length = 8, string $alphabet = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ'): string
{
    $max = strlen($alphabet) - 1;
    $out = '';
    for ($i = 0; $i < $length; $i++) {
        $out .= $alphabet[random_int(0, $max)];
    }
    return $out;
}

/** 生成对外展示的邀请码 */
function friend_invite_code(): string
{
    return 'RMCL-' . friend_rand(4) . '-' . friend_rand(4);
}

/** 用户信息的公开形态（绝不返回 pass_hash） */
function friend_public(array $u): array
{
    // 必须按“个人邀请码”精确查询：按 created_by 取最新一条会拿到别的码，导致用量永远显示 0
    $invite = db_one(
        'SELECT max_uses, used, disabled, expire_at FROM friend_invite WHERE code = ? LIMIT 1',
        [(string) $u['invite_code']]
    );
    return [
        'id' => (int) $u['id'],
        'email' => (string) $u['email'],
        'nickname' => (string) $u['nickname'],
        'invite_code' => (string) $u['invite_code'],
        'invite_used' => $invite === null ? 0 : (int) $invite['used'],
        'invite_max' => $invite === null ? 0 : (int) $invite['max_uses'],
        'invite_disabled' => $invite !== null && (int) $invite['disabled'] === 1,
        'invite_expire_at' => $invite === null ? 0 : (int) $invite['expire_at'],
        'rayskin_bound' => (string) $u['rayskin_uuid'] !== '',
        'created_at' => (int) $u['created_at'],
    ];
}

// ------------------------------------------

// ---------------- 令牌与鉴权 ----------------

function friend_token_issue(int $userId, string $device): string
{
    $token = bin2hex(random_bytes(32));
    $days = (int) friend_config('token_days', 30);
    db_exec(
        'INSERT INTO friend_token (token_hash, user_id, device, created_at, expire_at) VALUES (?, ?, ?, ?, ?)',
        [hash('sha256', $token), $userId, mb_substr($device, 0, 64), friend_now(), friend_now() + max(1, $days) * 86400]
    );
    // 顺手清掉过期令牌，避免表无限增长
    db_exec('DELETE FROM friend_token WHERE expire_at < ?', [friend_now()]);
    return $token;
}

function friend_http_token(): string
{
    $token = trim((string) ($_SERVER['HTTP_X_FRIEND_TOKEN'] ?? ''));
    if ($token !== '') {
        return $token;
    }
    return trim((string) input('token', ''));
}

/** 当前登录用户；失败直接输出错误并结束请求 */
function friend_auth(): array
{
    $token = friend_http_token();
    if ($token === '') {
        friend_err('E_TOKEN', '尚未登录');
    }
    $row = db_one(
        'SELECT u.* FROM friend_token t JOIN friend_user u ON u.id = t.user_id
         WHERE t.token_hash = ? AND t.expire_at > ? LIMIT 1',
        [hash('sha256', $token), friend_now()]
    );
    if ($row === null) {
        friend_err('E_TOKEN', '登录已失效，请重新登录');
    }
    if ((string) $row['status'] !== 'active') {
        friend_err('E_USER', '该账号已被停用，请联系管理员');
    }
    db_exec('UPDATE friend_user SET last_seen_at = ?, last_ip = ? WHERE id = ?', [friend_now(), friend_ip(), (int) $row['id']]);
    return $row;
}

// ------------------------------------------

// ---------------- 邮箱验证码 ----------------

/**
 * 读取 SMTP 配置。凭据放在 server/config.smtp.php（不进版本库），
 * 文件不存在时回退到 PHP mail()。
 */
function friend_smtp_config(): ?array
{
    static $loaded = false;
    static $config = null;
    if ($loaded) {
        return $config;
    }
    $loaded = true;
    $file = dirname(__DIR__) . '/config.smtp.php';
    if (!is_file($file)) {
        return null;
    }
    $data = require $file;
    if (!is_array($data) || empty($data['host']) || empty($data['from'])) {
        return null;
    }
    $config = $data;
    return $config;
}

/** 读取一行 SMTP 响应（支持多行 250- 形式） */
function friend_smtp_read($fp): string
{
    $result = '';
    while (($line = fgets($fp, 1024)) !== false) {
        $result .= $line;
        if (strlen($line) < 4 || $line[3] === ' ') {
            break;
        }
    }
    return $result;
}

function friend_smtp_cmd($fp, string $command): string
{
    fwrite($fp, $command . "\r\n");
    return friend_smtp_read($fp);
}

function friend_smtp_ok(string $response, string $code): bool
{
    return substr(ltrim($response), 0, 3) === $code;
}

/**
 * 通过 SMTP 直接投递。支持 ssl(465) / starttls(587) / 明文。
 * 不依赖 Composer，也不依赖主机 mail()。
 */
function friend_smtp_send(string $to, string $subject, string $body): bool
{
    $conf = friend_smtp_config();
    if ($conf === null) {
        return false;
    }
    $host = (string) $conf['host'];
    $port = (int) ($conf['port'] ?? 465);
    $encryption = strtolower((string) ($conf['encryption'] ?? 'ssl'));
    $timeout = (int) ($conf['timeout'] ?? 15);
    $remote = ($encryption === 'ssl' ? 'ssl://' : 'tcp://') . $host . ':' . $port;

    $context = stream_context_create([
        'ssl' => [
            'verify_peer' => true,
            'verify_peer_name' => true,
            'allow_self_signed' => false,
        ],
    ]);
    $errno = 0;
    $errstr = '';
    $fp = @stream_socket_client($remote, $errno, $errstr, $timeout, STREAM_CLIENT_CONNECT, $context);
    if ($fp === false) {
        error_log('[rmcl-friend] SMTP connect failed: ' . $errstr);
        return false;
    }
    stream_set_timeout($fp, $timeout);

    try {
        if (!friend_smtp_ok(friend_smtp_read($fp), '220')) {
            throw new RuntimeException('SMTP greeting failed');
        }
        $helo = 'EHLO ' . ($conf['helo'] ?? 'localhost');
        $response = friend_smtp_cmd($fp, $helo);
        if (!friend_smtp_ok($response, '250')) {
            throw new RuntimeException('SMTP EHLO failed: ' . trim($response));
        }
        if ($encryption === 'tls') {
            if (!friend_smtp_ok(friend_smtp_cmd($fp, 'STARTTLS'), '220')) {
                throw new RuntimeException('SMTP STARTTLS failed');
            }
            if (!stream_socket_enable_crypto($fp, true, STREAM_CRYPTO_METHOD_TLS_CLIENT)) {
                throw new RuntimeException('SMTP TLS handshake failed');
            }
            $response = friend_smtp_cmd($fp, $helo);
            if (!friend_smtp_ok($response, '250')) {
                throw new RuntimeException('SMTP EHLO after TLS failed');
            }
        }
        if (!empty($conf['user'])) {
            if (!friend_smtp_ok(friend_smtp_cmd($fp, 'AUTH LOGIN'), '334')) {
                throw new RuntimeException('SMTP AUTH not accepted');
            }
            if (!friend_smtp_ok(friend_smtp_cmd($fp, base64_encode((string) $conf['user'])), '334')) {
                throw new RuntimeException('SMTP username rejected');
            }
            if (!friend_smtp_ok(friend_smtp_cmd($fp, base64_encode((string) $conf['pass'])), '235')) {
                throw new RuntimeException('SMTP password rejected');
            }
        }

        $from = (string) $conf['from'];
        $fromName = (string) ($conf['from_name'] ?? 'RMCL');
        if (!friend_smtp_ok(friend_smtp_cmd($fp, 'MAIL FROM:<' . $from . '>'), '250')) {
            throw new RuntimeException('SMTP MAIL FROM rejected');
        }
        if (!friend_smtp_ok(friend_smtp_cmd($fp, 'RCPT TO:<' . $to . '>'), '250')) {
            throw new RuntimeException('SMTP RCPT TO rejected');
        }
        if (!friend_smtp_ok(friend_smtp_cmd($fp, 'DATA'), '354')) {
            throw new RuntimeException('SMTP DATA rejected');
        }

        $encodedSubject = '=?UTF-8?B?' . base64_encode($subject) . '?=';
        $encodedFrom = '=?UTF-8?B?' . base64_encode($fromName) . '?= <' . $from . '>';
        $headers = 'Date: ' . date('r') . "\r\n"
                 . 'From: ' . $encodedFrom . "\r\n"
                 . 'To: <' . $to . ">\r\n"
                 . 'Subject: ' . $encodedSubject . "\r\n"
                 . 'Message-ID: <' . bin2hex(random_bytes(12)) . '@' . $host . ">\r\n"
                 . "MIME-Version: 1.0\r\n"
                 . "Content-Type: text/plain; charset=UTF-8\r\n"
                 . "Content-Transfer-Encoding: base64\r\n";
        $payload = $headers . "\r\n" . chunk_split(base64_encode($body), 76, "\r\n");
        // 行首的点需要转义，否则会被当作结束标记
        $payload = preg_replace('/^\./m', '..', $payload);
        fwrite($fp, $payload . "\r\n.\r\n");
        $result = friend_smtp_read($fp);
        friend_smtp_cmd($fp, 'QUIT');
        fclose($fp);
        if (!friend_smtp_ok($result, '250')) {
            throw new RuntimeException('SMTP DATA not accepted: ' . trim($result));
        }
        return true;
    } catch (Throwable $e) {
        @fclose($fp);
        error_log('[rmcl-friend] SMTP send failed: ' . $e->getMessage());
        return false;
    }
}

function friend_mail_send(string $email, string $code, string $purpose): bool
{
    $subject = (string) friend_config('email_subject', 'RMCL 好友功能验证码');
    $appName = (string) cfg('app_name', 'RMCL');
    $minutes = (int) friend_config('code_minutes', 10);
    $body = "你正在{$appName}中{$purpose}，验证码是：{$code}\r\n"
          . "{$minutes} 分钟内有效。如果这不是你本人的操作，请忽略这封邮件。\r\n";
    $from = (string) friend_config('email_from', 'noreply@localhost');
    $headers = "From: {$from}\r\n"
             . "MIME-Version: 1.0\r\n"
             . "Content-Type: text/plain; charset=UTF-8\r\n"
             . "Content-Transfer-Encoding: 8bit\r\n";
    $encodedSubject = '=?UTF-8?B?' . base64_encode($subject) . '?=';

    try {
        @file_put_contents(
            data_dir() . '/friend-mail.log',
            '[' . date('Y-m-d H:i:s') . "] to={$email} code={$code} purpose={$purpose}\n",
            FILE_APPEND
        );
    } catch (Throwable $e) {
        // 日志失败不影响发送
    }

    if (!friend_config('email_enabled', true)) {
        return false;
    }
    if (friend_smtp_config() !== null) {
        return friend_smtp_send($email, $subject, $body);
    }
    return @mail($email, $encodedSubject, $body, $headers);
}

function friend_send_code(string $email, string $purpose): void
{
    $email = friend_email($email);
    if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
        friend_err('E_EMAIL', '邮箱格式不正确');
    }
    $purpose = $purpose === 'reset' ? 'reset' : 'register';

    if ($purpose === 'register' && db_one('SELECT id FROM friend_user WHERE email = ?', [$email]) !== null) {
        friend_err('E_EMAIL_USED', '这个邮箱已经注册过了，请直接登录');
    }

    $existing = db_one('SELECT created_at FROM friend_code WHERE email = ? AND purpose = ?', [$email, $purpose]);
    if ($existing !== null && friend_now() - (int) $existing['created_at'] < 60) {
        friend_err('E_RATE', '验证码发送太频繁，请稍后再试');
    }

    $code = (string) random_int(100000, 999999);
    $expire = friend_now() + max(3, (int) friend_config('code_minutes', 10)) * 60;
    db_exec(
        'INSERT INTO friend_code (email, purpose, code_hash, tries, created_at, expire_at) VALUES (?, ?, ?, 0, ?, ?)
         ON CONFLICT(email, purpose) DO UPDATE SET code_hash = excluded.code_hash, tries = 0,
             created_at = excluded.created_at, expire_at = excluded.expire_at',
        [$email, $purpose, password_hash($code, PASSWORD_DEFAULT), friend_now(), $expire]
    );

    $sent = friend_mail_send($email, $code, $purpose === 'register' ? '注册好友账号' : '重置密码');
    $debug = (bool) friend_config('debug_code', false) || !friend_config('email_enabled', true);
    if (!$sent && !$debug) {
        // 邮件没发出去就不要让用户干等：直接报错，管理员去查 SMTP / mail()
        friend_err('E_MAIL', '验证码邮件发送失败，请联系管理员检查邮件服务配置');
    }
    friend_ok([
        'sent' => $sent,
        'debug' => $debug,
        'dev_code' => $debug ? $code : null,
        'expire_in' => max(3, (int) friend_config('code_minutes', 10)) * 60,
    ]);
}

function friend_check_code(string $email, string $purpose, string $code): void
{
    $row = db_one('SELECT * FROM friend_code WHERE email = ? AND purpose = ?', [$email, $purpose]);
    if ($row === null || (int) $row['expire_at'] < friend_now()) {
        friend_err('E_CODE', '验证码已过期，请重新发送');
    }
    if ((int) $row['tries'] >= 6) {
        friend_err('E_CODE', '验证码尝试次数过多，请重新发送');
    }
    db_exec('UPDATE friend_code SET tries = tries + 1 WHERE email = ? AND purpose = ?', [$email, $purpose]);
    if (!password_verify($code, (string) $row['code_hash'])) {
        friend_err('E_CODE', '验证码不正确');
    }
    db_exec('DELETE FROM friend_code WHERE email = ? AND purpose = ?', [$email, $purpose]);
}

// ------------------------------------------

// ---------------- 注册 / 登录 / RaySkin ----------------

function friend_register(): void
{
    $email = friend_email((string) input('email'));
    $code = trim((string) input('code'));
    $nickname = trim((string) input('nickname'));
    $password = (string) input('password');
    $invite = strtoupper(trim((string) input('invite')));
    $device = (string) input('device');

    if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
        friend_err('E_EMAIL', '邮箱格式不正确');
    }
    if ($nickname === '' || mb_strlen($nickname) < 2 || mb_strlen($nickname) > 16) {
        friend_err('E_NAME', '昵称需要 2-16 个字符');
    }
    if (strlen($password) < 6) {
        friend_err('E_PASS', '密码至少 6 位');
    }
    if (db_one('SELECT id FROM friend_user WHERE email = ?', [$email]) !== null) {
        friend_err('E_EMAIL_USED', '这个邮箱已经注册过了');
    }

    $inviterId = 0;
    $inviteValid = false;
    if (friend_config('require_invite', true)) {
        $inviteRow = db_one('SELECT * FROM friend_invite WHERE code = ?', [$invite]);
        if ($inviteRow === null) {
            friend_err('E_INVITE', '邀请码无效');
        }
        if ((int) $inviteRow['disabled'] === 1) {
            friend_err('E_INVITE', '邀请码已被停用');
        }
        if ((int) $inviteRow['max_uses'] > 0 && (int) $inviteRow['used'] >= (int) $inviteRow['max_uses']) {
            friend_err('E_INVITE', '邀请码使用次数已用完');
        }
        if ((int) $inviteRow['expire_at'] > 0 && (int) $inviteRow['expire_at'] < friend_now()) {
            friend_err('E_INVITE', '邀请码已过期');
        }
        $inviterId = (int) $inviteRow['created_by'];
        $inviteValid = true;
    }

    friend_check_code($email, 'register', $code);

    $myInvite = friend_invite_code();
    db_exec(
        'INSERT INTO friend_user (email, nickname, pass_hash, invite_code, invited_by, email_verified, created_at)
         VALUES (?, ?, ?, ?, ?, 1, ?)',
        [$email, $nickname, password_hash($password, PASSWORD_DEFAULT), $myInvite, $inviterId, friend_now()]
    );
    $userId = (int) db()->lastInsertId();

    if ($inviteValid) {
        db_exec('UPDATE friend_invite SET used = used + 1 WHERE code = ?', [$invite]);
    }
    // 注册即赠送一个可分享的邀请码
    $bonus = max(0, (int) friend_config('register_bonus_invite', 3));
    if ($bonus > 0) {
        db_exec(
            'INSERT OR IGNORE INTO friend_invite (code, created_by, note, max_uses, used, created_at)
             VALUES (?, ?, ?, ?, 0, ?)',
            [$myInvite, $userId, '注册赠送', $bonus, friend_now()]
        );
    }

    tim_account_import($userId, $nickname);
    $user = db_one('SELECT * FROM friend_user WHERE id = ?', [$userId]);
    $token = friend_token_issue($userId, $device);
    log_event('friend.register', ['detail' => $email]);
    friend_ok(['token' => $token, 'user' => friend_public($user)]);
}

function friend_login(): void
{
    $email = friend_email((string) input('email'));
    $password = (string) input('password');
    $device = (string) input('device');

    $user = db_one('SELECT * FROM friend_user WHERE email = ?', [$email]);
    if ($user === null || !password_verify($password, (string) $user['pass_hash'])) {
        usleep(400000); // 轻微延迟，降低撞库效率
        friend_err('E_LOGIN', '邮箱或密码不正确');
    }
    if ((string) $user['status'] !== 'active') {
        friend_err('E_USER', '该账号已被停用，请联系管理员');
    }
    db_exec('UPDATE friend_user SET last_seen_at = ?, last_ip = ? WHERE id = ?', [friend_now(), friend_ip(), (int) $user['id']]);
    tim_account_import((int) $user['id'], (string) $user['nickname']);
    $token = friend_token_issue((int) $user['id'], $device);
    log_event('friend.login', ['detail' => $email]);
    friend_ok(['token' => $token, 'user' => friend_public($user)]);
}

/** 允许调用 hasJoined 的皮肤站域名，避免 SSRF */
function friend_rayskin_allowed(string $server): bool
{
    $host = strtolower((string) parse_url($server, PHP_URL_HOST));
    if ($host === '') {
        return false;
    }
    $allowed = (array) friend_config('rayskin_hosts', ['mc.lxray.top', 'littleskin.cn']);
    foreach ($allowed as $item) {
        $item = strtolower(trim((string) $item));
        if ($item !== '' && ($host === $item || str_ends_with($host, '.' . $item))) {
            return true;
        }
    }
    return false;
}

/**
 * RaySkin 一键登录：
 * 客户端先用 Yggdrasil 的 authenticate + join 证明账号归属，服务端再调用
 * sessionserver/hasJoined 做最终校验。密码全程不经过本服务端。
 */
function friend_rayskin_login(): void
{
    $email = friend_email((string) input('email'));
    $uuid = strtolower(str_replace('-', '', trim((string) input('uuid'))));
    $name = trim((string) input('name'));
    $serverId = trim((string) input('server_id'));
    $accessToken = trim((string) input('access_token'));
    $server = rtrim(trim((string) input('server')), '/');
    $device = (string) input('device');

    if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
        friend_err('E_EMAIL', 'RaySkin 账号的邮箱格式不正确');
    }
    // 两种模式：join + hasJoined（标准），或 accessToken + validate（join 被网络/代理拦截时的兜底）
    if ($uuid === '' || ($serverId === '' && $accessToken === '')) {
        friend_err('E_RAYSKIN', 'RaySkin 返回的数据不完整');
    }
    if (!preg_match('#^https://#i', $server) || !friend_rayskin_allowed($server)) {
        friend_err('E_RAYSKIN', '不支持该认证服务器');
    }

    $profile = null;
    if ($accessToken !== '') {
        if (!friend_config('rayskin_allow_token', true)) {
            friend_err('E_RAYSKIN', '当前服务端未启用 accessToken 校验，请在『启动』页登录后重试');
        }
        // 兜底模式：先用 validate 确认令牌有效（204 表示有效，401/403 表示失效）
        [$status, $body] = friend_http_request_ex(
            $server . '/authserver/validate',
            'POST',
            json_encode(['accessToken' => $accessToken], JSON_UNESCAPED_SLASHES),
            8
        );
        if ($status === 401 || $status === 403) {
            friend_err('E_TOKEN_INVALID', 'RaySkin 登录状态已失效，请重新登录');
        }
        if ($status !== 204) {
            friend_err('E_RAYSKIN', "RaySkin 校验失败（HTTP {$status}）");
        }
        // 再用 profile 确认这个令牌确实属于提交的 UUID
        [$pStatus, $pBody] = friend_http_request_ex(
            $server . '/sessionserver/session/minecraft/profile/' . rawurlencode($uuid),
            'GET',
            null,
            8
        );
        $profile = $pStatus === 200 ? json_decode((string) $pBody, true) : null;
        if (!is_array($profile) || empty($profile['id'])) {
            friend_err('E_RAYSKIN', 'RaySkin 校验失败，请到启动页重新登录后再试');
        }
        if (!empty($profile['name'])) {
            $name = trim((string) $profile['name']);
        }
    } else {
        if ($name === '') {
            friend_err('E_RAYSKIN', 'RaySkin 返回的数据不完整');
        }
        $verifyUrl = $server . '/sessionserver/session/minecraft/hasJoined?username=' . rawurlencode($name)
                   . '&serverId=' . rawurlencode($serverId);
        $raw = friend_http_request($verifyUrl, 'GET', null, 8);
        $profile = $raw === null ? null : json_decode($raw, true);
        if (!is_array($profile) || empty($profile['id'])) {
            friend_err('E_RAYSKIN', 'RaySkin 校验失败，请到启动页重新登录后再试');
        }
    }

    $verifiedUuid = strtolower(str_replace('-', '', (string) $profile['id']));
    if ($verifiedUuid !== $uuid) {
        friend_err('E_RAYSKIN', 'RaySkin 校验结果与提交的账号不一致');
    }
    if ($name === '') {
        $name = (string) ($profile['name'] ?? '');
    }

    $user = db_one('SELECT * FROM friend_user WHERE rayskin_uuid = ? OR email = ? ORDER BY id LIMIT 1', [$verifiedUuid, $email]);
    if ($user === null) {
        if (!friend_config('rayskin_auto_register', true)) {
            friend_err('E_RAYSKIN', '这个 RaySkin 账号还没有绑定好友账号，请先用邮箱注册');
        }
        $nickname = mb_substr($name, 0, 16);
        if (mb_strlen($nickname) < 2) {
            $nickname = '玩家' . friend_rand(4);
        }
        $myInvite = friend_invite_code();
        db_exec(
            'INSERT INTO friend_user (email, nickname, pass_hash, invite_code, rayskin_email, rayskin_uuid, email_verified, created_at)
             VALUES (?, ?, ?, ?, ?, ?, 1, ?)',
            [$email, $nickname, password_hash(bin2hex(random_bytes(16)), PASSWORD_DEFAULT), $myInvite, $email, $verifiedUuid, friend_now()]
        );
        $user = db_one('SELECT * FROM friend_user WHERE id = ?', [(int) db()->lastInsertId()]);
        $bonus = max(0, (int) friend_config('register_bonus_invite', 3));
        if ($bonus > 0) {
            db_exec(
                'INSERT OR IGNORE INTO friend_invite (code, created_by, note, max_uses, used, created_at) VALUES (?, ?, ?, ?, 0, ?)',
                [$myInvite, (int) $user['id'], 'RaySkin 注册赠送', $bonus, friend_now()]
            );
        }
    } else {
        db_exec(
            'UPDATE friend_user SET rayskin_email = ?, rayskin_uuid = ?, email_verified = 1, last_seen_at = ? WHERE id = ?',
            [$email, $verifiedUuid, friend_now(), (int) $user['id']]
        );
        $user = db_one('SELECT * FROM friend_user WHERE id = ?', [(int) $user['id']]);
    }
    if ((string) $user['status'] !== 'active') {
        friend_err('E_USER', '该账号已被停用，请联系管理员');
    }

    tim_account_import((int) $user['id'], (string) $user['nickname']);
    $token = friend_token_issue((int) $user['id'], $device);
    log_event('friend.rayskin', ['detail' => $email]);
    friend_ok(['token' => $token, 'user' => friend_public($user)]);
}

// ------------------------------------------

// ---------------- 好友与通讯录 ----------------

function friend_relation_exists(int $a, int $b): bool
{
    return db_one('SELECT 1 FROM friend_relation WHERE user_id = ? AND friend_id = ?', [$a, $b]) !== null;
}

function friend_add_relation(int $a, int $b): void
{
    db_exec('INSERT OR IGNORE INTO friend_relation (user_id, friend_id, created_at) VALUES (?, ?, ?)', [$a, $b, friend_now()]);
    db_exec('INSERT OR IGNORE INTO friend_relation (user_id, friend_id, created_at) VALUES (?, ?, ?)', [$b, $a, friend_now()]);
    friend_touch_conversation($a, 0, (string) $b);
    friend_touch_conversation($b, 0, (string) $a);
}

/** 确保会话存在，并写入正确的标题（不改变最后一条消息） */
function friend_touch_conversation(int $userId, int $convType, string $convKey): void
{
    if ($convType === 0) {
        $other = db_one('SELECT id, nickname FROM friend_user WHERE id = ?', [(int) $convKey]);
        $title = $other === null ? '未知用户' : (string) $other['nickname'];
    } else {
        $group = db_one('SELECT name FROM friend_group WHERE id = ?', [(int) $convKey]);
        $title = $group === null ? '群聊' : (string) $group['name'];
    }
    db_exec(
        'INSERT INTO friend_conversation (user_id, conv_type, conv_key, title, last_body, last_at, unread)
         VALUES (?, ?, ?, ?, ?, 0, 0)
         ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET title = excluded.title',
        [$userId, $convType, $convKey, $title, '']
    );
}

function friend_search(): void
{
    $me = friend_auth();
    $keyword = trim((string) input('keyword'));
    if ($keyword === '') {
        friend_ok(['items' => []]);
    }
    $like = '%' . str_replace(['%', '_'], ['\%', '\_'], $keyword) . '%';
    $rows = db_all(
        "SELECT * FROM friend_user WHERE status = 'active' AND id <> ? AND (email LIKE ? ESCAPE '\\' OR nickname LIKE ? ESCAPE '\\')
         ORDER BY last_seen_at DESC LIMIT 20",
        [(int) $me['id'], $like, $like]
    );
    $items = [];
    foreach ($rows as $row) {
        $relation = '';
        if (friend_relation_exists((int) $me['id'], (int) $row['id'])) {
            $relation = 'friend';
        } elseif (db_one('SELECT 1 FROM friend_request WHERE from_id = ? AND to_id = ? AND state = ?', [(int) $me['id'], (int) $row['id'], 'pending']) !== null) {
            $relation = 'pending';
        }
        $items[] = [
            'id' => (int) $row['id'],
            'email' => (string) $row['email'],
            'nickname' => (string) $row['nickname'],
            'relation' => $relation,
        ];
    }
    friend_ok(['items' => $items]);
}

function friend_add(): void
{
    $me = friend_auth();
    $target = (int) input('user_id', 0);
    $message = mb_substr(trim((string) input('message')), 0, 60);
    if ($target <= 0 || $target === (int) $me['id']) {
        friend_err('E_PARAM', '目标用户无效');
    }
    $user = db_one('SELECT id, status FROM friend_user WHERE id = ?', [$target]);
    if ($user === null || (string) $user['status'] !== 'active') {
        friend_err('E_USER', '该用户不存在或已停用');
    }
    if (friend_relation_exists((int) $me['id'], $target)) {
        friend_err('E_FRIEND', '你们已经是好友了');
    }
    // 对方先发过请求就直接成为好友
    $reverse = db_one('SELECT * FROM friend_request WHERE from_id = ? AND to_id = ? AND state = ?', [$target, (int) $me['id'], 'pending']);
    if ($reverse !== null) {
        db_exec('UPDATE friend_request SET state = ?, updated_at = ? WHERE id = ?', ['accepted', friend_now(), (int) $reverse['id']]);
        friend_add_relation((int) $me['id'], $target);
        friend_ok(['accepted' => true]);
    }
    if (db_one('SELECT 1 FROM friend_request WHERE from_id = ? AND to_id = ? AND state = ?', [(int) $me['id'], $target, 'pending']) !== null) {
        friend_err('E_PENDING', '已经发送过好友请求了');
    }
    db_exec(
        'INSERT INTO friend_request (from_id, to_id, message, state, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)',
        [(int) $me['id'], $target, $message, 'pending', friend_now(), friend_now()]
    );
    friend_ok(['requested' => true]);
}

function friend_requests(): void
{
    $me = friend_auth();
    $rows = db_all(
        'SELECT r.id, r.from_id, r.message, r.created_at, u.nickname AS from_name, u.email AS from_email
         FROM friend_request r JOIN friend_user u ON u.id = r.from_id
         WHERE r.to_id = ? AND r.state = ? ORDER BY r.id DESC LIMIT 50',
        [(int) $me['id'], 'pending']
    );
    $items = [];
    foreach ($rows as $row) {
        $items[] = [
            'id' => (int) $row['id'],
            'from_id' => (int) $row['from_id'],
            'from_name' => (string) $row['from_name'],
            'from_email' => (string) $row['from_email'],
            'message' => (string) $row['message'],
            'created_at' => (int) $row['created_at'],
        ];
    }
    friend_ok(['items' => $items]);
}

function friend_answer_request(bool $accept): void
{
    $me = friend_auth();
    $requestId = (int) input('request_id', 0);
    $row = db_one('SELECT * FROM friend_request WHERE id = ? AND to_id = ? AND state = ?', [$requestId, (int) $me['id'], 'pending']);
    if ($row === null) {
        friend_err('E_PARAM', '好友请求不存在或已处理');
    }
    db_exec('UPDATE friend_request SET state = ?, updated_at = ? WHERE id = ?', [$accept ? 'accepted' : 'rejected', friend_now(), $requestId]);
    if ($accept) {
        friend_add_relation((int) $me['id'], (int) $row['from_id']);
    }
    friend_ok(['accepted' => $accept]);
}

function friend_delete_relation(): void
{
    $me = friend_auth();
    $target = (int) input('user_id', 0);
    db_exec('DELETE FROM friend_relation WHERE (user_id = ? AND friend_id = ?) OR (user_id = ? AND friend_id = ?)',
        [(int) $me['id'], $target, $target, (int) $me['id']]);
    friend_ok([]);
}

function friend_contacts(): void
{
    $me = friend_auth();
    $rows = db_all(
        'SELECT u.id, u.email, u.nickname, u.last_seen_at FROM friend_relation r JOIN friend_user u ON u.id = r.friend_id
         WHERE r.user_id = ? AND u.status = ? ORDER BY u.nickname COLLATE NOCASE',
        [(int) $me['id'], 'active']
    );
    $items = [];
    foreach ($rows as $row) {
        $items[] = [
            'id' => (int) $row['id'],
            'email' => (string) $row['email'],
            'nickname' => (string) $row['nickname'],
            'remark' => '',
            'relation' => 'friend',
            'last_seen_at' => (int) $row['last_seen_at'],
            'online' => friend_now() - (int) $row['last_seen_at'] <= 120,
        ];
    }
    friend_ok(['items' => $items]);
}

// ------------------------------------------

// ---------------- 群组 ----------------

function friend_group_create(): void
{
    $me = friend_auth();
    $name = trim((string) input('name'));
    if (mb_strlen($name) < 2 || mb_strlen($name) > 24) {
        friend_err('E_NAME', '群名称需要 2-24 个字符');
    }
    $rawMembers = input('members', []);
    $members = [];
    if (is_array($rawMembers)) {
        foreach ($rawMembers as $id) {
            $id = (int) $id;
            if ($id > 0 && $id !== (int) $me['id'] && friend_relation_exists((int) $me['id'], $id)) {
                $members[$id] = true;
            }
        }
    }
    if (count($members) === 0) {
        friend_err('E_PARAM', '至少选择一位好友');
    }

    db_exec('INSERT INTO friend_group (name, owner_id, created_at) VALUES (?, ?, ?)', [$name, (int) $me['id'], friend_now()]);
    $groupId = (int) db()->lastInsertId();
    $all = array_keys($members);
    $all[] = (int) $me['id'];
    foreach (array_unique($all) as $uid) {
        db_exec('INSERT OR IGNORE INTO friend_group_member (group_id, user_id, role, joined_at) VALUES (?, ?, ?, ?)',
            [$groupId, (int) $uid, (int) $uid === (int) $me['id'] ? 1 : 0, friend_now()]);
        friend_touch_conversation((int) $uid, 1, (string) $groupId);
    }

    $imGroupId = tim_create_group($name, (int) $me['id'], array_keys($members));
    if ($imGroupId !== '') {
        db_exec('UPDATE friend_group SET im_group_id = ? WHERE id = ?', [$imGroupId, $groupId]);
    }

    $group = db_one('SELECT * FROM friend_group WHERE id = ?', [$groupId]);
    friend_ok(['group' => friend_group_payload($group)]);
}

function friend_group_payload(array $g): array
{
    $count = (int) db_one('SELECT COUNT(*) AS c FROM friend_group_member WHERE group_id = ?', [(int) $g['id']])['c'];
    return [
        'id' => (int) $g['id'],
        'name' => (string) $g['name'],
        'owner_id' => (int) $g['owner_id'],
        'member_count' => $count,
        'created_at' => (int) $g['created_at'],
    ];
}

function friend_group_list(): void
{
    $me = friend_auth();
    $rows = db_all(
        'SELECT g.* FROM friend_group g JOIN friend_group_member m ON m.group_id = g.id
         WHERE m.user_id = ? ORDER BY g.created_at DESC LIMIT 100',
        [(int) $me['id']]
    );
    $items = [];
    foreach ($rows as $row) {
        $items[] = friend_group_payload($row);
    }
    friend_ok(['items' => $items]);
}

function friend_group_info(): void
{
    $me = friend_auth();
    $groupId = (int) input('group_id', 0);
    if (db_one('SELECT 1 FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, (int) $me['id']]) === null) {
        friend_err('E_GROUP', '你不在这个群里');
    }
    $rows = db_all(
        'SELECT u.id, u.email, u.nickname, m.role FROM friend_group_member m JOIN friend_user u ON u.id = m.user_id
         WHERE m.group_id = ? ORDER BY m.role DESC, m.joined_at ASC',
        [$groupId]
    );
    $members = [];
    foreach ($rows as $row) {
        $members[] = [
            'id' => (int) $row['id'],
            'email' => (string) $row['email'],
            'nickname' => (string) $row['nickname'],
            'role' => (int) $row['role'],
        ];
    }
    friend_ok(['members' => $members]);
}

/** 群主邀请成员入群 */
function friend_group_add_members(): void
{
    $me = friend_auth();
    $groupId = (int) input('group_id', 0);
    $group = db_one('SELECT * FROM friend_group WHERE id = ?', [$groupId]);
    if ($group === null || (int) $group['owner_id'] !== (int) $me['id']) {
        friend_err('E_GROUP', '只有群主可以邀请成员');
    }
    $rawMembers = input('members', []);
    $added = [];
    if (is_array($rawMembers)) {
        $existing = [];
        foreach (db_all('SELECT user_id FROM friend_group_member WHERE group_id = ?', [$groupId]) as $row) {
            $existing[(int) $row['user_id']] = true;
        }
        foreach ($rawMembers as $id) {
            $id = (int) $id;
            if ($id <= 0 || isset($existing[$id])) {
                continue;
            }
            // 只能邀请自己的好友，避免被拿来拉陌生人
            if (!friend_relation_exists((int) $me['id'], $id)) {
                continue;
            }
            if (db_one("SELECT id FROM friend_user WHERE id = ? AND status = 'active'", [$id]) === null) {
                continue;
            }
            db_exec('INSERT OR IGNORE INTO friend_group_member (group_id, user_id, role, joined_at) VALUES (?, ?, 0, ?)',
                [$groupId, $id, friend_now()]);
            friend_touch_conversation($id, 1, (string) $groupId);
            $added[] = $id;
        }
    }
    if (!$added) {
        friend_err('E_PARAM', '没有可邀请的成员');
    }
    tim_add_group_members($groupId, $added);
    friend_ok(['added' => count($added)]);
}

/** 群主踢出成员 */
function friend_group_kick(): void
{
    $me = friend_auth();
    $groupId = (int) input('group_id', 0);
    $target = (int) input('user_id', 0);
    $group = db_one('SELECT * FROM friend_group WHERE id = ?', [$groupId]);
    if ($group === null || (int) $group['owner_id'] !== (int) $me['id']) {
        friend_err('E_GROUP', '只有群主可以移除成员');
    }
    if ($target <= 0 || $target === (int) $me['id']) {
        friend_err('E_PARAM', '不能移除自己');
    }
    if (db_one('SELECT 1 FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, $target]) === null) {
        friend_err('E_PARAM', '该用户不在群里');
    }
    db_exec('DELETE FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, $target]);
    db_exec('DELETE FROM friend_conversation WHERE user_id = ? AND conv_type = 1 AND conv_key = ?', [$target, (string) $groupId]);
    tim_remove_group_member($groupId, $target);
    friend_ok([]);
}

function friend_group_leave(): void
{
    $me = friend_auth();
    $groupId = (int) input('group_id', 0);
    $group = db_one('SELECT * FROM friend_group WHERE id = ?', [$groupId]);
    if ($group === null || db_one('SELECT 1 FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, (int) $me['id']]) === null) {
        friend_err('E_GROUP', '你不在这个群里');
    }
    db_exec('DELETE FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, (int) $me['id']]);
    db_exec('DELETE FROM friend_conversation WHERE user_id = ? AND conv_type = 1 AND conv_key = ?', [(int) $me['id'], (string) $groupId]);
    tim_remove_group_member($groupId, (int) $me['id']);

    $left = db_all('SELECT user_id FROM friend_group_member WHERE group_id = ? ORDER BY joined_at ASC', [$groupId]);
    if (!$left) {
        db_exec('DELETE FROM friend_group WHERE id = ?', [$groupId]);
        db_exec('DELETE FROM friend_conversation WHERE conv_type = 1 AND conv_key = ?', [(string) $groupId]);
        friend_ok(['destroyed' => true]);
    }
    if ((int) $group['owner_id'] === (int) $me['id']) {
        $newOwner = (int) $left[0]['user_id'];
        db_exec('UPDATE friend_group SET owner_id = ? WHERE id = ?', [$newOwner, $groupId]);
        db_exec('UPDATE friend_group_member SET role = 1 WHERE group_id = ? AND user_id = ?', [$groupId, $newOwner]);
    }
    friend_ok(['destroyed' => false]);
}

// ------------------------------------------

// ---------------- 消息 ----------------

function friend_message_payload(array $row, int $myId): array
{
    return [
        'id' => (int) $row['id'],
        'conv_type' => (int) $row['conv_type'],
        'conv_key' => (string) $row['conv_key'],
        'from_id' => (int) $row['from_id'],
        'from_name' => (string) ($row['from_name'] ?? ''),
        'body' => (string) $row['body'],
        'kind' => (string) $row['kind'],
        'created_at' => (int) $row['created_at'],
        'mine' => (int) $row['from_id'] === $myId,
    ];
}

/** 撤回自己发的消息（默认 120 秒内） */
function friend_recall(): void
{
    $me = friend_auth();
    $id = (int) input('msg_id', 0);
    if ($id <= 0) {
        friend_err('E_PARAM', '消息无效');
    }
    $row = db_one('SELECT * FROM friend_message WHERE id = ?', [$id]);
    if ($row === null) {
        friend_err('E_PARAM', '消息不存在或已被删除');
    }
    if ((int) $row['from_id'] !== (int) $me['id']) {
        friend_err('E_NOAUTH', '只能撤回自己发送的消息');
    }
    if ((string) $row['kind'] === 'recalled') {
        friend_ok([]);
    }
    $window = max(30, (int) friend_config('recall_seconds', 120));
    if (friend_now() - (int) $row['created_at'] > $window) {
        friend_err('E_EXPIRE', "只能撤回 {$window} 秒内的消息");
    }
    db_exec("UPDATE friend_message SET kind = 'recalled', body = '', recalled_at = ? WHERE id = ?",
        [friend_now(), $id]);

    // 会话列表的预览也要同步，否则撤回后列表里还留着原文
    $preview = mb_substr((string) $row['body'], 0, 60);
    if ((int) $row['conv_type'] === 1) {
        db_exec('UPDATE friend_conversation SET last_body = ? WHERE conv_type = 1 AND conv_key = ? AND last_body = ?',
            ['[已撤回]', (string) $row['group_id'], $preview]);
    } else {
        db_exec('UPDATE friend_conversation SET last_body = ?
                 WHERE conv_type = 0 AND last_body = ?
                   AND ((user_id = ? AND conv_key = ?) OR (user_id = ? AND conv_key = ?))',
            ['[已撤回]', $preview, (int) $row['from_id'], (string) $row['to_id'], (int) $row['to_id'], (string) $row['from_id']]);
    }
    friend_ok([]);
}

function friend_send_msg(): void
{
    $me = friend_auth();
    $convType = (int) input('conv_type', 0) === 1 ? 1 : 0;
    $convKey = trim((string) input('conv_key'));
    $body = trim((string) input('body'));
    $clientId = mb_substr(trim((string) input('client_id')), 0, 40);

    if ($body === '') {
        friend_err('E_PARAM', '消息不能为空');
    }
    if (mb_strlen($body) > 2000) {
        friend_err('E_PARAM', '单条消息不能超过 2000 个字符');
    }
    if ($convKey === '') {
        friend_err('E_PARAM', '会话无效');
    }

    $limit = max(5, (int) friend_config('msg_rate_per_min', 40));
    $recent = (int) db_one('SELECT COUNT(*) AS c FROM friend_message WHERE from_id = ? AND created_at > ?',
        [(int) $me['id'], friend_now() - 60])['c'];
    if ($recent >= $limit) {
        friend_err('E_RATE', '发送太快了，休息一下吧');
    }

    $toId = 0;
    $groupId = 0;
    if ($convType === 0) {
        $toId = (int) $convKey;
        if (!friend_relation_exists((int) $me['id'], $toId)) {
            friend_err('E_FRIEND', '对方不是你的好友');
        }
    } else {
        $groupId = (int) $convKey;
        if (db_one('SELECT 1 FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, (int) $me['id']]) === null) {
            friend_err('E_GROUP', '你不在这个群里');
        }
    }

    db_exec(
        'INSERT INTO friend_message (conv_type, conv_key, from_id, to_id, group_id, body, kind, client_id, created_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)',
        [$convType, $convKey, (int) $me['id'], $toId, $groupId, $body, 'text', $clientId, friend_now()]
    );
    $messageId = (int) db()->lastInsertId();
    $now = friend_now();

    // 会话：发送方已读，接收方未读 +1
    friend_touch_conversation((int) $me['id'], $convType, $convKey);
    db_exec(
        'UPDATE friend_conversation SET last_body = ?, last_at = ?, unread = 0
         WHERE user_id = ? AND conv_type = ? AND conv_key = ?',
        [mb_substr($body, 0, 60), $now, (int) $me['id'], $convType, $convKey]
    );

    if ($convType === 0) {
        friend_touch_conversation($toId, 0, (string) $me['id']);
        db_exec(
            'UPDATE friend_conversation SET last_body = ?, last_at = ?, unread = unread + 1
             WHERE user_id = ? AND conv_type = 0 AND conv_key = ?',
            [mb_substr($body, 0, 60), $now, $toId, (string) $me['id']]
        );
        $imId = tim_send_c2c((int) $me['id'], $toId, $body);
    } else {
        $members = db_all('SELECT user_id FROM friend_group_member WHERE group_id = ? AND user_id <> ?', [$groupId, (int) $me['id']]);
        foreach ($members as $member) {
            $uid = (int) $member['user_id'];
            friend_touch_conversation($uid, 1, (string) $groupId);
            db_exec(
                'UPDATE friend_conversation SET last_body = ?, last_at = ?, unread = unread + 1
                 WHERE user_id = ? AND conv_type = 1 AND conv_key = ?',
                [mb_substr($body, 0, 60), $now, $uid, (string) $groupId]
            );
        }
        $imId = tim_send_group($groupId, (int) $me['id'], $body);
    }

    if (!empty($imId)) {
        db_exec('UPDATE friend_message SET im_msg_id = ? WHERE id = ?', [(string) $imId, $messageId]);
    }

    $row = db_one('SELECT m.*, u.nickname AS from_name FROM friend_message m LEFT JOIN friend_user u ON u.id = m.from_id WHERE m.id = ?', [$messageId]);
    friend_ok(['message' => friend_message_payload($row, (int) $me['id'])]);
}

function friend_poll(): void
{
    $me = friend_auth();
    $since = (int) input('since_id', 0);
    $limit = min(200, max(1, (int) input('limit', 100)));

    $rows = db_all(
        'SELECT m.*, u.nickname AS from_name FROM friend_message m LEFT JOIN friend_user u ON u.id = m.from_id
         WHERE m.id > ? AND (
             m.to_id = ? OR m.from_id = ? OR
             (m.conv_type = 1 AND m.group_id IN (SELECT group_id FROM friend_group_member WHERE user_id = ?))
         )
         ORDER BY m.id ASC LIMIT ?',
        [$since, (int) $me['id'], (int) $me['id'], (int) $me['id'], $limit]
    );
    $items = [];
    $maxId = $since;
    foreach ($rows as $row) {
        $items[] = friend_message_payload($row, (int) $me['id']);
        $maxId = max($maxId, (int) $row['id']);
    }
    // 撤回不会产生新消息，单独告诉客户端哪些消息要变成「已撤回」
    $recallRows = db_all(
        'SELECT id FROM friend_message
         WHERE kind = ? AND recalled_at > ? AND (
             to_id = ? OR from_id = ? OR
             (conv_type = 1 AND group_id IN (SELECT group_id FROM friend_group_member WHERE user_id = ?))
         ) ORDER BY recalled_at DESC LIMIT 200',
        ['recalled', friend_now() - 600, (int) $me['id'], (int) $me['id'], (int) $me['id']]
    );
    $recalls = [];
    foreach ($recallRows as $row) {
        $recalls[] = (int) $row['id'];
    }
    friend_ok(['messages' => $items, 'max_id' => $maxId, 'recalls' => $recalls]);
}

function friend_messages(): void
{
    $me = friend_auth();
    $convType = (int) input('conv_type', 0) === 1 ? 1 : 0;
    $convKey = trim((string) input('conv_key'));
    $beforeId = (int) input('before_id', 0);
    $limit = min(100, max(1, (int) input('limit', 50)));

    if ($convType === 0) {
        $other = (int) $convKey;
        if (!friend_relation_exists((int) $me['id'], $other)) {
            friend_err('E_FRIEND', '对方不是你的好友');
        }
        $where = 'm.conv_type = 0 AND ((m.from_id = ? AND m.to_id = ?) OR (m.from_id = ? AND m.to_id = ?))';
        $params = [(int) $me['id'], $other, $other, (int) $me['id']];
    } else {
        $groupId = (int) $convKey;
        if (db_one('SELECT 1 FROM friend_group_member WHERE group_id = ? AND user_id = ?', [$groupId, (int) $me['id']]) === null) {
            friend_err('E_GROUP', '你不在这个群里');
        }
        $where = 'm.conv_type = 1 AND m.group_id = ?';
        $params = [$groupId];
    }
    if ($beforeId > 0) {
        $where .= ' AND m.id < ?';
        $params[] = $beforeId;
    }
    $params[] = $limit;

    $rows = db_all(
        "SELECT m.*, u.nickname AS from_name FROM friend_message m LEFT JOIN friend_user u ON u.id = m.from_id
         WHERE {$where} ORDER BY m.id DESC LIMIT ?",
        $params
    );
    $items = [];
    foreach (array_reverse($rows) as $row) {
        $items[] = friend_message_payload($row, (int) $me['id']);
    }
    friend_ok(['items' => $items, 'has_more' => count($rows) >= $limit]);
}

function friend_conversations(): void
{
    $me = friend_auth();
    $rows = db_all(
        'SELECT c.*,
                (SELECT COUNT(*) FROM friend_group_member gm WHERE gm.group_id = CAST(c.conv_key AS INTEGER)) AS member_count,
                (SELECT u.last_seen_at FROM friend_user u WHERE u.id = CAST(c.conv_key AS INTEGER)) AS peer_seen
         FROM friend_conversation c
         WHERE c.user_id = ? ORDER BY c.last_at DESC, c.rowid DESC LIMIT 200',
        [(int) $me['id']]
    );
    $items = [];
    foreach ($rows as $row) {
        $convType = (int) $row['conv_type'];
        $items[] = [
            'conv_type' => $convType,
            'conv_key' => (string) $row['conv_key'],
            'title' => (string) $row['title'],
            'last_body' => (string) $row['last_body'],
            'last_at' => (int) $row['last_at'],
            'unread' => (int) $row['unread'],
            'member_count' => $convType === 1 ? (int) $row['member_count'] : 0,
            'online' => $convType === 0 && friend_now() - (int) $row['peer_seen'] <= 120,
        ];
    }
    friend_ok(['items' => $items]);
}

function friend_read(): void
{
    $me = friend_auth();
    $convType = (int) input('conv_type', 0) === 1 ? 1 : 0;
    $convKey = trim((string) input('conv_key'));
    db_exec('UPDATE friend_conversation SET unread = 0 WHERE user_id = ? AND conv_type = ? AND conv_key = ?',
        [(int) $me['id'], $convType, $convKey]);
    friend_ok([]);
}

function friend_history(): void
{
    $me = friend_auth();
    $keyword = trim((string) input('keyword'));
    $scope = (int) input('scope', 0);
    $beforeId = (int) input('before_id', 0);
    $limit = min(100, max(1, (int) input('limit', 40)));

    $where = '(m.to_id = ? OR m.from_id = ? OR (m.conv_type = 1 AND m.group_id IN (SELECT group_id FROM friend_group_member WHERE user_id = ?)))';
    $params = [(int) $me['id'], (int) $me['id'], (int) $me['id']];
    if ($scope === 1) {
        $where .= ' AND m.conv_type = 0';
    } elseif ($scope === 2) {
        $where .= ' AND m.conv_type = 1';
    }
    if ($keyword !== '') {
        $where .= " AND m.body LIKE ? ESCAPE '\\'";
        $params[] = '%' . str_replace(['%', '_'], ['\%', '\_'], $keyword) . '%';
    }
    if ($beforeId > 0) {
        $where .= ' AND m.id < ?';
        $params[] = $beforeId;
    }
    $params[] = $limit;

    $rows = db_all(
        "SELECT m.*, u.nickname AS from_name,
                CASE WHEN m.conv_type = 0 THEN ou.nickname ELSE g.name END AS title
         FROM friend_message m
         LEFT JOIN friend_user u ON u.id = m.from_id
         LEFT JOIN friend_user ou ON ou.id = CASE WHEN m.conv_type = 0 THEN
             (CASE WHEN m.from_id = ? THEN m.to_id ELSE m.from_id END) ELSE 0 END
         LEFT JOIN friend_group g ON g.id = m.group_id
         WHERE {$where} ORDER BY m.id DESC LIMIT ?",
        array_merge([(int) $me['id']], $params)
    );
    $items = [];
    $nextBefore = 0;
    foreach ($rows as $row) {
        $items[] = [
            'id' => (int) $row['id'],
            'conv_type' => (int) $row['conv_type'],
            'conv_key' => (string) $row['conv_key'],
            'title' => (string) ($row['title'] ?? '会话'),
            'from_name' => (string) ($row['from_name'] ?? ''),
            'body' => (string) $row['body'],
            'created_at' => (int) $row['created_at'],
        ];
        $nextBefore = (int) $row['id'];
    }
    friend_ok(['items' => $items, 'next_before' => $nextBefore]);
}

function friend_sync(): void
{
    $me = friend_auth();
    $since = (int) input('since_id', 0);
    $limit = min(500, max(1, (int) input('limit', 500)));
    $rows = db_all(
        'SELECT m.*, u.nickname AS from_name FROM friend_message m LEFT JOIN friend_user u ON u.id = m.from_id
         WHERE m.id > ? AND (m.to_id = ? OR m.from_id = ? OR
             (m.conv_type = 1 AND m.group_id IN (SELECT group_id FROM friend_group_member WHERE user_id = ?)))
         ORDER BY m.id ASC LIMIT ?',
        [$since, (int) $me['id'], (int) $me['id'], (int) $me['id'], $limit]
    );
    $items = [];
    $maxId = $since;
    foreach ($rows as $row) {
        $items[] = friend_message_payload($row, (int) $me['id']);
        $maxId = max($maxId, (int) $row['id']);
    }
    friend_ok(['messages' => $items, 'max_id' => $maxId]);
}

// ------------------------------------------

// ---------------- 个人 / 配置 ----------------

function friend_me(): void
{
    $me = friend_auth();
    friend_ok(['user' => friend_public($me)]);
}

function friend_config_api(): void
{
    friend_ok([
        'name' => (string) cfg('app_name', 'RMCL 好友服务'),
        'version' => 1,
        'require_invite' => (bool) friend_config('require_invite', true),
        'email_verify' => (bool) friend_config('email_enabled', true),
        'im_enabled' => tim_enabled(),
        'sdkappid' => tim_enabled() ? (int) (friend_config('tim', [])['sdkappid'] ?? 0) : 0,
        'mail_transport' => friend_smtp_config() !== null ? 'smtp' : 'php_mail',
    ]);
}

function friend_set_profile(): void
{
    $me = friend_auth();
    $nickname = trim((string) input('nickname'));
    if (mb_strlen($nickname) < 2 || mb_strlen($nickname) > 16) {
        friend_err('E_NAME', '昵称需要 2-16 个字符');
    }
    db_exec('UPDATE friend_user SET nickname = ? WHERE id = ?', [$nickname, (int) $me['id']]);
    tim_account_import((int) $me['id'], $nickname);
    $me['nickname'] = $nickname;
    friend_ok(['user' => friend_public($me)]);
}

function friend_logout(): void
{
    $token = friend_http_token();
    if ($token !== '') {
        db_exec('DELETE FROM friend_token WHERE token_hash = ?', [hash('sha256', $token)]);
    }
    friend_ok([]);
}

// ------------------------------------------

// ---------------- 入口 ----------------

/**
 * 处理一个好友接口。由 index.php 在识别到 friend_ 前缀后调用。
 */
function friend_handle(string $action): void
{
    friend_tables();
    switch ($action) {
        case 'config':
            friend_config_api();
            return;
        case 'send_code':
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
                friend_err('E_METHOD', '请使用 POST');
            }
            friend_send_code((string) input('email'), (string) input('purpose', 'register'));
            return;
        case 'register':
            friend_register();
            return;
        case 'login':
            friend_login();
            return;
        case 'rayskin_login':
            friend_rayskin_login();
            return;
        case 'logout':
            friend_logout();
            return;
        case 'me':
            friend_me();
            return;
        case 'set_profile':
            friend_set_profile();
            return;
        case 'search':
            friend_search();
            return;
        case 'add':
            friend_add();
            return;
        case 'requests':
            friend_requests();
            return;
        case 'accept':
        case 'friend_accept':
            friend_answer_request(true);
            return;
        case 'reject':
        case 'friend_reject':
            friend_answer_request(false);
            return;
        case 'delete_friend':
        case 'friend_delete':
            friend_delete_relation();
            return;
        case 'contacts':
            friend_contacts();
            return;
        case 'group_create':
            friend_group_create();
            return;
        case 'group_list':
            friend_group_list();
            return;
        case 'group_info':
            friend_group_info();
            return;
        case 'group_add_members':
            friend_group_add_members();
            return;
        case 'group_kick':
            friend_group_kick();
            return;
        case 'group_leave':
            friend_group_leave();
            return;
        case 'recall':
            friend_recall();
            return;
        case 'send':
            friend_send_msg();
            return;
        case 'poll':
            friend_poll();
            return;
        case 'messages':
            friend_messages();
            return;
        case 'conversations':
            friend_conversations();
            return;
        case 'read':
            friend_read();
            return;
        case 'history':
            friend_history();
            return;
        case 'sync':
            friend_sync();
            return;
        default:
            friend_err('E_NOT_FOUND', '未知的好友接口：' . $action);
    }
}

// ------------------------------------------
