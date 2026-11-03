<?php
/** 管理后台主视图（由 admin.php 在已登录后引入，共享其作用域） */

$tab = in_array($_GET['tab'] ?? '', ['codes', 'releases', 'logs'], true) ? $_GET['tab'] : 'codes';
$flash = take_flash();
$csrf = csrf_token();
$siteUrl = base_url();

$stats = db_one("SELECT
        COUNT(*) AS total,
        SUM(CASE WHEN status = 'disabled' THEN 1 ELSE 0 END) AS disabled,
        SUM(CASE WHEN status <> 'disabled' AND activated_at = 0 THEN 1 ELSE 0 END) AS unused,
        SUM(CASE WHEN status <> 'disabled' AND activated_at > 0 AND expire_at > " . time() . " THEN 1 ELSE 0 END) AS active,
        SUM(CASE WHEN activated_at > 0 AND expire_at <= " . time() . " THEN 1 ELSE 0 END) AS expired
    FROM codes") ?: ['total' => 0, 'disabled' => 0, 'unused' => 0, 'active' => 0, 'expired' => 0];

$uploadMax = ini_get('upload_max_filesize');
$postMax = ini_get('post_max_size');
?>
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title><?= h(cfg('app_name')) ?> · 管理后台</title>
<style>
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body { margin: 0; background: #f5f6f8; color: #1c2027;
           font: 14px/1.65 -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif; }
    a { color: #3d7eff; text-decoration: none; }
    header { background: #fff; border-bottom: 1px solid #e9ecf0; padding: 0 28px; }
    .bar { max-width: 1180px; margin: 0 auto; display: flex; align-items: center; gap: 26px; height: 56px; }
    .brand { font-weight: 600; font-size: 15px; }
    nav { display: flex; gap: 4px; flex: 1; }
    nav a { padding: 6px 12px; border-radius: 8px; color: #4a5260; font-size: 13.5px; }
    nav a.on { background: #eef3ff; color: #2f6cf0; font-weight: 500; }
    nav a:hover { background: #f2f4f7; }
    .who { font-size: 12.5px; color: #8b94a3; display: flex; gap: 12px; align-items: center; }
    main { max-width: 1180px; margin: 22px auto 60px; padding: 0 28px; }
    .card { background: #fff; border-radius: 14px; padding: 20px 22px; box-shadow: 0 2px 10px rgba(24,32,48,.045); margin-bottom: 18px; }
    .card h2 { margin: 0 0 4px; font-size: 15px; font-weight: 600; }
    .card p.desc { margin: 0 0 16px; color: #7b8492; font-size: 12.5px; }
    .grid { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); margin-bottom: 18px; }
    .stat { background: #fff; border-radius: 12px; padding: 14px 16px; box-shadow: 0 2px 10px rgba(24,32,48,.045); }
    .stat b { display: block; font-size: 22px; font-weight: 600; line-height: 1.3; }
    .stat span { font-size: 12px; color: #7b8492; }
    table { width: 100%; border-collapse: collapse; font-size: 13px; }
    th { text-align: left; font-weight: 500; color: #7b8492; font-size: 12px; padding: 8px 10px; border-bottom: 1px solid #eef1f5; white-space: nowrap; }
    td { padding: 9px 10px; border-bottom: 1px solid #f4f6f9; vertical-align: middle; }
    tr:last-child td { border-bottom: 0; }
    .mono { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 12.5px; letter-spacing: .3px; }
    .tag { display: inline-block; padding: 1px 8px; border-radius: 20px; font-size: 11.5px; line-height: 18px; }
    .tag.green { background: #eaf7ee; color: #1e7a3c; }
    .tag.red { background: #fdecec; color: #c0392b; }
    .tag.blue { background: #eef3ff; color: #2f6cf0; }
    .tag.gray { background: #f0f2f5; color: #6b7482; }
    .muted { color: #8b94a3; font-size: 12px; }
    form.inline { display: inline; }
    button, .btn { height: 30px; padding: 0 12px; border: 1px solid #dfe3e9; border-radius: 8px;
                  background: #fff; font-size: 12.5px; cursor: pointer; color: #3b424e; }
    button:hover, .btn:hover { background: #f5f7fa; }
    button.primary { background: #3d7eff; border-color: #3d7eff; color: #fff; }
    button.primary:hover { background: #2f6cf0; }
    button.danger { color: #c0392b; border-color: #f0cfcf; }
    button.danger:hover { background: #fdf1f1; }
    input[type=text], input[type=number], input[type=date], select, textarea {
        height: 32px; padding: 0 10px; border: 1px solid #dfe3e9; border-radius: 8px;
        font-size: 13px; background: #fbfcfe; outline: none; font-family: inherit;
    }
    textarea { height: auto; padding: 8px 10px; line-height: 1.6; resize: vertical; }
    input:focus, select:focus, textarea:focus { border-color: #7aa7ff; background: #fff; }
    .row { display: flex; flex-wrap: wrap; gap: 10px; align-items: flex-end; }
    .field { display: flex; flex-direction: column; gap: 5px; }
    .field label { font-size: 12px; color: #6b7482; }
    .flash { padding: 9px 14px; border-radius: 9px; font-size: 13px; margin-bottom: 16px; }
    .flash.ok { background: #eaf7ee; color: #1e7a3c; }
    .flash.error { background: #fdecec; color: #c0392b; }
    .codes-out { width: 100%; min-height: 120px; font-family: ui-monospace, Consolas, monospace; font-size: 13px; }
    .pager { display: flex; gap: 8px; align-items: center; margin-top: 14px; font-size: 12.5px; color: #7b8492; }
    .dev { font-size: 12px; color: #5c6572; }
    details summary { cursor: pointer; color: #7b8492; font-size: 12px; }
</style>
</head>
<body>
<header>
    <div class="bar">
        <div class="brand"><?= h(cfg('app_name')) ?></div>
        <nav>
            <a href="admin.php?tab=codes" class="<?= $tab === 'codes' ? 'on' : '' ?>">验证码</a>
            <a href="admin.php?tab=releases" class="<?= $tab === 'releases' ? 'on' : '' ?>">版本发布</a>
            <a href="admin.php?tab=logs" class="<?= $tab === 'logs' ? 'on' : '' ?>">日志</a>
        </nav>
        <div class="who">
            <span class="mono"><?= h($siteUrl) ?></span>
            <a href="admin.php?do=logout">退出</a>
        </div>
    </div>
</header>
<main>
<?php if ($flash): ?>
    <div class="flash <?= h($flash['type']) ?>"><?= h($flash['message']) ?></div>
<?php endif; ?>

<?php if ($tab === 'codes'): ?>
    <div class="grid">
        <div class="stat"><b><?= (int) $stats['total'] ?></b><span>验证码总数</span></div>
        <div class="stat"><b><?= (int) $stats['active'] ?></b><span>生效中</span></div>
        <div class="stat"><b><?= (int) $stats['unused'] ?></b><span>未使用</span></div>
        <div class="stat"><b><?= (int) $stats['expired'] ?></b><span>已过期</span></div>
        <div class="stat"><b><?= (int) $stats['disabled'] ?></b><span>已停用</span></div>
    </div>

    <div class="card">
        <h2>生成验证码</h2>
        <p class="desc">一码默认绑定一台设备；有效期可选「激活后起算 N 天」或「固定到期日」。</p>
        <form method="post" action="admin.php">
            <input type="hidden" name="do" value="create_codes">
            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
            <div class="row">
                <div class="field"><label>数量</label>
                    <input type="number" name="count" value="1" min="1" max="200" style="width:90px"></div>
                <div class="field"><label>有效期模式</label>
                    <select name="mode" id="modeSel">
                        <option value="day">激活后起算天数</option>
                        <option value="date">固定到期日</option>
                    </select></div>
                <div class="field" id="daysField"><label>有效天数</label>
                    <input type="number" name="days" value="30" min="1" max="3650" style="width:110px"></div>
                <div class="field" id="dateField" style="display:none"><label>到期日期</label>
                    <input type="date" name="fixed_expire" value="<?= date('Y-m-d', strtotime('+1 year')) ?>"></div>
                <div class="field"><label>可绑定设备数</label>
                    <input type="number" name="device_limit" value="1" min="1" max="20" style="width:110px"></div>
                <div class="field" style="flex:1;min-width:180px"><label>备注（买家 / 用途）</label>
                    <input type="text" name="note" placeholder="选填" style="width:100%"></div>
                <button class="primary" type="submit">生成</button>
            </div>
        </form>
        <script>
            const modeSel = document.getElementById('modeSel');
            modeSel.addEventListener('change', () => {
                const isDate = modeSel.value === 'date';
                document.getElementById('daysField').style.display = isDate ? 'none' : '';
                document.getElementById('dateField').style.display = isDate ? '' : 'none';
            });
        </script>
    </div>

    <?php if (!empty($_GET['show'])): ?>
        <div class="card">
            <h2>刚生成的验证码</h2>
            <p class="desc">复制后发给用户；同一个码只能绑一台设备（除非上调了设备数）。</p>
            <textarea class="codes-out" readonly onclick="this.select()"><?php
                foreach (array_filter(explode(',', (string) $_GET['show'])) as $c) {
                    echo h($c) . "\n";
                }
            ?></textarea>
        </div>
    <?php endif; ?>

    <div class="card">
        <h2>验证码列表</h2>
        <form method="get" action="admin.php" style="margin-bottom:14px">
            <input type="hidden" name="tab" value="codes">
            <div class="row">
                <input type="text" name="q" value="<?= h((string) ($_GET['q'] ?? '')) ?>" placeholder="搜索验证码或备注" style="width:240px">
                <button type="submit">搜索</button>
                <?php if (!empty($_GET['q'])): ?><a class="btn" href="admin.php?tab=codes" style="line-height:30px">清空</a><?php endif; ?>
            </div>
        </form>

        <?php
        $q = trim((string) ($_GET['q'] ?? ''));
        $page = max(1, (int) ($_GET['page'] ?? 1));
        $per = 30;
        $offset = ($page - 1) * $per;
        $where = '';
        $params = [];
        if ($q !== '') {
            $where = 'WHERE code LIKE ? OR note LIKE ?';
            $params = ['%' . $q . '%', '%' . $q . '%'];
        }
        $totalRow = db_one("SELECT COUNT(*) AS n FROM codes {$where}", $params);
        $total = (int) ($totalRow['n'] ?? 0);
        $pages = max(1, (int) ceil($total / $per));
        $rows = db_all("SELECT * FROM codes {$where} ORDER BY id DESC LIMIT {$per} OFFSET {$offset}", $params);
        ?>

        <table>
            <thead><tr>
                <th>验证码</th><th>备注</th><th>有效期</th><th>状态</th><th>绑定设备</th><th>到期时间</th><th>操作</th>
            </tr></thead>
            <tbody>
            <?php if (!$rows): ?>
                <tr><td colspan="7" class="muted">还没有验证码，先在上方生成一批。</td></tr>
            <?php endif; ?>
            <?php foreach ($rows as $row):
                [$label, $color] = code_status_label($row);
                $devices = db_all('SELECT * FROM devices WHERE code_id = ? ORDER BY last_seen DESC', [(int) $row['id']]);
                $expireAt = (int) $row['expire_at'];
                $leftDays = $expireAt > 0 ? remaining_days($expireAt) : 0;
            ?>
                <tr>
                    <td>
                        <span class="mono" id="code-<?= (int) $row['id'] ?>"><?= h($row['code']) ?></span>
                        <button type="button" class="btn" style="height:22px;padding:0 7px;font-size:11px"
                                onclick="navigator.clipboard.writeText('<?= h($row['code']) ?>').then(()=>{this.textContent='已复制';setTimeout(()=>this.textContent='复制',1200)})">复制</button>
                    </td>
                    <td class="muted"><?= h($row['note']) ?: '—' ?></td>
                    <td class="muted"><?= $row['mode'] === 'date'
                        ? h($row['fixed_expire']) . '<br>固定到期日'
                        : '激活后 ' . (int) $row['days'] . ' 天' ?></td>
                    <td><span class="tag <?= $color ?>"><?= $label ?></span></td>
                    <td>
                        <?php if (!$devices): ?>
                            <span class="muted">未绑定</span>
                        <?php else: foreach ($devices as $d): ?>
                            <div class="dev">
                                <?= h($d['device_name'] ?: '未知设备') ?>
                                <span class="muted">（<?= h(substr((string) $d['device_id'], 0, 10)) ?>…）</span>
                            </div>
                        <?php endforeach; endif; ?>
                    </td>
                    <td class="muted">
                        <?php if ($expireAt > 0): ?>
                            <?= h(fmt_time($expireAt)) ?><br>
                            <?= $leftDays > 0 ? '剩余 ' . $leftDays . ' 天' : '已到期' ?>
                        <?php else: ?>—<?php endif; ?>
                    </td>
                    <td style="white-space:nowrap">
                        <form class="inline" method="post" action="admin.php">
                            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                            <input type="hidden" name="id" value="<?= (int) $row['id'] ?>">
                            <input type="hidden" name="do" value="toggle_code">
                            <button type="submit"><?= $row['status'] === 'disabled' ? '恢复' : '停用' ?></button>
                        </form>
                        <form class="inline" method="post" action="admin.php">
                            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                            <input type="hidden" name="id" value="<?= (int) $row['id'] ?>">
                            <input type="hidden" name="do" value="unbind">
                            <button type="submit" onclick="return confirm('解除该验证码的全部设备绑定？换机时使用，到期时间不变。')">解绑</button>
                        </form>
                        <details style="display:inline-block">
                            <summary>更多</summary>
                            <div style="margin-top:6px;display:flex;gap:6px">
                                <form class="inline" method="post" action="admin.php">
                                    <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                    <input type="hidden" name="id" value="<?= (int) $row['id'] ?>">
                                    <input type="hidden" name="do" value="extend">
                                    <input type="number" name="days" value="30" min="1" style="width:64px;height:26px">
                                    <button type="submit" style="height:26px">续期</button>
                                </form>
                                <form class="inline" method="post" action="admin.php">
                                    <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                    <input type="hidden" name="id" value="<?= (int) $row['id'] ?>">
                                    <input type="hidden" name="do" value="delete_code">
                                    <button type="submit" class="danger" style="height:26px"
                                            onclick="return confirm('删除后不可恢复，确定？')">删除</button>
                                </form>
                            </div>
                        </details>
                    </td>
                </tr>
            <?php endforeach; ?>
            </tbody>
        </table>

        <?php if ($pages > 1): ?>
            <div class="pager">
                <span>共 <?= $total ?> 条 · 第 <?= $page ?> / <?= $pages ?> 页</span>
                <?php if ($page > 1): ?><a class="btn" href="admin.php?tab=codes&page=<?= $page - 1 ?>&q=<?= urlencode($q) ?>" style="line-height:30px">上一页</a><?php endif; ?>
                <?php if ($page < $pages): ?><a class="btn" href="admin.php?tab=codes&page=<?= $page + 1 ?>&q=<?= urlencode($q) ?>" style="line-height:30px">下一页</a><?php endif; ?>
            </div>
        <?php endif; ?>
    </div>

<?php elseif ($tab === 'releases'): ?>
    <div class="card">
        <h2>发布新版本</h2>
        <p class="desc">
            客户端按版本号比较大小，取 channel=stable 里版本号最大的已上架版本。
            当前 PHP 上传上限：upload_max_filesize = <?= h((string) $uploadMax) ?>，post_max_size = <?= h((string) $postMax) ?>。
            超过上限时请用 FTP 把文件传到 <span class="mono">data/releases/</span>，再从下面的列表里选择。
        </p>
        <form method="post" action="admin.php" enctype="multipart/form-data">
            <input type="hidden" name="do" value="publish">
            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
            <div class="row" style="margin-bottom:12px">
                <div class="field"><label>版本号</label>
                    <input type="text" name="version" placeholder="2.19.0.0" required style="width:140px"></div>
                <div class="field"><label>标题</label>
                    <input type="text" name="title" placeholder="留空自动填 RMCL x.y.z" style="width:220px"></div>
                <div class="field"><label>渠道</label>
                    <select name="channel"><option value="stable">stable</option><option value="beta">beta</option></select></div>
                <div class="field"><label>状态</label>
                    <select name="status"><option value="published">立即上架</option><option value="draft">草稿</option></select></div>
            </div>
            <div class="row" style="margin-bottom:12px">
                <div class="field" style="flex:1;min-width:260px"><label>上传发布包（.exe / .zip）</label>
                    <input type="file" name="package" accept=".exe,.zip" style="height:auto;padding:5px;background:#fff;border:1px solid #dfe3e9;border-radius:8px"></div>
                <div class="field"><label>或选择服务器已有文件</label>
                    <select name="existing_file" style="min-width:240px">
                        <option value="">— 不使用 —</option>
                        <?php
                        $files = @scandir(release_dir()) ?: [];
                        foreach ($files as $f) {
                            if ($f === '.' || $f === '..' || str_starts_with($f, '.')) {
                                continue;
                            }
                            $p = release_dir() . '/' . $f;
                            if (!is_file($p)) {
                                continue;
                            }
                            echo '<option value="' . h($f) . '">' . h($f) . '（' . round(filesize($p) / 1048576, 2) . ' MB）</option>';
                        }
                        ?>
                    </select></div>
            </div>
            <div class="field" style="margin-bottom:12px"><label>更新公告（纯文本，客户端弹窗直接展示）</label>
                <textarea name="notes" rows="6" style="width:100%" placeholder="· 修复 xxx&#10;· 新增 xxx"></textarea></div>
            <button class="primary" type="submit">发布</button>
        </form>
    </div>

    <div class="card">
        <h2>已发布版本</h2>
        <?php
        $rels = db_all('SELECT * FROM releases ORDER BY sort_key DESC, id DESC LIMIT 50');
        $dl = [];
        foreach (db_all('SELECT release_id, SUM(uses) AS u FROM tickets GROUP BY release_id') as $r) {
            $dl[(int) $r['release_id']] = (int) $r['u'];
        }
        ?>
        <table>
            <thead><tr><th>版本</th><th>状态</th><th>文件</th><th>大小</th><th>sha256</th><th>发布时间</th><th>下载段数</th><th>操作</th></tr></thead>
            <tbody>
            <?php if (!$rels): ?>
                <tr><td colspan="8" class="muted">还没有发布任何版本，客户端会提示「服务器上还没有发布任何版本」。</td></tr>
            <?php endif; ?>
            <?php foreach ($rels as $r): ?>
                <tr>
                    <td><b><?= h($r['version']) ?></b><div class="muted"><?= h($r['channel']) ?></div></td>
                    <td><span class="tag <?= $r['status'] === 'published' ? 'green' : 'gray' ?>"><?= $r['status'] === 'published' ? '已上架' : '草稿' ?></span></td>
                    <td class="mono"><?= h($r['file_name']) ?></td>
                    <td class="muted"><?= round(((int) $r['size']) / 1048576, 2) ?> MB</td>
                    <td class="mono muted"><?= h(substr((string) $r['sha256'], 0, 12)) ?>…</td>
                    <td class="muted"><?= h(fmt_time((int) $r['published_at'])) ?></td>
                    <td class="muted"><?= $dl[(int) $r['id']] ?? 0 ?></td>
                    <td style="white-space:nowrap">
                        <form class="inline" method="post" action="admin.php">
                            <input type="hidden" name="do" value="release_status">
                            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                            <input type="hidden" name="id" value="<?= (int) $r['id'] ?>">
                            <button type="submit"><?= $r['status'] === 'published' ? '下架' : '上架' ?></button>
                        </form>
                        <form class="inline" method="post" action="admin.php">
                            <input type="hidden" name="do" value="release_delete">
                            <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                            <input type="hidden" name="id" value="<?= (int) $r['id'] ?>">
                            <button type="submit" class="danger" onclick="return confirm('同时删除服务器上的文件，确定？')">删除</button>
                        </form>
                    </td>
                </tr>
            <?php endforeach; ?>
            </tbody>
        </table>
    </div>

<?php else: ?>
    <div class="card">
        <h2>最近日志</h2>
        <p class="desc">激活成功 / 失败、票据签发与后台操作。只保留最近 200 条。</p>
        <table>
            <thead><tr><th>时间</th><th>事件</th><th>码 ID</th><th>设备</th><th>IP</th><th>详情</th></tr></thead>
            <tbody>
            <?php $logs = db_all('SELECT * FROM logs ORDER BY id DESC LIMIT 200'); ?>
            <?php if (!$logs): ?><tr><td colspan="6" class="muted">暂无日志。</td></tr><?php endif; ?>
            <?php foreach ($logs as $l): ?>
                <tr>
                    <td class="muted" style="white-space:nowrap"><?= h(date('m-d H:i:s', (int) $l['ts'])) ?></td>
                    <td><span class="tag <?= str_contains((string) $l['event'], 'fail') ? 'red' : (str_starts_with((string) $l['event'], 'admin') ? 'blue' : 'gray') ?>"><?= h($l['event']) ?></span></td>
                    <td class="muted"><?= (int) $l['code_id'] ?: '—' ?></td>
                    <td class="mono muted"><?= h(substr((string) $l['device_id'], 0, 10)) ?: '—' ?></td>
                    <td class="mono muted"><?= h($l['ip']) ?></td>
                    <td class="muted"><?= h($l['detail']) ?></td>
                </tr>
            <?php endforeach; ?>
            </tbody>
        </table>
    </div>
<?php endif; ?>
</main>
</body>
</html>
