<?php
/**
 * 好友管理（集成进 admin.php 的「好友」标签页）
 *
 * 依赖：core.php、friend.php（建表与邀请码生成）、admin.php 的 flash/redirect/csrf。
 */

declare(strict_types=1);

/** 好友功能概览数据 */
function admin_friend_stats(): array
{
    friend_tables();
    $stats = ['users' => 0, 'messages' => 0, 'today' => 0, 'groups' => 0, 'invites' => 0, 'online' => 0];
    try {
        $stats['users'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_user')['c'];
        $stats['messages'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_message')['c'];
        $stats['today'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_message WHERE created_at > ?', [strtotime('today')])['c'];
        $stats['groups'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_group')['c'];
        $stats['invites'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_invite WHERE disabled = 0 AND (max_uses = 0 OR used < max_uses)')['c'];
        $stats['online'] = (int) db_one('SELECT COUNT(*) AS c FROM friend_user WHERE status = ? AND last_seen_at > ?', ['active', time() - 120])['c'];
    } catch (Throwable $e) {
        error_log('[rmcl-admin-friend] ' . $e->getMessage());
    }
    return $stats;
}

/**
 * 处理好友管理的写操作。$action 形如 friend_create_invite。
 * 处理完直接 redirect 回好友标签页。
 */
function admin_friend_handle_post(string $action): void
{
    friend_tables();
    $sub = 'users';
    switch ($action) {
        case 'friend_create_invite':
            $count = max(1, min(50, (int) ($_POST['count'] ?? 1)));
            $uses = max(0, min(9999, (int) ($_POST['uses'] ?? 1)));
            $note = mb_substr(trim((string) ($_POST['note'] ?? '')), 0, 60);
            for ($i = 0; $i < $count; $i++) {
                db_exec(
                    'INSERT OR IGNORE INTO friend_invite (code, created_by, note, max_uses, used, created_at) VALUES (?, 0, ?, ?, 0, ?)',
                    [friend_invite_code(), $note, $uses, time()]
                );
            }
            $sub = 'invites';
            break;

        case 'friend_toggle_invite':
            db_exec('UPDATE friend_invite SET disabled = 1 - disabled WHERE id = ?', [(int) ($_POST['id'] ?? 0)]);
            $sub = 'invites';
            break;

        case 'friend_delete_invite':
            db_exec('DELETE FROM friend_invite WHERE id = ? AND used = 0', [(int) ($_POST['id'] ?? 0)]);
            $sub = 'invites';
            break;

        case 'friend_toggle_user':
            $id = (int) ($_POST['id'] ?? 0);
            db_exec("UPDATE friend_user SET status = CASE WHEN status = 'active' THEN 'disabled' ELSE 'active' END WHERE id = ?", [$id]);
            break;

        case 'friend_delete_user':
            $id = (int) ($_POST['id'] ?? 0);
            if ($id > 0) {
                db_exec('DELETE FROM friend_relation WHERE user_id = ? OR friend_id = ?', [$id, $id]);
                db_exec('DELETE FROM friend_group_member WHERE user_id = ?', [$id]);
                // 自己名下的会话 + 别人会话里指向他的那一行，一起清掉，避免残留「已注销用户」
                db_exec('DELETE FROM friend_conversation WHERE user_id = ? OR (conv_type = 0 AND conv_key = ?)',
                    [$id, (string) $id]);
                db_exec('DELETE FROM friend_roam WHERE user_id = ? OR (conv_type = 0 AND conv_key = ?)',
                    [$id, (string) $id]);
                db_exec('DELETE FROM friend_token WHERE user_id = ?', [$id]);
                db_exec('DELETE FROM friend_request WHERE from_id = ? OR to_id = ?', [$id, $id]);
                db_exec('DELETE FROM friend_user WHERE id = ?', [$id]);
            }
            break;

        case 'friend_clear_messages':
            // 危险操作：清空所有云端聊天记录，必须手工输入确认词
            if (trim((string) ($_POST['confirm'] ?? '')) !== '清空') {
                flash('请输入「清空」两个字再提交', 'error');
                $sub = 'maintenance';
                break;
            }
            db_exec('DELETE FROM friend_message');
            db_exec('UPDATE friend_conversation SET last_body = ?, last_at = 0, unread = 0', ['']);
            $sub = 'maintenance';
            break;

        case 'friend_purge_messages':
            $days = max(1, min(3650, (int) ($_POST['days'] ?? 90)));
            // 只清未开启漫游的消息，漫游记录要一直保留
            db_exec('DELETE FROM friend_message WHERE roamed = 0 AND created_at < ?', [time() - $days * 86400]);
            break;

        default:
            return;
    }
    flash('好友管理操作已完成', 'ok');
    redirect('friends', ['sub' => $sub]);
}

/** 渲染好友标签页 */
function admin_friend_render(string $sub, string $q): void
{
    friend_tables();
    if (!in_array($sub, ['users', 'invites', 'groups', 'maintenance'], true)) {
        $sub = 'users';
    }
    $stats = admin_friend_stats();
    $imEnabled = tim_enabled();
    $smtp = friend_smtp_config() !== null;
    $users = $invites = $groups = [];
    if ($sub === 'users') {
        if ($q !== '') {
            $like = '%' . $q . '%';
            $users = db_all('SELECT * FROM friend_user WHERE email LIKE ? OR nickname LIKE ? ORDER BY id DESC LIMIT 200', [$like, $like]);
        } else {
            $users = db_all('SELECT * FROM friend_user ORDER BY id DESC LIMIT 200');
        }
    } elseif ($sub === 'invites') {
        $invites = db_all('SELECT * FROM friend_invite ORDER BY id DESC LIMIT 200');
    } elseif ($sub === 'groups') {
        $groups = db_all(
            'SELECT g.*, u.nickname AS owner_name,
                    (SELECT COUNT(*) FROM friend_group_member m WHERE m.group_id = g.id) AS member_count,
                    (SELECT COUNT(*) FROM friend_message msg WHERE msg.group_id = g.id) AS message_count
             FROM friend_group g LEFT JOIN friend_user u ON u.id = g.owner_id
             ORDER BY g.id DESC LIMIT 200'
        );
    }
    $csrf = csrf_token();
    ?>
    <div class="grid">
        <div class="stat"><b><?= $stats['users'] ?></b><span>好友用户</span></div>
        <div class="stat"><b><?= $stats['online'] ?></b><span>2 分钟内在线</span></div>
        <div class="stat"><b><?= $stats['messages'] ?></b><span>消息总数</span></div>
        <div class="stat"><b><?= $stats['today'] ?></b><span>今日消息</span></div>
        <div class="stat"><b><?= $stats['groups'] ?></b><span>群聊</span></div>
        <div class="stat"><b><?= $stats['invites'] ?></b><span>可用邀请码</span></div>
    </div>

    <div class="card">
        <h2>运行状态</h2>
        <table>
            <tr><th style="width:180px">腾讯云 IM</th>
                <td><?= $imEnabled ? '<span class="tag blue">已启用</span>' : '<span class="tag gray">未配置</span>' ?></td></tr>
            <tr><th>SDKAppID</th><td class="mono"><?= $imEnabled ? (int) (friend_config('tim', [])['sdkappid'] ?? 0) : '—' ?></td></tr>
            <tr><th>邮件投递</th><td><?= $smtp ? '<span class="tag blue">SMTP</span>' : '<span class="tag gray">PHP mail()</span>' ?></td></tr>
            <tr><th>注册邀请码</th><td><?= friend_config('require_invite', true) ? '必须' : '不需要' ?></td></tr>
            <tr><th>后台文件版本</th>
                <td class="mono muted">admin_view <?= h(date('m-d H:i', (int) (@filemtime(__DIR__ . '/admin_view.php') ?: 0))) ?>
                    · admin_friend <?= h(date('m-d H:i', (int) (@filemtime(__DIR__ . '/admin_friend.php') ?: 0))) ?>
                    · friend <?= h(date('m-d H:i', (int) (@filemtime(__DIR__ . '/friend.php') ?: 0))) ?></td></tr>
        </table>
    </div>

    <div class="card">
        <div class="row" style="gap:8px;margin-bottom:14px">
            <a class="btn" href="admin.php?tab=friends&sub=users" style="line-height:30px;<?= $sub === 'users' ? 'background:#3d7eff;color:#fff;border-color:#3d7eff' : '' ?>">用户</a>
            <a class="btn" href="admin.php?tab=friends&sub=invites" style="line-height:30px;<?= $sub === 'invites' ? 'background:#3d7eff;color:#fff;border-color:#3d7eff' : '' ?>">邀请码</a>
            <a class="btn" href="admin.php?tab=friends&sub=groups" style="line-height:30px;<?= $sub === 'groups' ? 'background:#3d7eff;color:#fff;border-color:#3d7eff' : '' ?>">群组</a>
            <a class="btn" href="admin.php?tab=friends&sub=maintenance" style="line-height:30px;<?= $sub === 'maintenance' ? 'background:#3d7eff;color:#fff;border-color:#3d7eff' : '' ?>">维护</a>
        </div>

        <?php if ($sub === 'users'): ?>
            <form method="get" action="admin.php" style="margin-bottom:14px">
                <input type="hidden" name="tab" value="friends">
                <input type="hidden" name="sub" value="users">
                <div class="row">
                    <input type="text" name="q" value="<?= h($q) ?>" placeholder="邮箱或昵称" style="width:240px">
                    <button type="submit">搜索</button>
                </div>
            </form>
            <table>
                <thead><tr><th>ID</th><th>昵称</th><th>邮箱</th><th>邀请码</th><th>状态</th><th>最后在线</th><th>操作</th></tr></thead>
                <tbody>
                <?php if (!$users): ?><tr><td colspan="7" class="muted">还没有用户。</td></tr><?php endif; ?>
                <?php foreach ($users as $u): ?>
                    <tr>
                        <td><?= (int) $u['id'] ?></td>
                        <td><?= h($u['nickname']) ?><?= $u['rayskin_uuid'] !== '' ? ' <span class="tag gray">RaySkin</span>' : '' ?></td>
                        <td><?= h($u['email']) ?></td>
                        <td class="mono"><?= h($u['invite_code']) ?></td>
                        <td><?= $u['status'] === 'active' ? '<span class="tag blue">正常</span>' : '<span class="tag red">停用</span>' ?></td>
                        <td class="muted"><?= $u['last_seen_at'] > 0 ? h(date('m-d H:i', (int) $u['last_seen_at'])) : '—' ?></td>
                        <td>
                            <form class="inline" method="post" action="admin.php">
                                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                <input type="hidden" name="do" value="friend_toggle_user">
                                <input type="hidden" name="id" value="<?= (int) $u['id'] ?>">
                                <button type="submit" class="btn"><?= $u['status'] === 'active' ? '停用' : '启用' ?></button>
                            </form>
                            <form class="inline" method="post" action="admin.php" onsubmit="return confirm('删除用户会同时删除好友关系与令牌，聊天记录保留。确定继续？')">
                                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                <input type="hidden" name="do" value="friend_delete_user">
                                <input type="hidden" name="id" value="<?= (int) $u['id'] ?>">
                                <button type="submit" class="btn danger">删除</button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach; ?>
                </tbody>
            </table>

        <?php elseif ($sub === 'invites'): ?>
            <form method="post" action="admin.php" style="margin-bottom:16px">
                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                <input type="hidden" name="do" value="friend_create_invite">
                <div class="row">
                    <div class="field"><label>数量</label><input type="number" name="count" value="5" min="1" max="50" style="width:80px"></div>
                    <div class="field"><label>每个可用次数（0 = 不限）</label><input type="number" name="uses" value="1" min="0" max="9999" style="width:110px"></div>
                    <div class="field" style="flex:1;min-width:180px"><label>备注</label><input type="text" name="note" placeholder="例如 QQ 群发放" style="width:100%"></div>
                    <button type="submit" class="primary">生成</button>
                </div>
            </form>
            <table>
                <thead><tr><th>邀请码</th><th>备注</th><th>使用</th><th>来源</th><th>状态</th><th>创建时间</th><th>操作</th></tr></thead>
                <tbody>
                <?php if (!$invites): ?><tr><td colspan="7" class="muted">还没有邀请码。</td></tr><?php endif; ?>
                <?php foreach ($invites as $i): ?>
                    <tr>
                        <td class="mono"><?= h($i['code']) ?></td>
                        <td><?= h($i['note']) ?></td>
                        <td><?= (int) $i['used'] ?> / <?= (int) $i['max_uses'] === 0 ? '∞' : (int) $i['max_uses'] ?></td>
                        <td class="muted"><?= (int) $i['created_by'] > 0 ? '用户 #' . (int) $i['created_by'] : '管理员' ?></td>
                        <td><?= (int) $i['disabled'] === 1 ? '<span class="tag red">已停用</span>' : '<span class="tag blue">可用</span>' ?></td>
                        <td class="muted"><?= h(date('Y-m-d H:i', (int) $i['created_at'])) ?></td>
                        <td>
                            <form class="inline" method="post" action="admin.php">
                                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                <input type="hidden" name="do" value="friend_toggle_invite">
                                <input type="hidden" name="id" value="<?= (int) $i['id'] ?>">
                                <button type="submit" class="btn"><?= (int) $i['disabled'] === 1 ? '启用' : '停用' ?></button>
                            </form>
                            <form class="inline" method="post" action="admin.php" onsubmit="return confirm('确定删除这个邀请码？')">
                                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                                <input type="hidden" name="do" value="friend_delete_invite">
                                <input type="hidden" name="id" value="<?= (int) $i['id'] ?>">
                                <button type="submit" class="btn danger">删除</button>
                            </form>
                        </td>
                    </tr>
                <?php endforeach; ?>
                </tbody>
            </table>

        <?php elseif ($sub === 'groups'): ?>
            <table>
                <thead><tr><th>ID</th><th>群名称</th><th>群主</th><th>成员</th><th>消息</th><th>IM GroupId</th><th>创建时间</th></tr></thead>
                <tbody>
                <?php if (!$groups): ?><tr><td colspan="7" class="muted">还没有群聊。</td></tr><?php endif; ?>
                <?php foreach ($groups as $g): ?>
                    <tr>
                        <td><?= (int) $g['id'] ?></td>
                        <td><?= h($g['name']) ?></td>
                        <td><?= h($g['owner_name'] ?? '未知') ?></td>
                        <td><?= (int) $g['member_count'] ?></td>
                        <td><?= (int) $g['message_count'] ?></td>
                        <td class="muted mono"><?= h($g['im_group_id'] ?: '—') ?></td>
                        <td class="muted"><?= h(date('Y-m-d H:i', (int) $g['created_at'])) ?></td>
                    </tr>
                <?php endforeach; ?>
                </tbody>
            </table>

        <?php else: ?>
            <h2>清理历史消息</h2>
            <p class="desc">删除早于指定天数的消息。已开启「聊天记录漫游」的会话不会被清理，用户本地缓存也不受影响。</p>
            <form method="post" action="admin.php" class="row">
                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                <input type="hidden" name="do" value="friend_purge_messages">
                删除早于 <input type="number" name="days" value="90" min="1" max="3650" style="width:90px"> 天的消息
                <button type="submit" class="btn danger" onclick="return confirm('确定要清理吗？此操作不可撤销。')">清理</button>
            </form>

            <h2 style="margin-top:26px">清空所有云端聊天记录</h2>
            <p class="desc">删除 `friend_message` 里的全部消息，所有用户、所有会话的云端记录都会被清空，且不可恢复。本机缓存不受影响。</p>
            <form method="post" action="admin.php" class="row" style="margin-top:8px"
                  onsubmit="return confirm('这会删除所有用户、所有会话的云端聊天记录，且不可恢复。确定继续？')">
                <input type="hidden" name="csrf" value="<?= h($csrf) ?>">
                <input type="hidden" name="do" value="friend_clear_messages">
                输入 <b>清空</b> 确认：<input type="text" name="confirm" placeholder="清空" style="width:90px">
                <button type="submit" class="btn danger">清空所有聊天记录</button>
            </form>
        <?php endif; ?>
    </div>
    <?php
}
