<?php
/**
 * 部署自检。部署完成、登录过管理后台之后访问本文件，会逐项检查环境与授权链路。
 *
 * 它会临时创建一张 1 天期的验证码、绑定一台测试设备、取最新版本、签一张票据、真的下载 16 字节，
 * 结束后把所有临时数据删掉，不会污染正式数据。
 *
 * 检查完建议改名为 selftest.php.bak 或直接删除。
 */

declare(strict_types=1);

require __DIR__ . '/lib/core.php';
require __DIR__ . '/lib/license.php';

session_name('rmcl_admin');
session_set_cookie_params(['httponly' => true, 'samesite' => 'Lax']);
session_start();

$authorized = !empty($_SESSION['rmcl_admin']);
$checks = [];

function add_check(string $name, bool $ok, string $detail = ''): void
{
    global $checks;
    $checks[] = ['name' => $name, 'ok' => $ok, 'detail' => $detail];
}

if ($authorized) {
    // 1. 运行环境
    add_check('PHP 版本', PHP_VERSION_ID >= 80000, PHP_VERSION . '（需要 8.0+）');
    add_check('PDO SQLite', extension_loaded('pdo_sqlite'), extension_loaded('pdo_sqlite') ? '已开启' : '缺失：需要在主机面板里启用 pdo_sqlite');
    add_check('mbstring', extension_loaded('mbstring'), extension_loaded('mbstring') ? '已开启' : '未开启（不致命，截断中文设备名会按字节）');
    add_check('data 目录可写', is_writable(data_dir()), data_dir());
    add_check('releases 目录可写', is_writable(release_dir()), release_dir());
    add_check('站点地址', true, base_url());
    add_check('上传上限', true, 'upload_max_filesize=' . ini_get('upload_max_filesize') . '，post_max_size=' . ini_get('post_max_size'));

    // 2. 数据库
    try {
        $row = db_one('SELECT COUNT(*) AS n FROM codes');
        add_check('数据库读写', true, '验证码 ' . (int) ($row['n'] ?? 0) . ' 条');
    } catch (Throwable $e) {
        add_check('数据库读写', false, $e->getMessage());
    }

    // 3. 授权链路
    $testCode = '';
    $testDevice = 'selftest-' . bin2hex(random_bytes(4));
    try {
        $testCode = gen_code();
        db_exec(
            'INSERT INTO codes (code, note, mode, days, device_limit, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$testCode, '自检临时码（可删）', 'day', 1, 1, 'unused', time()]
        );

        $license = activate($testCode, $testDevice, '自检设备', 'selftest');
        add_check('激活验证码', true, '到期 ' . date('Y-m-d H:i', $license['expire_at']) . '，剩余 ' . $license['remaining_days'] . ' 天');

        $context = verify_token($license['token'], $testDevice, false);
        add_check('校验令牌', true, '剩余 ' . remaining_days($context['expire_at']) . ' 天');

        // 换个设备再来一次，应当被拒绝（验证一码一设备）
        try {
            activate($testCode, $testDevice . '-other', '另一台设备', 'selftest');
            add_check('设备绑定拦截', false, '同一张码在第二台设备上也能激活，绑定逻辑有问题');
        } catch (ApiError $e) {
            add_check('设备绑定拦截', $e->errorCode === 'E_DEVICE_BOUND', $e->getMessage());
        }

        $release = db_one("SELECT * FROM releases WHERE status = 'published' ORDER BY sort_key DESC, id DESC LIMIT 1");
        if ($release === null) {
            add_check('已发布版本', false, '还没有发布任何版本，客户端会提示「服务器上还没有发布任何版本」');
        } else {
            add_check('已发布版本', true, $release['version'] . '（' . round(((int) $release['size']) / 1048576, 2) . ' MB）');

            $ticket = create_ticket($context, (int) $release['id']);
            add_check('签发下载票据', true, substr($ticket['ticket'], 0, 12) . '…（10 分钟有效）');

            $body = @file_get_contents($ticket['url'], false, stream_context_create([
                'http' => ['timeout' => 8, 'header' => "Range: bytes=0-15\r\n", 'ignore_errors' => true],
                'ssl' => ['verify_peer' => false, 'verify_peer_name' => false],
            ]));
            if ($body === false) {
                add_check('下载地址可达', false, '请求失败，检查 base_url 是否正确、站点能否被外部访问：' . $ticket['url']);
            } else {
                add_check('下载地址可达', strlen($body) > 0, '通过 Range 请求拿到 ' . strlen($body) . ' 字节');
            }
        }
    } catch (Throwable $e) {
        add_check('授权链路', false, $e->getMessage());
    } finally {
        if ($testCode !== '') {
            try {
                $row = db_one('SELECT id FROM codes WHERE code = ?', [$testCode]);
                if ($row !== null) {
                    $id = (int) $row['id'];
                    db_exec('DELETE FROM devices WHERE code_id = ?', [$id]);
                    db_exec('DELETE FROM tokens WHERE code_id = ?', [$id]);
                    db_exec('DELETE FROM tickets WHERE code_id = ?', [$id]);
                    db_exec('DELETE FROM codes WHERE id = ?', [$id]);
                }
            } catch (Throwable $e) {
                // 清理失败不影响结论
            }
        }
    }
}

