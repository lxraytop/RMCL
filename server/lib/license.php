<?php
/**
 * 授权：验证码、设备绑定、有效期、令牌、下载票据。
 *
 * 规则：
 *  - 一个验证码默认绑定一台设备（device_limit 可调）。
 *  - 有效期两种模式：day（首次激活起算 N 天）与 date（固定到期日当天 23:59:59 失效）。
 *  - 到期后令牌立即失效，接口一律拒绝，客户端无法继续检查/下载更新。
 *  - 重新激活同一台设备不会延长到期时间，避免用户靠重输验证码无限续期。
 */

declare(strict_types=1);

// 去掉了容易看错的 I O 0 1
const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

function gen_code(): string
{
    $groups = [];
    for ($g = 0; $g < 4; $g++) {
        $part = '';
        for ($i = 0; $i < 4; $i++) {
            $part .= CODE_ALPHABET[random_int(0, strlen(CODE_ALPHABET) - 1)];
        }
        $groups[] = $part;
    }
    return implode('-', $groups);
}

/** 把用户随手输入的码统一成 XXXX-XXXX-XXXX-XXXX；格式不对返回空串 */
function normalize_code(string $raw): string
{
    $s = strtoupper(preg_replace('/[^A-Za-z0-9]/', '', $raw) ?? '');
    if (str_starts_with($s, 'RMCL')) {
        $s = substr($s, 4);
    }
    if (strlen($s) !== 16) {
        return '';
    }
    return implode('-', str_split($s, 4));
}

/** 截断字符串；不依赖 mbstring 扩展（虚拟主机偶尔没开） */
function str_limit(string $value, int $length): string
{
    if (function_exists('mb_substr')) {
        return mb_substr($value, 0, $length);
    }
    return substr($value, 0, $length);
}

function mask_code(string $code): string
{
    $plain = preg_replace('/[^A-Z0-9]/', '', strtoupper($code)) ?? '';
    if (strlen($plain) <= 8) {
        return $code;
    }
    return substr($plain, 0, 4) . '-****-****-' . substr($plain, -4);
}

function remaining_days(int $expireAt): int
{
    $left = $expireAt - time();
    if ($left <= 0) {
        return 0;
    }
    return (int) ceil($left / 86400);
}

/** 同一 IP 的激活失败次数限制，挡住穷举验证码 */
function guard_activate_attempts(): void
{
    $since = time() - 600;
    $row = db_one(
        "SELECT COUNT(*) AS n FROM logs WHERE event = 'activate.fail' AND ip = ? AND ts > ?",
        [client_ip(), $since]
    );
    if ($row !== null && (int) $row['n'] >= 20) {
        throw new ApiError('E_TOO_MANY', '尝试次数过多，请 10 分钟后再试', 429);
    }
}

function issue_token(int $codeId, string $deviceId, int $codeExpireAt): array
{
    $ttl = (int) cfg('token_ttl', 0);
    $expire = $ttl > 0 ? min(time() + $ttl, $codeExpireAt) : $codeExpireAt;
    $token = bin2hex(random_bytes(24));
    db_exec(
        'INSERT INTO tokens (token, code_id, device_id, created_at, expire_at) VALUES (?, ?, ?, ?, ?)',
        [$token, $codeId, $deviceId, time(), $expire]
    );
    return ['token' => $token, 'expire_at' => $expire];
}

/**
 * 激活验证码。
 * @return array 客户端需要的授权信息
 */
