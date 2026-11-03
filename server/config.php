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
    // 因此不能做成「一次即失效」。10 分钟足够下完一个 50MB 的 exe。
    'ticket_ttl' => 600,

    // 单张票据的最大使用次数，防止票据被疯狂刷
    'ticket_max_use' => 800,

    // 是否记录请求日志（写入 logs 表，后台可查）。关闭可减少数据库写入
    'enable_log' => true,

    // 管理后台密码哈希。留空则首次打开 admin.php 时要求现场设置，哈希写入 data/admin.hash。
    // 也可以在命令行生成：php -r "echo password_hash('你的密码', PASSWORD_DEFAULT);"
    'admin_password_hash' => '',

    // 管理后台路径（改成不常见的名字可以少被扫描器骚扰，例如 'console-7f3a.php'）
    'admin_path' => 'admin.php',
];