$failed = count(array_filter($checks, fn($c) => !$c['ok']));
?>
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>部署自检 · <?= htmlspecialchars((string) cfg('app_name'), ENT_QUOTES, 'UTF-8') ?></title>
<style>
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body { margin: 0; padding: 40px 20px; background: #f5f6f8; color: #1c2027;
           font: 14px/1.65 -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif; }
    .wrap { max-width: 820px; margin: 0 auto; background: #fff; border-radius: 16px; padding: 28px 30px;
            box-shadow: 0 10px 34px rgba(22,30,46,.07); }
    h1 { margin: 0 0 4px; font-size: 19px; }
    p.sub { margin: 0 0 20px; color: #7b8492; font-size: 12.5px; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th { text-align: left; font-size: 12px; color: #7b8492; font-weight: 500; padding: 8px 10px; border-bottom: 1px solid #eef1f5; }
    td { padding: 9px 10px; border-bottom: 1px solid #f4f6f9; vertical-align: top; }
    tr:last-child td { border-bottom: 0; }
    .ok { color: #1e7a3c; font-weight: 500; white-space: nowrap; }
    .bad { color: #c0392b; font-weight: 500; white-space: nowrap; }
    .detail { color: #6b7482; font-size: 12.5px; word-break: break-all; }
    .sum { margin-top: 18px; padding: 12px 14px; border-radius: 10px; font-size: 13px; }
    .sum.good { background: #eaf7ee; color: #1e7a3c; }
    .sum.bad { background: #fdecec; color: #c0392b; }
    .tip { margin-top: 16px; font-size: 12px; color: #8b94a3; line-height: 1.7; }
    code { background: #f1f3f6; border-radius: 4px; padding: 1px 6px; font-size: 12px; }
    a { color: #3d7eff; text-decoration: none; }
</style>
</head>
<body>
<div class="wrap">
    <h1>部署自检</h1>
    <p class="sub"><?= htmlspecialchars((string) cfg('app_name'), ENT_QUOTES, 'UTF-8') ?></p>

    <?php if (!$authorized): ?>
        <div class="sum bad">请先登录管理后台，再回来运行自检。</div>
        <p class="tip"><a href="admin.php">前往 admin.php 登录</a></p>
    <?php else: ?>
        <table>
            <thead><tr><th style="width:150px">项目</th><th style="width:80px">结果</th><th>详情</th></tr></thead>
            <tbody>
            <?php foreach ($checks as $check): ?>
                <tr>
                    <td><?= htmlspecialchars($check['name'], ENT_QUOTES, 'UTF-8') ?></td>
                    <td class="<?= $check['ok'] ? 'ok' : 'bad' ?>"><?= $check['ok'] ? '通过' : '需处理' ?></td>
                    <td class="detail"><?= htmlspecialchars($check['detail'], ENT_QUOTES, 'UTF-8') ?></td>
                </tr>
            <?php endforeach; ?>
            </tbody>
        </table>

        <div class="sum <?= $failed === 0 ? 'good' : 'bad' ?>">
            <?= $failed === 0
                ? '全部通过。接下来把服务器地址填进启动器「设置 → 功能 → 软件更新 → 更新源 → 官方源」，再用后台签发的验证码激活即可。'
                : "有 {$failed} 项需要处理，按上面的详情逐项排查。" ?>
        </div>

        <p class="tip">
            自检会临时建一张验证码并实际请求一次下载，结束后已清理干净。<br>
            检查完建议把 <code>selftest.php</code> 改名或删除，避免暴露环境信息。
        </p>
    <?php endif; ?>
</div>
</body>
</html>
