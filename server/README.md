# RMCL 更新服务

自托管的启动器更新源：客户端从这里检查版本、下载安装包，**下载前必须用验证码授权，授权到期后自动拒绝**。

- 单入口 PHP 8 + SQLite，不需要 Composer / Node / Redis / Docker，整站丢进虚拟主机就能跑。
- 管理后台签发验证码（一码一设备）、解绑换机、续期、停用、发布版本、查日志。
- 下载走一次性（短时有效）票据，支持 `Range`，配合客户端的多线程分段下载。
- 服务器只提供版本信息与文件，永远不直接暴露发布包路径。

---

## 1. 部署

### 1.1 上传

把 `server/` 整个目录上传到站点根目录或任意子目录（例如 `/rmcl-update/`），保持结构：

```
index.php          入口与全部 API
admin.php          管理后台
config.php         配置（按需修改）
lib/core.php       配置 / 数据库 / 日志
lib/license.php    验证码、设备绑定、令牌、票据
lib/admin_*.php    后台的登录页与视图
data/              SQLite 与发布包（必须禁止直接访问，仓库自带 .htaccess）
.htaccess          路由与保护
```

要求：PHP **8.0+**，开启 `pdo_sqlite`（默认自带）。`mbstring` 建议开启。

### 1.2 目录权限

`data/` 需要可写（PHP 进程用户），一般 755 或 775 即可：

```bash
chmod -R 775 data
```

### 1.3 首次进入后台

访问 `https://你的域名/rmcl-update/admin.php`，会要求设置管理员密码，哈希写入 `data/admin.hash`。
也可以在 `config.php` 里直接指定 `admin_password_hash`（用 `password_hash()` 生成）。

> 建议把 `config.php` 里的 `admin_path` 之外，顺手把 `admin.php` 改成不常见的文件名，并给它加一层 IP 白名单或 Basic Auth。

### 1.4 nginx（如果你不是 Apache）

仓库里的 `.htaccess` 在 nginx 下不生效，用等价配置：

```nginx
location ^~ /rmcl-update/data/ {
    deny all;
    return 403;
}

location ~ ^/rmcl-update/(api/[a-z]+|download/[A-Fa-f0-9]+)$ {
    rewrite ^/rmcl-update/(.*)$ /rmcl-update/index.php?action=$1 last;
}

location ~ ^/rmcl-update/download/([A-Fa-f0-9]+)$ {
    rewrite ^/rmcl-update/download/(.*)$ /rmcl-update/index.php?action=download&ticket=$1 last;
}
```

客户端默认走 `?action=` 查询串（`index.php?action=latest`），**不依赖 rewrite 也能用**，rewrite 只是让路径好看。

### 1.5 跑一次自检

登录后台后访问 `selftest.php`，它会逐项检查 PHP 版本、`pdo_sqlite`、目录权限、站点地址，
并**实际跑一遍**建码 → 激活 → 令牌校验 → 换票据 → 真实下载 16 字节的完整链路，
顺带验证「同一张码不能绑第二台设备」。临时数据在结束时自动清理。

全部通过说明客户端那边的「检查更新 / 下载」也能正常工作。检查完请把 `selftest.php` 改名或删除。

### 1.6 强烈建议开 HTTPS

验证码和令牌都会经过网络。客户端接受 `http://`，但正式环境请务必上 TLS，否则验证码可能被抓包。

---

## 2. 发布一个版本

1. 后台 →「版本发布」。
2. 填版本号（必须形如 `2.19.0.0`，要和 `AssemblyInfo.vb` 里的 `AssemblyVersion` 一致）、公告、渠道（默认 `stable`）。
3. 上传 `RMCL.exe`，或先用 FTP 传到 `data/releases/` 再从下拉里选。
4. 点发布。服务端会自动计算 `size` 与 `sha256`，客户端下载完会拿它校验。

> PHP 的 `upload_max_filesize` / `post_max_size` 通常只有 2M/8M。启动器 exe 有十几 MB 时请走「FTP + 下拉选择」这条路，后台页面上会显示当前的上传上限。

---

## 3. 签发验证码

后台 →「验证码」→「生成验证码」：

| 选项 | 说明 |
| --- | --- |
| 有效期模式 | `激活后起算天数`：用户首次激活才开始倒计时；`固定到期日`：不管何时激活，到那天 23:59:59 失效 |
| 可绑定设备数 | 默认 1，即一码一设备。换机时在列表里点「解绑」，到期时间不变 |
| 备注 | 买家 / 用途，方便对账 |

