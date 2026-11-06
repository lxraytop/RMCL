<?php
/**
 * RMCL 更新服务 —— 站点配置
 *
 * 只需要改这个文件就能完成部署，其余逻辑都在 lib/ 与 index.php / admin.php 里。
 * 所有路径都相对于本文件所在目录，整站可以直接放在任意子目录（例如 /rmcl-update/）。
 */

return [
    // 服务名，出现在首页与接口健康检查里
    'app_name' => 'RMCL 更新服务',

    // 数据目录：SQLite 数据库与发布包都存在这里。
    // 必须禁止 Web 直接访问（仓库里已附带 .htaccess；nginx 请参考 README）。
    'data_dir' => __DIR__ . '/data',

    // 对外地址。留空则按请求自动推断（反代 / 域名绑定场景建议手动填写，例如 https://update.example.com）
    'base_url' => '',

    // 时区，影响后台显示的时间
    'timezone' => 'Asia/Shanghai',

    // 授权令牌有效期（秒）。令牌只是「免重复输码」的凭证，真正的到期时间以验证码为准。
    // 留 0 表示与授权同期到期 —— 推荐这样，否则令牌会先于授权失效，用户明明没过期却被要求重输验证码。
    'token_ttl' => 0,

    // 下载票据有效期（秒）。客户端是多线程分段下载，同一张票据会在有效期内被反复使用（带 Range 请求），
    // 因此不能做成「一次即失效」。给 30 分钟，慢速网络下也不会下到一半就断。
    'ticket_ttl' => 1800,

    // 单张票据的最大使用次数，防止票据被疯狂刷
    'ticket_max_use' => 800,

    // 是否记录请求日志（写入 logs 表，后台可查）。关闭可减少数据库写入
    'enable_log' => true,

    // 管理后台密码哈希。留空则首次打开 admin.php 时要求现场设置，哈希写入 data/admin.hash。
    // 也可以在命令行生成：php -r "echo password_hash('你的密码', PASSWORD_DEFAULT);"
    'admin_password_hash' => '',

    // 管理后台路径（改成不常见的名字可以少被扫描器骚扰，例如 'console-7f3a.php'）
    'admin_path' => 'admin.php',

    // ---- 从 GitHub 同步版本（后台「版本发布 → 从 GitHub 同步」）----

    // 默认仓库，形如 owner/name。后台里可以临时改成别的仓库。
    'github_repo' => 'lxraytop/RMCL',

    // 加速镜像前缀。仅当服务器直连 GitHub 失败时才需要：留空表示只用官方直连。
    // 服务器在海外（能直连 api.github.com 与资产下载）就保持空，不要多绕一层。
    // 国内服务器连不上时再填，格式与 gh-proxy 一致：https://gh-proxy.com/
    'github_mirror' => '',

    // 可选：GitHub Token。填了能提高 API 限额（未认证 60 次/小时），同步私有仓库时必须填。
    'github_token' => '',

    // ---- 好友功能（账号 / 好友 / 群聊 / 历史记录）----
    //
    // 腾讯云即时通信 IM 只负责实时转发，聊天记录同时保存在本服务的 SQLite 里。
    // 未填 sdkappid / key 时，好友功能依然可用（消息通过客户端轮询服务端收发），
    // 只是不具备 IM 的多端同步等能力。开通与密钥位置：
    // https://console.cloud.tencent.com/im
    'friend' => [
        // 注册是否需要邀请码。首个管理员邀请码可以在 admin-friend.php 里生成
        'require_invite' => true,
        // 注册成功后赠送的邀请码可用次数（0 表示不赠送）
        'register_bonus_invite' => 3,
        // 登录令牌有效期（天）
        'token_days' => 30,

        // 邮箱验证。虚拟主机默认用 PHP mail()；发不出去时可以关掉，
        // 此时验证码会以 dev_code 形式回给客户端（仅建议在测试环境这么做）。
        'email_enabled' => true,
        'email_from' => 'noreply@example.com',
        'email_subject' => 'RMCL 好友功能验证码',
        'code_minutes' => 10,
        'debug_code' => false,

        // RaySkin 一键登录：允许校验的皮肤站域名，避免被拿来做 SSRF
        'rayskin_auto_register' => true,
        'rayskin_hosts' => ['mc.lxray.top', 'littleskin.cn'],
        // join 被网络/代理拦截时，允许客户端提交 accessToken 由服务端调用 validate 校验。
        // 关闭后只能走标准 join + hasJoined 流程。
        'rayskin_allow_token' => true,

        // 单人每分钟最多发送的消息条数
        'msg_rate_per_min' => 40,

        // 腾讯云即时通信 IM
        // 密钥不写在这里：放到 data/tim.key（data 目录已被版本库与 Web 访问双重隔离）。
        // 也可以直接把密钥填到 key 字段，key 优先于 key_file。
        'tim' => [
            'enabled' => true,
            'sdkappid' => 1600171310,
            'key' => '',
            'key_file' => 'tim.key',
            'admin' => 'administrator',
            'group_type' => 'Private', // Private / Public / ChatRoom / AVChatRoom
        ],
    ],
];
