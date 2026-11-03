<?php
/**
 * RMCL 更新服务 —— 管理后台
 *
 * 功能：签发/管理验证码（一码一设备、天数或固定日期）、解绑换机、续期、停用；
 *      发布版本（上传 exe 或扫描服务器上已放好的文件）；查看激活与下载日志。
 *
 * 安全：首次访问要求设置管理员密码（哈希写入 data/admin.hash）；
 *      登录态走 session，所有写操作校验 CSRF Token。
 */

declare(strict_types=1);

require __DIR__ . '/lib/core.php';
require __DIR__ . '/lib/license.php';

session_name('rmcl_admin');
session_set_cookie_params([
    'httponly' => true,
    'samesite' => 'Lax',
    'secure' => (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off'),
]);
session_start();

$HASH_FILE = data_dir() . '/admin.hash';

function current_hash(): string
{
    global $HASH_FILE;
    $configured = trim((string) cfg('admin_password_hash', ''));
    if ($configured !== '') {
        return $configured;
    }
    return is_file($HASH_FILE) ? trim((string) file_get_contents($HASH_FILE)) : '';
}

function is_logged_in(): bool
{
    return !empty($_SESSION['rmcl_admin']);
}

function csrf_token(): string
{
    if (empty($_SESSION['csrf'])) {
        $_SESSION['csrf'] = bin2hex(random_bytes(16));
    }
    return $_SESSION['csrf'];
}

function check_csrf(): void
{
    $sent = (string) ($_POST['csrf'] ?? '');
    if ($sent === '' || !hash_equals((string) ($_SESSION['csrf'] ?? ''), $sent)) {
        flash('表单已过期，请重新提交', 'error');
        redirect('codes');
    }
}

function flash(string $message, string $type = 'ok'): void
{
    $_SESSION['flash'] = ['message' => $message, 'type' => $type];
}

function take_flash(): ?array
{
    $f = $_SESSION['flash'] ?? null;
    unset($_SESSION['flash']);
    return $f;
}

function redirect(string $tab = '', array $query = []): void
{
    $url = 'admin.php';
    $params = array_merge($tab !== '' ? ['tab' => $tab] : [], $query);
    if ($params) {
        $url .= '?' . http_build_query($params);
    }
    header('Location: ' . $url);
    exit;
}

function h($value): string
{
    return htmlspecialchars((string) $value, ENT_QUOTES, 'UTF-8');
}

function fmt_time(int $ts): string
{
    return $ts > 0 ? date('Y-m-d H:i', $ts) : '—';
}

function code_status_label(array $row): array
{
    if ($row['status'] === 'disabled') {
        return ['已停用', 'gray'];
    }
    if ((int) $row['activated_at'] === 0) {
        return ['未使用', 'blue'];
    }
    if ((int) $row['expire_at'] <= time()) {
        return ['已过期', 'red'];
    }
    return ['生效中', 'green'];
}

// ---------------------------------------------------------------- 动作处理

$action = (string) ($_POST['do'] ?? $_GET['do'] ?? '');

if ($action === 'setup' && current_hash() === '') {
    $password = (string) ($_POST['password'] ?? '');
    if (strlen($password) < 6) {
        flash('密码至少 6 位', 'error');
        redirect();
    }
    $hash = password_hash($password, PASSWORD_DEFAULT);
    if (@file_put_contents($HASH_FILE, $hash) === false) {
        flash('无法写入 ' . $HASH_FILE . '，请在 config.php 里填写 admin_password_hash', 'error');
        redirect();
    }
    @chmod($HASH_FILE, 0600);
    $_SESSION['rmcl_admin'] = true;
    flash('管理员密码已设置');
    redirect('codes');
}

if ($action === 'login') {
    $password = (string) ($_POST['password'] ?? '');
    $hash = current_hash();
    if ($hash !== '' && password_verify($password, $hash)) {
        $_SESSION['rmcl_admin'] = true;
        redirect('codes');
    }
    usleep(400000);
    flash('密码不正确', 'error');
    redirect();
}

if ($action === 'logout') {
    $_SESSION = [];
    session_destroy();
    redirect();
}

if (!is_logged_in()) {
    require __DIR__ . '/lib/admin_login.php';
    exit;
}

if ($action !== '') {
    check_csrf();
}

try {
    switch ($action) {
        case 'create_codes':
            $count = max(1, min(200, (int) ($_POST['count'] ?? 1)));
            $mode = ($_POST['mode'] ?? 'day') === 'date' ? 'date' : 'day';
            $days = max(1, min(3650, (int) ($_POST['days'] ?? 30)));
            $fixed = trim((string) ($_POST['fixed_expire'] ?? ''));
            $note = mb_substr(trim((string) ($_POST['note'] ?? '')), 0, 120);
            $limit = max(1, min(20, (int) ($_POST['device_limit'] ?? 1)));

            if ($mode === 'date') {
                $ts = strtotime($fixed);
                if ($ts === false) {
                    throw new RuntimeException('到期日期格式不正确，应为 2026-12-31');
                }
                if ($ts < time()) {
                    throw new RuntimeException('到期日期不能早于今天');
                }
                $fixed = date('Y-m-d', $ts);
            }

            $created = [];
            $pdo = db();
            $pdo->beginTransaction();
            for ($i = 0; $i < $count; $i++) {
                do {
                    $code = gen_code();
                    $exists = db_one('SELECT id FROM codes WHERE code = ?', [$code]);
                } while ($exists !== null);
                db_exec(
                    'INSERT INTO codes (code, note, mode, days, fixed_expire, device_limit, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
                    [$code, $note, $mode, $days, $mode === 'date' ? $fixed : '', $limit, 'unused', time()]
                );
                $created[] = $code;
            }
            $pdo->commit();
            log_event('admin.create', ['detail' => "生成 {$count} 个验证码"]);
            flash('已生成 ' . count($created) . ' 个验证码');
            redirect('codes', ['show' => implode(',', $created)]);
            break;

        case 'toggle_code':
            $id = (int) ($_POST['id'] ?? 0);
            $row = db_one('SELECT * FROM codes WHERE id = ?', [$id]);
            if ($row !== null) {
                $next = $row['status'] === 'disabled'
                    ? ((int) $row['activated_at'] > 0 ? 'active' : 'unused')
                    : 'disabled';
                db_exec('UPDATE codes SET status = ? WHERE id = ?', [$next, $id]);
                if ($next === 'disabled') {
                    db_exec('DELETE FROM tokens WHERE code_id = ?', [$id]);
                }
                log_event('admin.toggle', ['code_id' => $id, 'detail' => $next]);
                flash($next === 'disabled' ? '已停用该验证码' : '已恢复该验证码');
            }
            redirect('codes');
            break;

        case 'unbind':
            $id = (int) ($_POST['id'] ?? 0);
            $n = unbind_devices($id);
            flash("已解除 {$n} 台设备的绑定，到期时间不变");
            redirect('codes');
            break;

        case 'extend':
            $id = (int) ($_POST['id'] ?? 0);
            $days = max(1, min(3650, (int) ($_POST['days'] ?? 30)));
            extend_code($id, $days);
            flash("已续期 {$days} 天");
            redirect('codes');
            break;

        case 'delete_code':
            $id = (int) ($_POST['id'] ?? 0);
            db_exec('DELETE FROM devices WHERE code_id = ?', [$id]);
            db_exec('DELETE FROM tokens WHERE code_id = ?', [$id]);
            db_exec('DELETE FROM codes WHERE id = ?', [$id]);
            log_event('admin.delete', ['code_id' => $id]);
            flash('已删除该验证码');
            redirect('codes');
            break;

        case 'publish':
            $version = trim((string) ($_POST['version'] ?? ''));
            if (!preg_match('/^\d+(\.\d+){1,3}$/', $version)) {
                throw new RuntimeException('版本号格式应为 2.19.0.0');
            }
            $fileKey = (string) ($_POST['existing_file'] ?? '');
            $storedName = '';
            $displayName = '';
            $size = 0;

            if (!empty($_FILES['package']['tmp_name']) && is_uploaded_file($_FILES['package']['tmp_name'])) {
                $original = basename((string) $_FILES['package']['name']);
                $ext = strtolower(pathinfo($original, PATHINFO_EXTENSION));
                if (!in_array($ext, ['exe', 'zip'], true)) {
                    throw new RuntimeException('只接受 .exe 或 .zip');
                }
                $storedName = bin2hex(random_bytes(12)) . '.' . $ext . '.bin';
                $target = release_dir() . '/' . $storedName;
                if (!move_uploaded_file($_FILES['package']['tmp_name'], $target)) {
                    throw new RuntimeException('保存上传文件失败，请检查 data/releases 目录权限');
                }
                $displayName = $original;
                $size = (int) filesize($target);
            } elseif ($fileKey !== '') {
                $safe = basename($fileKey);
                $target = release_dir() . '/' . $safe;
                if (!is_file($target)) {
                    throw new RuntimeException('服务器上找不到该文件：' . $safe);
                }
                $storedName = $safe;
                $displayName = preg_replace('/\.bin$/', '', $safe) ?: $safe;
                $size = (int) filesize($target);
            } else {
                throw new RuntimeException('请上传发布包，或从服务器已有文件中选择');
            }

            if ($size <= 0) {
                throw new RuntimeException('发布包为空');
            }
            $sha = hash_file('sha256', $target) ?: '';
            $notes = (string) ($_POST['notes'] ?? '');
            $title = trim((string) ($_POST['title'] ?? '')) ?: ('RMCL ' . $version);
            $channel = preg_replace('/[^a-z]/', '', strtolower((string) ($_POST['channel'] ?? 'stable'))) ?: 'stable';
            $status = ($_POST['status'] ?? 'published') === 'draft' ? 'draft' : 'published';

            db_exec(
                'INSERT INTO releases (version, sort_key, title, notes, file_name, stored_name, size, sha256, channel, status, published_at)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                [$version, version_sort_key($version), $title, $notes, $displayName, $storedName, $size, $sha, $channel, $status, time()]
            );
            log_event('admin.release', ['detail' => "发布 {$version}（{$displayName}，" . round($size / 1048576, 2) . 'MB）']);
            flash("已发布 {$version}，sha256 " . substr($sha, 0, 12) . '…');
            redirect('releases');
            break;

        case 'release_status':
            $id = (int) ($_POST['id'] ?? 0);
            $row = db_one('SELECT * FROM releases WHERE id = ?', [$id]);
            if ($row !== null) {
                $next = $row['status'] === 'published' ? 'draft' : 'published';
                db_exec('UPDATE releases SET status = ? WHERE id = ?', [$next, $id]);
                flash($next === 'published' ? '已上架该版本' : '已下架该版本');
            }
            redirect('releases');
            break;

        case 'release_delete':
            $id = (int) ($_POST['id'] ?? 0);
            $row = db_one('SELECT * FROM releases WHERE id = ?', [$id]);
            if ($row !== null) {
                $path = release_dir() . '/' . basename((string) $row['stored_name']);
                if (is_file($path)) {
                    @unlink($path);
                }
                db_exec('DELETE FROM releases WHERE id = ?', [$id]);
                log_event('admin.release_delete', ['detail' => '删除 ' . $row['version']]);
                flash('已删除该版本');
            }
            redirect('releases');
            break;
    }
} catch (ApiError $e) {
    flash($e->getMessage(), 'error');
    redirect('codes');
} catch (Throwable $e) {
    flash($e->getMessage(), 'error');
    redirect('codes');
}

// 没有动作时落到下面的视图渲染
$_GET['tab'] = $_GET['tab'] ?? 'codes';
require __DIR__ . '/lib/admin_view.php';