function activate(string $rawCode, string $deviceId, string $deviceName, string $appVersion): array
{
    guard_activate_attempts();

    $deviceId = trim($deviceId);
    if ($deviceId === '' || strlen($deviceId) > 128 || !preg_match('/^[A-Za-z0-9._:-]+$/', $deviceId)) {
        throw new ApiError('E_BAD_DEVICE', '设备标识无效，请更新到最新版客户端', 400);
    }

    $code = normalize_code($rawCode);
    if ($code === '') {
        throw new ApiError('E_CODE_FORMAT', '验证码格式不正确，应为 4 组 4 位字符');
    }

    $row = db_one('SELECT * FROM codes WHERE code = ?', [$code]);
    if ($row === null) {
        log_event('activate.fail', ['device_id' => $deviceId, 'detail' => '验证码不存在']);
        throw new ApiError('E_CODE_NOT_FOUND', '验证码不存在，请检查是否输入有误');
    }
    if ($row['status'] === 'disabled') {
        log_event('activate.fail', ['code_id' => (int) $row['id'], 'device_id' => $deviceId, 'detail' => '验证码已停用']);
        throw new ApiError('E_CODE_DISABLED', '该验证码已被停用，请联系管理员');
    }

    $now = time();
    $codeId = (int) $row['id'];

    // 首次激活：确定到期时间
    if ((int) $row['activated_at'] === 0) {
        if ($row['mode'] === 'date') {
            $expire = strtotime($row['fixed_expire'] . ' 23:59:59');
            if ($expire === false) {
                throw new ApiError('E_CODE_INVALID', '该验证码未设置有效的到期日期，请联系管理员', 500);
            }
        } else {
            $expire = $now + max(1, (int) $row['days']) * 86400;
        }
        db_exec(
            "UPDATE codes SET activated_at = ?, expire_at = ?, status = 'active', last_seen_at = ?, last_ip = ? WHERE id = ?",
            [$now, $expire, $now, client_ip(), $codeId]
        );
        $row['activated_at'] = $now;
        $row['expire_at'] = $expire;
        $row['status'] = 'active';
    }

    $expireAt = (int) $row['expire_at'];
    if ($expireAt <= $now) {
        log_event('activate.fail', ['code_id' => $codeId, 'device_id' => $deviceId, 'detail' => '授权已过期']);
        throw new ApiError('E_EXPIRED', '授权已于 ' . date('Y-m-d H:i', $expireAt) . ' 到期，请续期后再试', 403, [
            'expire_at' => $expireAt,
        ]);
    }

    // 设备绑定
    $bound = db_all('SELECT * FROM devices WHERE code_id = ?', [$codeId]);
    $mine = null;
    foreach ($bound as $d) {
        if (hash_equals((string) $d['device_id'], $deviceId)) {
            $mine = $d;
            break;
        }
    }
    $limit = max(1, (int) $row['device_limit']);
    if ($mine === null) {
        if (count($bound) >= $limit) {
            $other = $bound[0]['device_name'] !== '' ? $bound[0]['device_name'] : '另一台设备';
            log_event('activate.fail', ['code_id' => $codeId, 'device_id' => $deviceId, 'detail' => '设备数已达上限']);
            throw new ApiError('E_DEVICE_BOUND', "该验证码已绑定{$other}，如需换机请联系管理员解绑", 403, [
                'bound_name' => $other,
            ]);
        }
        db_exec(
            'INSERT OR IGNORE INTO devices (code_id, device_id, device_name, first_seen, last_seen, ip, app_version) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$codeId, $deviceId, str_limit($deviceName, 0, 64), $now, $now, client_ip(), str_limit($appVersion, 0, 32)]
        );
        log_event('activate.bind', ['code_id' => $codeId, 'device_id' => $deviceId, 'detail' => '绑定新设备 ' . $deviceName]);
    } else {
        db_exec(
            'UPDATE devices SET last_seen = ?, ip = ?, app_version = ?, device_name = ? WHERE id = ?',
            [$now, client_ip(), str_limit($appVersion, 0, 32), str_limit($deviceName, 0, 64), (int) $mine['id']]
        );
    }

    db_exec(
        'UPDATE codes SET last_seen_at = ?, last_ip = ?, last_device = ? WHERE id = ?',
        [$now, client_ip(), str_limit($deviceName, 0, 64), $codeId]
    );

    $token = issue_token($codeId, $deviceId, $expireAt);
    gc_expired();
    log_event('activate.ok', ['code_id' => $codeId, 'device_id' => $deviceId, 'detail' => '激活成功']);

    return array_merge($token, [
        'code_mask' => mask_code($code),
        'device_id' => $deviceId,
        'activated_at' => (int) $row['activated_at'],
        'expire_at' => $expireAt,
        'remaining_days' => remaining_days($expireAt),
        'mode' => $row['mode'],
    ]);
}