码形如 `RMCL-7K3M-9XQP-2WRD-8JHT`（已去掉容易看错的 `I O 0 1`），客户端输入时大小写、有无横线都能识别。

**续期**：列表里「更多 → 续期」，在原到期时间上叠加天数；已过期的从当前时间重新起算。
**停用**：立刻失效，已签发的令牌一并作废。

---

## 4. 客户端怎么配

启动器「设置 → 功能 → 软件更新」：

- **更新源** 选「官方源（需要授权码）」
- **服务器地址** 填 `https://你的域名/rmcl-update/`（结尾斜杠可有可无）
- 第一次检查更新时会弹出输入框，填验证码 → 激活成功后会显示「已授权，剩余 N 天」
- 令牌会保存在本地，之后自动续用；到期后界面会提示「授权已到期」，此时检查更新与下载都会被拒绝

另外两个更新源不需要授权：GitHub 原生源、GitHub 镜像源（镜像地址可自己填，默认 `https://gh-proxy.com/`）。

---

## 5. 接口

统一返回：

```json
{ "ok": true, "data": { } }
{ "ok": false, "error": { "code": "E_EXPIRED", "message": "授权已于 2026-11-20 12:00 到期，请续期后再试" } }
```

| 方法 | 路径 | 参数 | 说明 |
| --- | --- | --- | --- |
| GET | `?action=health` | — | 健康检查，不需要授权 |
| POST | `?action=activate` | `code` `device_id` `device_name` `app_version` | 激活验证码，返回 `token` 与到期信息 |
| POST | `?action=verify` | `token` `device_id` | 校验授权，返回剩余天数 |
| GET | `?action=latest` | `token` `device_id` `channel?` | 取最新版本信息（需要有效授权） |
| POST | `?action=ticket` | `token` `device_id` `release_id` | 换下载票据，返回 `url` |
| GET | `?action=download` | `ticket` | 输出文件，支持 `Range` |

错误码：`E_NO_TOKEN` `E_TOKEN_INVALID` `E_TOKEN_EXPIRED` `E_TOKEN_DEVICE` `E_CODE_NOT_FOUND`
`E_CODE_FORMAT` `E_CODE_DISABLED` `E_DEVICE_BOUND` `E_EXPIRED` `E_NO_RELEASE` `E_TICKET*` `E_TOO_MANY`。

客户端把 `E_NO_TOKEN` / `E_TOKEN_INVALID` / `E_TOKEN_EXPIRED` 当作「需要重新输入验证码」，
`E_EXPIRED` / `E_CODE_DISABLED` / `E_DEVICE_BOUND` 当作「授权不可用，明确报错」。

### 用 curl 自测

```bash
# 1. 健康检查
curl -s "https://你的域名/rmcl-update/index.php?action=health"

# 2. 激活（device_id 随便填一串合法字符）
curl -s -X POST "https://你的域名/rmcl-update/index.php?action=activate" \
  -H "Content-Type: application/json" \
  -d '{"code":"7K3M-9XQP-2WRD-8JHT","device_id":"test-device-01","device_name":"自测机","app_version":"2.19.0.0"}'

# 3. 取最新版本（把 TOKEN 换成上一步返回值）
curl -s "https://你的域名/rmcl-update/index.php?action=latest&token=TOKEN&device_id=test-device-01"

# 4. 换票据并下载
curl -s -X POST "https://你的域名/rmcl-update/index.php?action=ticket" \
  -H "Content-Type: application/json" \
  -d '{"token":"TOKEN","device_id":"test-device-01","release_id":1}'
curl -o RMCL.exe "返回的 url"
```

---

## 6. 备份与迁移

只需要备份 `data/`：`rmcl.sqlite`（验证码与发布记录）、`data/releases/`（发布包）、`data/admin.hash`（后台密码）。
迁移到新服务器时整目录拷过去即可，`base_url` 变了记得改 `config.php` 或依赖自动推断。

---

## 7. 已知边界

- SQLite 适合单机、几百到几千个验证码的规模；量级再大请换 MySQL（只需改 `lib/core.php` 里的 `db()`）。
- 设备标识由客户端根据 Windows `MachineGuid` 派生，重装系统会变，此时用后台「解绑」。
- 下载票据在有效期内可重复使用（客户端是多线程分段下载，必须如此），有效期默认 10 分钟、单票据上限 800 次。
