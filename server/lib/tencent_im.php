<?php
/**
 * 腾讯云即时通信 IM 服务端封装（REST API + UserSig）
 *
 * 说明：
 * - 客户端不直接接触 IM 的密钥，全部由本服务端签名并转发；
 * - IM 只是「实时通道」，聊天记录同时落本地 SQLite，IM 不可用时好友功能仍然可用；
 * - 未配置 sdkappid/key 时所有调用安全地变成空操作，不会影响其余接口。
 *
 * 文档：https://cloud.tencent.com/document/product/269
 */

declare(strict_types=1);

/** 读取好友配置节 */
function friend_config(?string $key = null, $default = null)
{
    $conf = cfg('friend', []);
    if ($key === null) {
        return $conf;
    }
    return array_key_exists($key, $conf) ? $conf[$key] : $default;
}

/**
 * 读取 IM 密钥。
 * 优先用配置里的 key；没有配置时读取 data 目录下的密钥文件（key_file，默认 data/tim.key），
 * 这样真实密钥可以不进入版本库。
 */
function tim_key(): string
{
    $tim = friend_config('tim', []);
    $key = trim((string) ($tim['key'] ?? ''));
    if ($key !== '') {
        return $key;
    }
    $file = basename((string) ($tim['key_file'] ?? 'tim.key'));
    if ($file === '') {
        return '';
    }
    $path = data_dir() . '/' . $file;
    if (!is_file($path)) {
        return '';
    }
    return trim((string) @file_get_contents($path));
}

/** 腾讯云 IM 是否已配置 */
function tim_enabled(): bool
{
    $tim = friend_config('tim', []);
    return !empty($tim['enabled']) && (int) ($tim['sdkappid'] ?? 0) > 0 && tim_key() !== '';
}

/** IM 账号名。前缀避免与其它业务冲突 */
function tim_identifier(int $userId): string
{
    return 'rmcl_' . $userId;
}

/**
 * 生成 UserSig（TLS 2.0）。
 * 与官方 TLSSigAPIv2 完全一致：JSON → gzcompress → URL 安全 Base64。
 */
function tim_usersig(string $identifier, int $expire = 604800): string
{
    $tim = friend_config('tim', []);
    $sdkappid = (int) ($tim['sdkappid'] ?? 0);
    $key = tim_key();
    if ($sdkappid <= 0 || $key === '') {
        return '';
    }
    $time = time();
    $raw = "TLS.identifier:{$identifier}\n"
         . "TLS.sdkappid:{$sdkappid}\n"
         . "TLS.time:{$time}\n"
         . "TLS.expire:{$expire}\n";
    $sigArray = [
        'TLS.ver' => '2.0',
        'TLS.identifier' => $identifier,
        'TLS.sdkappid' => $sdkappid,
        'TLS.expire' => $expire,
        'TLS.time' => $time,
        'TLS.sig' => base64_encode(hash_hmac('sha256', $raw, $key, true)),
    ];
    $compressed = gzcompress(json_encode($sigArray));
    // 官方实现：URL 安全 Base64 的替换表是 + => *, / => -, = => _
    return str_replace(['+', '/', '='], ['*', '-', '_'], base64_encode($compressed));
}

/**
 * 调用一次 IM REST 接口。
 * @return array{ok:bool,data?:array,error?:string}
 */
