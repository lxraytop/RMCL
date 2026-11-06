<?php
/**
 * 兼容入口：好友管理已经集成到 admin.php 的「好友」标签页。
 * 老书签 / 旧链接访问这里时直接跳转过去。
 */

declare(strict_types=1);

require __DIR__ . '/lib/core.php';

session_name('rmcl_admin');
session_set_cookie_params([
    'httponly' => true,
    'samesite' => 'Lax',
    'secure' => (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off'),
]);
session_start();

$target = !empty($_SESSION['rmcl_admin'])
    ? 'admin.php?tab=friends'
    : 'admin.php';
header('Location: ' . $target, true, 302);
exit;