/**
 * 校验令牌，通过则返回授权上下文。
 * @return array{code_id:int, device_id:string, expire_at:int, code:array}
 */
function verify_token(string $token, string $deviceId, bool $touch = true): array
{
    $token = trim($token);
    if ($token === '' || strlen($token) > 128) {
        throw new ApiError('E_NO_TOKEN', '尚未授权，请先输入验证码', 401);
    }

    $row = db_one(
        'SELECT t.token, t.code_id, t.device_id, t.expire_at AS token_expire, c.*
           FROM tokens t JOIN codes c ON c.id = t.code_id
          WHERE t.token = ?',
        [$token]
    );
    if ($row === null) {
        throw new ApiError('E_TOKEN_INVALID', '授权凭证无效，请重新输入验证码', 401);
    }
    if (!hash_equals((string) $row['device_id'], trim($deviceId))) {
        throw new ApiError('E_TOKEN_DEVICE', '授权凭证与当前设备不匹配', 403);
    }
    if ($row['status'] === 'disabled') {
        throw new ApiError('E_CODE_DISABLED', '该授权已被管理员停用', 403);
    }

    $now = time();
    if ((int) $row['expire_at'] <= $now) {
        db_exec('DELETE FROM tokens WHERE token = ?', [$token]);
        throw new ApiError('E_EXPIRED', '授权已于 ' . date('Y-m-d H:i', (int) $row['expire_at']) . ' 到期，请续期后再试', 403, [
            'expire_at' => (int) $row['expire_at'],
        ]);
    }
    if ((int) $row['token_expire'] <= $now) {
        throw new ApiError('E_TOKEN_EXPIRED', '授权凭证已失效，请重新输入验证码', 401);
    }

    if ($touch) {
        db_exec('UPDATE codes SET last_seen_at = ? WHERE id = ?', [$now, (int) $row['id']]);
    }

    return [
        'code_id' => (int) $row['id'],
        'device_id' => (string) $row['device_id'],
        'expire_at' => (int) $row['expire_at'],
        'code' => $row,
    ];
}

/** 从请求里取 token / device_id 并校验（input() 由入口文件提供，兼容 JSON body） */
function require_license(): array
{
    $token = (string) input('token');
    $deviceId = (string) input('device_id');
    return verify_token($token, $deviceId);
}

function create_ticket(array $license, int $releaseId): array
{
    $release = db_one("SELECT * FROM releases WHERE id = ? AND status = 'published'", [$releaseId]);
    if ($release === null) {
        throw new ApiError('E_NO_RELEASE', '该版本不存在或已下架', 404);
    }

    $ttl = max(60, (int) cfg('ticket_ttl', 600));
    $ticket = bin2hex(random_bytes(20));
    $expire = time() + $ttl;
    db_exec(
        'INSERT INTO tickets (ticket, code_id, device_id, release_id, created_at, expire_at, uses) VALUES (?, ?, ?, ?, ?, ?, 0)',
        [$ticket, $license['code_id'], $license['device_id'], $releaseId, time(), $expire]
    );
    gc_expired();
    log_event('ticket', ['code_id' => $license['code_id'], 'device_id' => $license['device_id'], 'detail' => '版本 ' . $release['version']]);

    return [
        'ticket' => $ticket,
        'url' => base_url() . '/index.php?action=download&ticket=' . $ticket,
        'expire_at' => $expire,
        'file' => [
            'name' => $release['file_name'],
            'size' => (int) $release['size'],
            'sha256' => $release['sha256'],
        ],
    ];
}

