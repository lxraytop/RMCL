<?php
/**
 * RMCL 更新服务 —— 单入口
 *
 * 接口一览（全部返回 {ok:true,data:{...}} 或 {ok:false,error:{code,message}}）：
 *   GET  ?action=health                         服务健康检查，不需要授权
 *   POST ?action=activate    code,device_id,device_name,app_version
 *   POST ?action=verify      token,device_id
 *   GET  ?action=latest      token,device_id[,channel]
 *   POST ?action=ticket      token,device_id,release_id
 *   GET  ?action=download    ticket               支持 Range，供多线程分段下载
 *
 * 也支持美化路径：/api/{action}、/download/{ticket}（需要 .htaccess 或等价的 nginx 规则）
 */

declare(strict_types=1);

require __DIR__ . '/lib/core.php';
require __DIR__ . '/lib/license.php';

/** 合并 JSON body / POST / GET 的入参 */
function input(string $key, $default = '')
{
    static $body = null;
    if ($body === null) {
        $body = [];
        $raw = file_get_contents('php://input');
        $type = $_SERVER['CONTENT_TYPE'] ?? '';
        if ($raw !== '' && (stripos($type, 'application/json') !== false || str_starts_with(ltrim($raw), '{'))) {
            $decoded = json_decode($raw, true);
            if (is_array($decoded)) {
                $body = $decoded;
            }
        }
    }
    if (array_key_exists($key, $body)) {
        return $body[$key];
    }
    if (array_key_exists($key, $_POST)) {
        return $_POST[$key];
    }
    if (array_key_exists($key, $_GET)) {
        return $_GET[$key];
    }
    return $default;
}

/** 解析出动作名，兼容 ?action= 与美化路径 */
function resolve_action(): string
{
    $action = trim((string) ($_GET['action'] ?? ''));
    if ($action !== '') {
        return strtolower($action);
    }

    $path = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?? '/';
    $scriptDir = rtrim(str_replace('\\', '/', dirname($_SERVER['SCRIPT_NAME'] ?? '/')), '/');
    if ($scriptDir !== '' && $scriptDir !== '/' && str_starts_with($path, $scriptDir)) {
        $path = substr($path, strlen($scriptDir));
    }
    $path = '/' . trim($path, '/');

    if (preg_match('#^/api/([a-z]+)$#i', $path, $m)) {
        return strtolower($m[1]);
    }
    if (preg_match('#^/download/([A-Fa-f0-9]+)$#', $path, $m)) {
        $_GET['ticket'] = $m[1];
        return 'download';
    }
    return $path === '/' ? 'home' : 'unknown';
}

/** 把 releases 表的一行整理成客户端要的结构 */
function release_payload(array $rel): array
{
    return [
        'id' => (int) $rel['id'],
        'version' => $rel['version'],
        'title' => $rel['title'],
        'notes' => $rel['notes'],
        'published_at' => (int) $rel['published_at'],
        'downloads' => (int) $rel['downloads'],
        'asset' => [
            'name' => $rel['file_name'],
            'size' => (int) $rel['size'],
            'sha256' => $rel['sha256'],
        ],
    ];
}