function tim_call(string $service, string $command, array $body, int $timeout = 8): array
{
    if (!tim_enabled()) {
        return ['ok' => false, 'error' => 'disabled'];
    }
    $tim = friend_config('tim', []);
    $identifier = (string) ($tim['admin'] ?? 'administrator');
    $sig = tim_usersig($identifier);
    if ($sig === '') {
        return ['ok' => false, 'error' => 'no_sig'];
    }
    $url = 'https://console.tim.qq.com/v4/' . $service . '/' . $command
        . '?sdkappid=' . (int) $tim['sdkappid']
        . '&identifier=' . rawurlencode($identifier)
        . '&usersig=' . rawurlencode($sig)
        . '&random=' . random_int(0, 4294967295)
        . '&contenttype=json';

    $payload = json_encode($body, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    $raw = friend_http_request($url, 'POST', $payload, $timeout);
    if ($raw === null) {
        return ['ok' => false, 'error' => 'network'];
    }
    $decoded = json_decode($raw, true);
    if (!is_array($decoded)) {
        return ['ok' => false, 'error' => 'bad_json'];
    }
    // IM 的返回：ActionStatus=OK 且 ErrorCode=0 才算成功
    if (($decoded['ActionStatus'] ?? '') === 'OK' && (int) ($decoded['ErrorCode'] ?? -1) === 0) {
        return ['ok' => true, 'data' => $decoded];
    }
    return ['ok' => false, 'error' => (string) ($decoded['ErrorInfo'] ?? 'im_error'), 'data' => $decoded];
}

/** 导入 IM 账号（重复导入是幂等的） */
function tim_account_import(int $userId, string $nickname, string $faceUrl = ''): void
{
    if (!tim_enabled()) {
        return;
    }
    $identifier = tim_identifier($userId);
    tim_call('im_open_login_svc', 'account_import', [
        'UserID' => $identifier,
        'Nick' => mb_substr($nickname, 0, 32),
        'FaceUrl' => $faceUrl,
    ]);
}

/** 单聊发消息 */
function tim_send_c2c(int $fromId, int $toId, string $body): ?string
{
    if (!tim_enabled()) {
        return null;
    }
    $res = tim_call('openim', 'sendmsg', [
        'SyncOtherMachine' => 2, // 不同步给发送者的其它终端，发送端由我们自己的通道回显
        'From_Account' => tim_identifier($fromId),
        'To_Account' => tim_identifier($toId),
        'MsgLifeTime' => 604800,
        'MsgRandom' => random_int(0, 4294967295),
        'MsgBody' => [['MsgType' => 'TIMTextElem', 'MsgContent' => ['Text' => $body]]],
    ]);
    return $res['ok'] ? (string) ($res['data']['MsgTime'] ?? '') : null;
}

/** 群消息 */
function tim_send_group(int $groupId, int $fromId, string $body): ?string
{
    if (!tim_enabled()) {
        return null;
    }
    $imGroupId = friend_group_im_id($groupId);
    if ($imGroupId === '') {
        return null;
    }
    $res = tim_call('group_open_http_svc', 'send_group_msg', [
        'GroupId' => $imGroupId,
        'From_Account' => tim_identifier($fromId),
        'Random' => random_int(0, 4294967295),
        'MsgBody' => [['MsgType' => 'TIMTextElem', 'MsgContent' => ['Text' => $body]]],
    ]);
    return $res['ok'] ? (string) ($res['data']['MsgTime'] ?? '') : null;
}

/** 创建 IM 群，返回 GroupId */
function tim_create_group(string $name, int $ownerId, array $memberIds): string
{
    if (!tim_enabled()) {
        return '';
    }
    $members = [];
    foreach (array_unique(array_merge([$ownerId], $memberIds)) as $uid) {
        $members[] = ['Member_Account' => tim_identifier((int) $uid)];
    }
    $res = tim_call('group_open_http_svc', 'create_group', [
        'Owner_Account' => tim_identifier($ownerId),
        'Type' => (string) (friend_config('tim', [])['group_type'] ?? 'Private') ?: 'Private',
        'Name' => mb_substr($name, 0, 30),
        'MemberList' => $members,
    ]);
    return $res['ok'] ? (string) ($res['data']['GroupId'] ?? '') : '';
}

/** 拉人入 IM 群 */
function tim_add_group_members(int $groupId, array $userIds): void
{
    if (!tim_enabled() || !$userIds) {
        return;
    }
    $imGroupId = friend_group_im_id($groupId);
    if ($imGroupId === '') {
        return;
    }
    $members = [];
    foreach ($userIds as $uid) {
        $members[] = ['Member_Account' => tim_identifier((int) $uid)];
    }
    tim_call('group_open_http_svc', 'add_group_member', ['GroupId' => $imGroupId, 'MemberList' => $members]);
}

/** 从 IM 群移除成员 */
function tim_remove_group_member(int $groupId, int $userId): void
{
    if (!tim_enabled()) {
        return;
    }
    $imGroupId = friend_group_im_id($groupId);
    if ($imGroupId === '') {
        return;
    }
    tim_call('group_open_http_svc', 'delete_group_member', [
        'GroupId' => $imGroupId,
        'MemberToDel_Account' => [tim_identifier($userId)],
        'Silence' => 1,
    ]);
}

/**
 * 通用 HTTP 请求。优先 curl，其次 stream，保证虚拟主机可用。
 */
function friend_http_request(string $url, string $method = 'GET', ?string $body = null, int $timeout = 8): ?string
{
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => $timeout,
            CURLOPT_CONNECTTIMEOUT => min(5, $timeout),
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_HTTPHEADER => ['Content-Type: application/json; charset=utf-8'],
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_FOLLOWLOCATION => false,
        ]);
        if ($method !== 'GET' && $body !== null) {
            curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
        }
        $result = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);
        if ($result === false || $code >= 400) {
            return $result === false ? null : (string) $result;
        }
        return (string) $result;
    }

    $context = stream_context_create([
        'http' => [
            'method' => $method,
            'header' => "Content-Type: application/json; charset=utf-8\r\n",
            'content' => $body ?? '',
            'timeout' => $timeout,
            'ignore_errors' => true,
        ],
        'ssl' => ['verify_peer' => true, 'verify_peer_name' => true],
    ]);
    $result = @file_get_contents($url, false, $context);
    return $result === false ? null : (string) $result;
}

/**
 * 与 friend_http_request 相同，但同时返回状态码，用于需要区分 204/403 的校验接口。
 *
 * @return array{0:int,1:string} [status, body]
 */
function friend_http_request_ex(string $url, string $method = 'GET', ?string $body = null, int $timeout = 8): array
{
    if (function_exists('curl_init')) {
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => $timeout,
            CURLOPT_CONNECTTIMEOUT => min(5, $timeout),
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_HTTPHEADER => ['Content-Type: application/json; charset=utf-8'],
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_FOLLOWLOCATION => false,
        ]);
        if ($method !== 'GET' && $body !== null) {
            curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
        }
        $result = curl_exec($ch);
        $code = (int) curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);
        return [$code, $result === false ? '' : (string) $result];
    }

    $context = stream_context_create([
        'http' => [
            'method' => $method,
            'header' => "Content-Type: application/json; charset=utf-8\r\n",
            'content' => $body ?? '',
            'timeout' => $timeout,
            'ignore_errors' => true,
        ],
        'ssl' => ['verify_peer' => true, 'verify_peer_name' => true],
    ]);
    $result = @file_get_contents($url, false, $context);
    $status = 0;
    if (isset($http_response_header[0]) && preg_match('#\s(\d{3})\b#', (string) $http_response_header[0], $m)) {
        $status = (int) $m[1];
    }
    return [$status, $result === false ? '' : (string) $result];
}

/** 读取群对应的 IM GroupId（表在 friend.php 中建） */
function friend_group_im_id(int $groupId): string
{
    try {
        $row = db_one('SELECT im_group_id FROM friend_group WHERE id = ?', [$groupId]);
        return $row === null ? '' : (string) $row['im_group_id'];
    } catch (Throwable $e) {
        return '';
    }
}