/** 取出票据对应的发布包，校验有效性 */
function consume_ticket(string $ticket): array
{
    $ticket = trim($ticket);
    if ($ticket === '' || !preg_match('/^[a-f0-9]{40}$/', $ticket)) {
        throw new ApiError('E_TICKET', '下载凭证无效', 403);
    }
    $row = db_one(
        'SELECT t.*, r.stored_name, r.file_name, r.size, r.sha256 FROM tickets t
           JOIN releases r ON r.id = t.release_id
          WHERE t.ticket = ?',
        [$ticket]
    );
    if ($row === null) {
        throw new ApiError('E_TICKET', '下载凭证无效，请重新发起更新', 403);
    }
    if ((int) $row['expire_at'] <= time()) {
        throw new ApiError('E_TICKET_EXPIRED', '下载凭证已过期，请重新发起更新', 403);
    }
    if ((int) $row['uses'] > (int) cfg('ticket_max_use', 800)) {
        throw new ApiError('E_TICKET_USES', '下载凭证使用次数超限，请重新发起更新', 429);
    }

    // 只有「第一次用到这张票据」才算一次真实下载：客户端是多线程分段下载，
    // 同一张票会被打几十次，按请求计数会把一次下载算成几十次。
    $first = db_exec('UPDATE tickets SET uses = uses + 1 WHERE ticket = ? AND uses = 0', [$ticket]) > 0;
    if ($first) {
        record_download((int) $row['release_id'], (int) $row['code_id'], (string) $row['device_id']);
    } else {
        db_exec('UPDATE tickets SET uses = uses + 1 WHERE ticket = ?', [$ticket]);
    }

    return $row;
}

/**
 * 记一次真实下载，供后台按用户与版本统计。
 * 单独建表而不是累加 releases.downloads：票据一小时后就被回收，累计值会跟票据一起失真。
 */
function record_download(int $releaseId, int $codeId, string $deviceId): void
{
    try {
        $version = '';
        $release = db_one('SELECT version FROM releases WHERE id = ?', [$releaseId]);
        if ($release !== null) {
            $version = (string) $release['version'];
        }
        db_exec(
            'INSERT INTO downloads (release_id, version, code_id, device_id, ts, ip) VALUES (?, ?, ?, ?, ?, ?)',
            [$releaseId, $version, $codeId, $deviceId, time(), client_ip()]
        );
        // 顺手把汇总值也加上：明细表用来对账，汇总字段用来给客户端/列表直接读
        db_exec('UPDATE releases SET downloads = downloads + 1 WHERE id = ?', [$releaseId]);
        log_event('download', [
            'code_id' => $codeId,
            'device_id' => $deviceId,
            'detail' => '下载 ' . ($version !== '' ? $version : ('#' . $releaseId)),
        ]);
    } catch (Throwable $e) {
        // 统计失败不能影响下载本身
    }
}

/** 后台：解除某验证码的全部设备绑定（到期时间不变） */
function unbind_devices(int $codeId): int
{
    $n = db_exec('DELETE FROM devices WHERE code_id = ?', [$codeId]);
    db_exec('DELETE FROM tokens WHERE code_id = ?', [$codeId]);
    db_exec('UPDATE codes SET unbind_count = unbind_count + 1 WHERE id = ?', [$codeId]);
    log_event('admin.unbind', ['code_id' => $codeId, 'detail' => '解绑 ' . $n . ' 台设备']);
    return $n;
}

/** 后台：续期（在现有到期时间基础上加天数；若已过期则从现在起算） */
function extend_code(int $codeId, int $days): void
{
    $row = db_one('SELECT * FROM codes WHERE id = ?', [$codeId]);
    if ($row === null) {
        return;
    }
    $base = max((int) $row['expire_at'], time());
    $expire = $base + max(1, $days) * 86400;
    $activated = (int) $row['activated_at'] === 0 ? time() : (int) $row['activated_at'];
    db_exec("UPDATE codes SET expire_at = ?, activated_at = ?, status = 'active' WHERE id = ?", [$expire, $activated, $codeId]);
    log_event('admin.extend', ['code_id' => $codeId, 'detail' => '+' . $days . ' 天']);
}