/** 输出发布包，支持 Range 请求（客户端是多线程分段下载） */
function serve_file(string $path, string $downloadName, int $declaredSize): void
{
    $size = filesize($path);
    if ($size === false) {
        throw new ApiError('E_FILE', '发布包读取失败，请联系管理员', 500);
    }
    if ($declaredSize > 0 && $declaredSize !== $size) {
        // 数据库里的记录与磁盘不一致时以磁盘为准，但记一笔，方便排查
        log_event('download.size_mismatch', ['detail' => "db={$declaredSize} disk={$size}"]);
    }

    while (ob_get_level() > 0) {
        ob_end_clean();
    }
    ini_set('zlib.output_compression', 'Off');
    @set_time_limit(0);

    $start = 0;
    $end = $size - 1;
    $partial = false;
    if (!empty($_SERVER['HTTP_RANGE']) && preg_match('/bytes=(\d*)-(\d*)/i', (string) $_SERVER['HTTP_RANGE'], $m)) {
        if ($m[1] === '' && $m[2] !== '') {
            $start = max(0, $size - (int) $m[2]);
        } elseif ($m[1] !== '') {
            $start = (int) $m[1];
        }
        if ($m[2] !== '') {
            $end = min((int) $m[2], $size - 1);
        }
        if ($start > $end || $start >= $size) {
            http_response_code(416);
            header('Content-Range: bytes */' . $size);
            return;
        }
        $partial = true;
    }

    $length = $end - $start + 1;

    http_response_code($partial ? 206 : 200);
    header('Content-Type: application/octet-stream');
    header('Content-Disposition: attachment; filename="' . str_replace('"', '', $downloadName) . '"');
    header('Content-Length: ' . $length);
    header('Accept-Ranges: bytes');
    header('Cache-Control: no-store');
    header('X-Accel-Buffering: no');
    if ($partial) {
        header('Content-Range: bytes ' . $start . '-' . $end . '/' . $size);
    }

    if (($_SERVER['REQUEST_METHOD'] ?? 'GET') === 'HEAD') {
        return;
    }

    $fp = fopen($path, 'rb');
    if ($fp === false) {
        return;
    }
    fseek($fp, $start);
    $left = $length;
    while ($left > 0 && !feof($fp)) {
        $chunk = fread($fp, (int) min(262144, $left));
        if ($chunk === false || $chunk === '') {
            break;
        }
        echo $chunk;
        $left -= strlen($chunk);
        flush();
    }
    fclose($fp);
}

