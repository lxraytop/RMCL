<?php
/** 管理后台登录 / 首次设置密码（由 admin.php 在未登录时引入） */

$needSetup = current_hash() === '';
$message = take_flash();
?>
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title><?= $needSetup ? '设置管理员密码' : '登录' ?> · <?= h(cfg('app_name')) ?></title>
<style>
    :root { color-scheme: light; }
    * { box-sizing: border-box; }
    body {
        margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
        background: #f5f6f8; color: #1c2027;
        font: 14px/1.65 -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif;
    }
    .card {
        width: 360px; background: #fff; border-radius: 16px; padding: 32px 30px 26px;
        box-shadow: 0 10px 34px rgba(22, 30, 46, .08);
    }
    h1 { margin: 0 0 4px; font-size: 18px; font-weight: 600; }
    p.sub { margin: 0 0 22px; color: #6b7482; font-size: 12.5px; }
    label { display: block; font-size: 12.5px; color: #4a5260; margin-bottom: 6px; }
    input[type=password] {
        width: 100%; height: 38px; padding: 0 12px; border: 1px solid #dfe3e9; border-radius: 9px;
        font-size: 14px; background: #fbfcfe; outline: none; transition: border-color .15s, background .15s;
    }
    input[type=password]:focus { border-color: #7aa7ff; background: #fff; }
    button {
        width: 100%; height: 38px; margin-top: 16px; border: 0; border-radius: 9px;
        background: #3d7eff; color: #fff; font-size: 14px; font-weight: 500; cursor: pointer;
    }
    button:hover { background: #2f6cf0; }
    .flash { margin-bottom: 14px; padding: 8px 12px; border-radius: 8px; font-size: 12.5px; }
    .flash.error { background: #fdecec; color: #c0392b; }
    .flash.ok { background: #eaf7ee; color: #1e7a3c; }
    .tip { margin-top: 14px; font-size: 11.5px; color: #8b94a3; line-height: 1.6; }
</style>
</head>
<body>
<form class="card" method="post" action="admin.php">
    <h1><?= $needSetup ? '设置管理员密码' : '管理后台登录' ?></h1>
    <p class="sub"><?= h(cfg('app_name')) ?></p>
    <?php if ($message): ?>
        <div class="flash <?= h($message['type']) ?>"><?= h($message['message']) ?></div>
    <?php endif; ?>
    <input type="hidden" name="do" value="<?= $needSetup ? 'setup' : 'login' ?>">
    <label for="password">密码</label>
    <input id="password" type="password" name="password" autocomplete="current-password" autofocus required minlength="6">
    <button type="submit"><?= $needSetup ? '保存并进入' : '登录' ?></button>
    <?php if ($needSetup): ?>
        <div class="tip">首次使用：密码会以哈希形式保存在 data/admin.hash，请设置后牢记。
            也可以在 config.php 的 admin_password_hash 里直接指定。</div>
    <?php endif; ?>
</form>
</body>
</html>