function render_home(): void
{
    $latest = null;
    try {
        $latest = db_one("SELECT version, published_at FROM releases WHERE status = 'published' ORDER BY published_at DESC LIMIT 1");
    } catch (Throwable $e) {
        // 数据库还没建好也照样能打开首页
    }
    $name = htmlspecialchars((string) cfg('app_name', 'RMCL 更新服务'), ENT_QUOTES, 'UTF-8');
    $version = $latest !== null ? htmlspecialchars($latest['version'], ENT_QUOTES, 'UTF-8') : '尚未发布';
    header('Content-Type: text/html; charset=utf-8');
    echo <<<HTML
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{$name}</title>
<style>
 body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
      background:#f6f7f9;color:#1c2027;font:15px/1.7 -apple-system,"Segoe UI","Microsoft YaHei",sans-serif}
 .box{background:#fff;border-radius:14px;padding:36px 40px;box-shadow:0 8px 30px rgba(20,26,40,.07);max-width:520px}
 h1{margin:0 0 6px;font-size:20px} .sub{color:#6b7482;font-size:13px;margin-bottom:20px}
 dl{display:grid;grid-template-columns:auto 1fr;gap:6px 16px;margin:0;font-size:13.5px}
 dt{color:#6b7482} dd{margin:0}
 code{background:#f1f3f6;border-radius:4px;padding:1px 6px;font-size:12.5px}
</style></head><body><div class="box">
<h1>{$name}</h1><div class="sub">RMCL 启动器的自建更新源，下载需要有效授权码。</div>
<dl><dt>最新版本</dt><dd>{$version}</dd>
<dt>状态</dt><dd>运行中</dd>
<dt>接口</dt><dd><code>?action=activate</code> / <code>latest</code> / <code>ticket</code></dd></dl>
</div></body></html>
HTML;
}

// ---------------------------------------------------------------- 路由

try {
    $action = resolve_action();

    switch ($action) {
        case 'home':
            render_home();
            break;

        case 'health':
            $latest = db_one("SELECT version, published_at FROM releases WHERE status = 'published' ORDER BY published_at DESC LIMIT 1");
            json_ok([
                'service' => 'rmcl-update',
                'api' => 1,
                'name' => cfg('app_name'),
                'time' => time(),
                'latest' => $latest['version'] ?? null,
            ]);
            break;

        case 'activate':
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
                throw new ApiError('E_METHOD', '请使用 POST', 405);
            }
            $data = activate(
                (string) input('code'),
                (string) input('device_id'),
                (string) input('device_name', '未知设备'),
                (string) input('app_version', '')
            );
            json_ok($data);
            break;

        case 'verify':
            $license = require_license();
            json_ok([
                'code_mask' => mask_code((string) $license['code']['code']),
                'device_id' => $license['device_id'],
                'expire_at' => $license['expire_at'],
                'activated_at' => (int) $license['code']['activated_at'],
                'remaining_days' => remaining_days($license['expire_at']),
            ]);
            break;

        case 'latest':
            $license = require_license();
            $channel = preg_replace('/[^a-z]/', '', strtolower((string) input('channel', 'stable'))) ?: 'stable';
            $rel = db_one(
                "SELECT * FROM releases WHERE channel = ? AND status = 'published' ORDER BY sort_key DESC, id DESC LIMIT 1",
                [$channel]
            );
            if ($rel === null) {
                throw new ApiError('E_NO_RELEASE', '服务器上还没有发布任何版本', 404);
            }
            json_ok([
                'release' => release_payload($rel),
                'license' => [
                    'code_mask' => mask_code((string) $license['code']['code']),
                    'device_id' => $license['device_id'],
                    'expire_at' => $license['expire_at'],
                    'remaining_days' => remaining_days($license['expire_at']),
                ],
            ]);
            break;

        case 'release':
            // 按版本号取发布信息：客户端更新完成、重启后要用它展示「这一版更新了什么」
            $license = require_license();
            $version = ltrim(trim((string) input('version')), 'vV');
            if ($version === '') {
                throw new ApiError('E_PARAM', '缺少 version 参数', 400);
            }
            $rel = db_one(
                "SELECT * FROM releases WHERE version = ? AND status = 'published' ORDER BY id DESC LIMIT 1",
                [$version]
            );
            if ($rel === null) {
                throw new ApiError('E_NO_RELEASE', '服务器上没有 ' . $version . ' 这个版本', 404);
            }
            json_ok([
                'release' => release_payload($rel),
                'license' => [
                    'code_mask' => mask_code((string) $license['code']['code']),
                    'expire_at' => $license['expire_at'],
                    'remaining_days' => remaining_days($license['expire_at']),
                ],
            ]);
            break;

        case 'ticket':
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
                throw new ApiError('E_METHOD', '请使用 POST', 405);
            }
            $license = require_license();
            $releaseId = (int) input('release_id', 0);
            if ($releaseId <= 0) {
                throw new ApiError('E_PARAM', '缺少 release_id', 400);
            }
            json_ok(create_ticket($license, $releaseId));
            break;

        case 'download':
            $ticket = (string) input('ticket');
            $row = consume_ticket($ticket);
            $path = release_dir() . '/' . basename((string) $row['stored_name']);
            if (!is_file($path)) {
                throw new ApiError('E_FILE', '发布包文件丢失，请联系管理员', 404);
            }
            serve_file($path, (string) $row['file_name'], (int) $row['size']);
            break;

        default:
            throw new ApiError('E_NOT_FOUND', '未知接口：' . $action, 404);
    }
} catch (ApiError $e) {
    if ($e->errorCode === 'E_EXPIRED' || $e->errorCode === 'E_TOKEN_INVALID') {
        log_event('api.denied', ['detail' => $e->errorCode . ' ' . $e->getMessage()]);
    }
    json_fail($e->errorCode, $e->getMessage(), $e->httpStatus, $e->payload);
} catch (Throwable $e) {
    error_log('[rmcl-update] ' . $e->getMessage() . ' @ ' . $e->getFile() . ':' . $e->getLine());
    json_fail('E_INTERNAL', '服务器内部错误：' . $e->getMessage(), 500);
}
