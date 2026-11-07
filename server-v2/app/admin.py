"""网页管理后台。

挂在 /admin，登录用 config.json 里的 admin_password。
页面是单文件（内嵌 CSS/JS），数据全部走 /admin/api/*，不依赖模板引擎。
"""

import base64
import hashlib
import hmac
import json
import logging
import secrets
import time

from fastapi import APIRouter, Depends, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse

from . import config, mail, tim
from .config import CONFIG
from .db import one, rows, tx
from .security import hash_password, now
from .hub import HUB

log = logging.getLogger("friend.admin")
router = APIRouter(prefix="/admin")

COOKIE = "friend_admin"
SESSION_TTL = 7 * 86400


def _password() -> str:
    return str(CONFIG.get("admin_password") or "")


def _secret() -> bytes:
    return hashlib.sha256(("rmcl-admin|" + _password()).encode()).digest()


def _make_token() -> str:
    exp = str(int(time.time()) + SESSION_TTL)
    sig = hmac.new(_secret(), exp.encode(), hashlib.sha256).hexdigest()[:32]
    return base64.urlsafe_b64encode(f"{exp}.{sig}".encode()).decode()


def _check_token(token: str) -> bool:
    try:
        raw = base64.urlsafe_b64decode(token.encode()).decode()
        exp, sig = raw.split(".", 1)
        if int(exp) < time.time():
            return False
        good = hmac.new(_secret(), exp.encode(), hashlib.sha256).hexdigest()[:32]
        return hmac.compare_digest(good, sig)
    except Exception:
        return False


def require_admin(request: Request) -> None:
    token = request.cookies.get(COOKIE, "")
    if not _password() or not _check_token(token):
        raise AdminAuthError()


class AdminAuthError(Exception):
    pass


def _int(value, default=0) -> int:
    try:
        return int(value)
    except Exception:
        return default


# ---------------------------------------------------------------- 登录

@router.post("/api/login")
async def login(request: Request):
    body = await request.json()
    pwd = str(body.get("password") or "")
    if not _password():
        return JSONResponse({"error": "服务端未设置 admin_password，无法登录"}, status_code=403)
    if not hmac.compare_digest(pwd, _password()):
        return JSONResponse({"error": "密码不正确"}, status_code=401)
    resp = JSONResponse({"ok": True})
    resp.set_cookie(COOKIE, _make_token(), max_age=SESSION_TTL, httponly=True, samesite="lax")
    return resp


@router.post("/api/logout")
async def logout():
    resp = JSONResponse({"ok": True})
    resp.delete_cookie(COOKIE)
    return resp


# ---------------------------------------------------------------- 概览

@router.get("/api/overview")
async def overview(_: None = Depends(require_admin)):
    with tx() as conn:
        users = _int(one(conn, "SELECT COUNT(*) AS c FROM user")["c"])
        disabled = _int(one(conn, "SELECT COUNT(*) AS c FROM user WHERE status <> 'active'")["c"])
        messages = _int(one(conn, "SELECT COUNT(*) AS c FROM message")["c"])
        groups = _int(one(conn, "SELECT COUNT(*) AS c FROM chat_group")["c"])
        friendships = _int(one(conn, "SELECT COUNT(*) AS c FROM friendship")["c"]) // 2
        invites = _int(one(conn, "SELECT COUNT(*) AS c FROM invite WHERE disabled = 0")["c"])
        pending = _int(one(conn, "SELECT COUNT(*) AS c FROM friend_request WHERE status = 'pending'")["c"])
        today = int(time.time()) - 86400
        new_users = _int(one(conn, "SELECT COUNT(*) AS c FROM user WHERE created_at > ?", (today,))["c"])
        new_msgs = _int(one(conn, "SELECT COUNT(*) AS c FROM message WHERE created_at > ?", (today,))["c"])
    return {
        "users": users, "disabled": disabled, "friendships": friendships,
        "messages": messages, "groups": groups, "invites": invites,
        "pending_requests": pending, "new_users_24h": new_users, "new_messages_24h": new_msgs,
        "ws_connections": sum(len(v) for v in HUB._conns.values()),
        "config": config.describe(), "tim": tim.self_check(), "mail": mail.self_check(),
    }


# ---------------------------------------------------------------- 邀请码

@router.get("/api/invites")
async def invites(_: None = Depends(require_admin)):
    with tx() as conn:
        items = rows(conn, "SELECT * FROM invite ORDER BY created_at DESC, code LIMIT 500")
        counts = {r["invite_code"]: _int(r["c"]) for r in rows(
            conn, "SELECT invite_code, COUNT(*) AS c FROM user WHERE invite_code <> '' GROUP BY invite_code")}
    return {"items": [{
        "code": it["code"], "max_uses": _int(it["max_uses"]), "used": _int(it["used"]),
        "disabled": bool(it["disabled"]), "created_at": _int(it["created_at"]),
        "bound_users": counts.get(it["code"], 0),
    } for it in items]}


@router.post("/api/invites")
async def create_invites(request: Request, _: None = Depends(require_admin)):
    body = await request.json()
    count = max(1, min(50, _int(body.get("count"), 1)))
    uses = max(0, min(1000, _int(body.get("uses"), 1)))
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    made = []
    with tx() as conn:
        for _i in range(count):
            code = "RMCL-" + "".join(secrets.choice(alphabet) for _ in range(4)) + "-" + "".join(
                secrets.choice(alphabet) for _ in range(4))
            conn.execute("INSERT OR IGNORE INTO invite (code, max_uses, used, created_at) VALUES (?, ?, 0, ?)",
                         (code, uses, now()))
            made.append(code)
    return {"ok": True, "codes": made}


@router.post("/api/invites/{code}/toggle")
async def toggle_invite(code: str, _: None = Depends(require_admin)):
    with tx() as conn:
        row = one(conn, "SELECT disabled FROM invite WHERE code = ?", (code,))
        if row is None:
            return JSONResponse({"error": "邀请码不存在"}, status_code=404)
        value = 0 if _int(row["disabled"]) else 1
        conn.execute("UPDATE invite SET disabled = ? WHERE code = ?", (value, code))
    return {"ok": True, "disabled": bool(value)}


@router.delete("/api/invites/{code}")
async def delete_invite(code: str, _: None = Depends(require_admin)):
    with tx() as conn:
        conn.execute("DELETE FROM invite WHERE code = ?", (code,))
    return {"ok": True}


# ---------------------------------------------------------------- 用户

@router.get("/api/users")
async def users(q: str = "", _: None = Depends(require_admin)):
    q = q.strip()
    with tx() as conn:
        if q:
            items = rows(conn,
                         "SELECT * FROM user WHERE email LIKE ? OR nickname LIKE ? ORDER BY id DESC LIMIT 200",
                         (f"%{q}%", f"%{q}%"))
        else:
            items = rows(conn, "SELECT * FROM user ORDER BY id DESC LIMIT 200")
        out = []
        for it in items:
            uid = _int(it["id"])
            out.append({
                "id": uid, "email": it["email"], "nickname": it["nickname"],
                "status": it["status"], "created_at": _int(it["created_at"]),
                "rayskin": bool(it["rayskin_uuid"]), "invite_code": it["invite_code"],
                "friends": _int(one(conn, "SELECT COUNT(*) AS c FROM friendship WHERE user_id = ?", (uid,))["c"]),
                "messages": _int(one(conn, "SELECT COUNT(*) AS c FROM message WHERE from_id = ?", (uid,))["c"]),
            })
    return {"items": out}


@router.patch("/api/users/{uid}")
async def update_user(uid: int, request: Request, _: None = Depends(require_admin)):
    body = await request.json()
    with tx() as conn:
        if one(conn, "SELECT 1 FROM user WHERE id = ?", (uid,)) is None:
            return JSONResponse({"error": "用户不存在"}, status_code=404)
        if "status" in body:
            status = "active" if body.get("status") == "active" else "disabled"
            conn.execute("UPDATE user SET status = ? WHERE id = ?", (status, uid))
            if status == "disabled":
                conn.execute("DELETE FROM auth_token WHERE user_id = ?", (uid,))
        if body.get("password"):
            pwd = str(body["password"])
            if len(pwd) < 6:
                return JSONResponse({"error": "密码至少 6 位"}, status_code=400)
            conn.execute("UPDATE user SET pass_hash = ? WHERE id = ?", (hash_password(pwd), uid))
            conn.execute("DELETE FROM auth_token WHERE user_id = ?", (uid,))
    return {"ok": True}


@router.delete("/api/users/{uid}")
async def delete_user(uid: int, _: None = Depends(require_admin)):
    with tx() as conn:
        if one(conn, "SELECT 1 FROM user WHERE id = ?", (uid,)) is None:
            return JSONResponse({"error": "用户不存在"}, status_code=404)
        for gid in [r["id"] for r in rows(conn, "SELECT id FROM chat_group WHERE owner_id = ?", (uid,))]:
            conn.execute("DELETE FROM group_member WHERE group_id = ?", (gid,))
            conn.execute("DELETE FROM message WHERE group_id = ?", (gid,))
            conn.execute("DELETE FROM conversation WHERE conv_type = 1 AND conv_key = ?", (str(gid),))
            conn.execute("DELETE FROM chat_group WHERE id = ?", (gid,))
        conn.execute("DELETE FROM group_member WHERE user_id = ?", (uid,))
        conn.execute("DELETE FROM friendship WHERE user_id = ? OR friend_id = ?", (uid, uid))
        conn.execute("DELETE FROM friend_request WHERE from_id = ? OR to_id = ?", (uid, uid))
        conn.execute("DELETE FROM message WHERE from_id = ? OR to_id = ?", (uid, uid))
        conn.execute("DELETE FROM conversation WHERE user_id = ? OR (conv_type = 0 AND conv_key = ?)",
                     (uid, str(uid)))
        conn.execute("DELETE FROM auth_token WHERE user_id = ?", (uid,))
        conn.execute("DELETE FROM user WHERE id = ?", (uid,))
    return {"ok": True}


# ---------------------------------------------------------------- 群组

@router.get("/api/groups")
async def groups(_: None = Depends(require_admin)):
    with tx() as conn:
        items = rows(conn,
                     "SELECT g.*, u.nickname AS owner_name, "
                     "(SELECT COUNT(*) FROM group_member m WHERE m.group_id = g.id) AS member_count "
                     "FROM chat_group g LEFT JOIN user u ON u.id = g.owner_id ORDER BY g.id DESC LIMIT 200")
    return {"items": [{
        "id": _int(it["id"]), "name": it["name"], "owner_id": _int(it["owner_id"]),
        "owner_name": it["owner_name"] or "", "member_count": _int(it["member_count"]),
        "created_at": _int(it["created_at"]),
    } for it in items]}


@router.delete("/api/groups/{gid}")
async def delete_group(gid: int, _: None = Depends(require_admin)):
    with tx() as conn:
        conn.execute("DELETE FROM group_member WHERE group_id = ?", (gid,))
        conn.execute("DELETE FROM message WHERE group_id = ?", (gid,))
        conn.execute("DELETE FROM conversation WHERE conv_type = 1 AND conv_key = ?", (str(gid),))
        conn.execute("DELETE FROM chat_group WHERE id = ?", (gid,))
    return {"ok": True}


# ---------------------------------------------------------------- 消息

@router.get("/api/messages")
async def messages(q: str = "", limit: int = 60, _: None = Depends(require_admin)):
    limit = max(1, min(200, limit))
    with tx() as conn:
        if q.strip():
            items = rows(conn,
                         "SELECT m.*, u.nickname AS from_name FROM message m LEFT JOIN user u ON u.id = m.from_id "
                         "WHERE m.body LIKE ? ORDER BY m.id DESC LIMIT ?", (f"%{q.strip()}%", limit))
        else:
            items = rows(conn,
                         "SELECT m.*, u.nickname AS from_name FROM message m LEFT JOIN user u ON u.id = m.from_id "
                         "ORDER BY m.id DESC LIMIT ?", (limit,))
        titles = {}
        for it in items:
            if _int(it["conv_type"]) == 1:
                gid = _int(it["group_id"])
                if gid not in titles:
                    g = one(conn, "SELECT name FROM chat_group WHERE id = ?", (gid,))
                    titles[gid] = g["name"] if g else f"群 {gid}"
    return {"items": [{
        "id": _int(it["id"]), "conv_type": _int(it["conv_type"]),
        "conv_key": it["conv_key"], "title": titles.get(_int(it["group_id"]), "") if _int(it["conv_type"]) == 1 else "",
        "from_id": _int(it["from_id"]), "from_name": it["from_name"] or "",
        "to_id": _int(it["to_id"]), "body": it["body"], "kind": it["kind"],
        "created_at": _int(it["created_at"]),
    } for it in items]}


@router.delete("/api/messages/{mid}")
async def delete_message(mid: int, _: None = Depends(require_admin)):
    with tx() as conn:
        conn.execute("DELETE FROM message WHERE id = ?", (mid,))
    return {"ok": True}


# ---------------------------------------------------------------- 页面

@router.get("", response_class=HTMLResponse)
@router.get("/", response_class=HTMLResponse)
async def page():
    if not _password():
        return HTMLResponse("""<!doctype html><meta charset="utf-8">
<body style="font-family:system-ui;padding:40px;line-height:1.7">
<h2>管理后台未启用</h2>
<p>请先在 <code>config.json</code> 里设置 <code>admin_password</code>，然后重启项目。</p>
<p>生成一个随机密码：<code>python -c "import secrets;print(secrets.token_urlsafe(12))"</code></p>
</body>""", status_code=403)
    return HTMLResponse(PAGE)


PAGE = r"""<!doctype html>
<html lang="zh-CN"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>RMCL 好友后台</title>
<style>
:root{--bg:#f5f6f8;--card:#fff;--line:#e6e8eb;--text:#1f2328;--muted:#6b7280;--brand:#2f6fed;--danger:#d1453b;--ok:#1a7f37}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif}
header{background:#fff;border-bottom:1px solid var(--line);padding:14px 22px;display:flex;align-items:center;gap:16px;position:sticky;top:0;z-index:9}
header h1{font-size:16px;margin:0;font-weight:600}
nav{display:flex;gap:4px;margin-left:8px;flex-wrap:wrap}
nav button{border:0;background:transparent;padding:6px 12px;border-radius:8px;cursor:pointer;color:var(--muted);font-size:13px}
nav button.on{background:#eef3ff;color:var(--brand);font-weight:600}
.spacer{flex:1}
main{max-width:1080px;margin:0 auto;padding:22px}
.card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:16px}
.card h2{font-size:14px;margin:0 0 12px;font-weight:600}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:12px}
.stat{border:1px solid var(--line);border-radius:10px;padding:12px 14px}
.stat b{display:block;font-size:22px;font-weight:600}
.stat span{color:var(--muted);font-size:12px}
table{width:100%;border-collapse:collapse;font-size:13px}
th,td{text-align:left;padding:8px 6px;border-bottom:1px solid var(--line);vertical-align:middle}
th{color:var(--muted);font-weight:500;font-size:12px}
tr:last-child td{border-bottom:0}
button.btn{border:1px solid var(--line);background:#fff;border-radius:7px;padding:5px 10px;cursor:pointer;font-size:12.5px}
button.btn:hover{border-color:#c9ced6}
button.primary{background:var(--brand);border-color:var(--brand);color:#fff}
button.danger{color:var(--danger);border-color:#f0c9c6}
input,select{border:1px solid var(--line);border-radius:7px;padding:6px 9px;font-size:13px;background:#fff}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.badge{display:inline-block;padding:1px 7px;border-radius:99px;font-size:11.5px;border:1px solid}
.badge.ok{color:var(--ok);border-color:#bfe3c8;background:#f0fbf3}
.badge.off{color:var(--muted);border-color:var(--line);background:#fafafa}
.badge.warn{color:#9a6700;border-color:#f2e0b5;background:#fff9e8}
.mono{font-family:ui-monospace,Consolas,monospace}
.muted{color:var(--muted)}
.hidden{display:none!important}
#login{max-width:340px;margin:12vh auto;background:#fff;border:1px solid var(--line);border-radius:14px;padding:26px}
#login h1{font-size:17px;margin:0 0 6px}
#login p{color:var(--muted);font-size:12.5px;margin:0 0 16px}
#login input{width:100%;margin-bottom:10px;padding:9px 11px}
#login button{width:100%;padding:9px;border:0;border-radius:8px;background:var(--brand);color:#fff;font-size:14px;cursor:pointer}
.toast{position:fixed;right:18px;bottom:18px;background:#1f2328;color:#fff;padding:9px 14px;border-radius:9px;font-size:13px;opacity:0;transform:translateY(8px);transition:.2s;pointer-events:none}
.toast.show{opacity:1;transform:none}
.empty{color:var(--muted);font-size:13px;padding:14px 0;text-align:center}
</style></head>
<body>
<div id="login" class="hidden">
  <h1>RMCL 好友后台</h1>
  <p>输入 config.json 里的 admin_password</p>
  <input id="pwd" type="password" placeholder="管理密码" autocomplete="current-password">
  <button onclick="doLogin()">登录</button>
</div>

<div id="app" class="hidden">
<header>
  <h1>RMCL 好友后台</h1>
  <nav>
    <button data-tab="overview" class="on">概览</button>
    <button data-tab="invites">邀请码</button>
    <button data-tab="users">用户</button>
    <button data-tab="groups">群组</button>
    <button data-tab="messages">消息</button>
  </nav>
  <span class="spacer"></span>
  <button class="btn" onclick="logout()">退出</button>
</header>
<main>
  <section id="tab-overview">
    <div class="card"><h2>运行状态</h2><div id="health" class="row"></div></div>
    <div class="card"><h2>数据</h2><div id="stats" class="grid"></div></div>
    <div class="card"><h2>最近注册</h2><div id="recent"></div></div>
  </section>

  <section id="tab-invites" class="hidden">
    <div class="card">
      <h2>生成邀请码</h2>
      <div class="row">
        数量 <input id="inv-count" type="number" value="5" min="1" max="50" style="width:70px">
        每个可用次数 <input id="inv-uses" type="number" value="1" min="0" max="1000" style="width:80px">
        <span class="muted">0 表示不限次数</span>
        <button class="btn primary" onclick="genInvites()">生成</button>
      </div>
    </div>
    <div class="card"><h2>邀请码列表</h2><div id="invite-list"></div></div>
  </section>

  <section id="tab-users" class="hidden">
    <div class="card">
      <h2>用户</h2>
      <div class="row" style="margin-bottom:12px">
        <input id="user-q" placeholder="搜索邮箱或昵称" style="width:220px">
        <button class="btn" onclick="loadUsers()">搜索</button>
        <span class="muted">最多显示 200 条</span>
      </div>
      <div id="user-list"></div>
    </div>
  </section>

  <section id="tab-groups" class="hidden">
    <div class="card"><h2>群组</h2><div id="group-list"></div></div>
  </section>

  <section id="tab-messages" class="hidden">
    <div class="card">
      <h2>消息</h2>
      <div class="row" style="margin-bottom:12px">
        <input id="msg-q" placeholder="按内容搜索" style="width:240px">
        <button class="btn" onclick="loadMessages()">搜索</button>
        <span class="muted">最近 60 条</span>
      </div>
      <div id="msg-list"></div>
    </div>
  </section>
</main>
</div>
<div id="toast" class="toast"></div>

<script>
const $ = s => document.querySelector(s);
function toast(t){const e=$('#toast');e.textContent=t;e.classList.add('show');setTimeout(()=>e.classList.remove('show'),1800);}
function esc(s){return String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
function dt(ts){if(!ts)return '—';const d=new Date(ts*1000);return d.toLocaleString('zh-CN',{hour12:false});}
async function api(path, opt={}){
  const r = await fetch('/admin' + path, {credentials:'same-origin', headers:{'Content-Type':'application/json'}, ...opt});
  if(r.status===401){showLogin();throw new Error('unauth');}
  const data = await r.json().catch(()=>({}));
  if(!r.ok) throw new Error(data.error||('HTTP '+r.status));
  return data;
}
function showLogin(){$('#login').classList.remove('hidden');$('#app').classList.add('hidden');}
function showApp(){$('#login').classList.add('hidden');$('#app').classList.remove('hidden');}
async function doLogin(){
  const pwd=$('#pwd').value;
  try{
    const r=await fetch('/admin/api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:pwd})});
    if(!r.ok){const d=await r.json().catch(()=>({}));throw new Error(d.error||'登录失败');}
    showApp();boot();
  }catch(e){toast(e.message);}
}
async function logout(){await fetch('/admin/api/logout',{method:'POST'});showLogin();}

const tabs=['overview','invites','users','groups','messages'];
document.querySelectorAll('nav button').forEach(b=>{
  b.onclick=()=>{
    tabs.forEach(t=>$('#tab-'+t).classList.add('hidden'));
    document.querySelectorAll('nav button').forEach(x=>x.classList.remove('on'));
    b.classList.add('on');
    $('#tab-'+b.dataset.tab).classList.remove('hidden');
    ({overview:loadOverview,invites:loadInvites,users:loadUsers,groups:loadGroups,messages:loadMessages})[b.dataset.tab]();
  };
});

function stat(label,val){return `<div class="stat"><b>${val}</b><span>${label}</span></div>`;}
async function loadOverview(){
  const d=await api('/api/overview');
  $('#health').innerHTML=[
    d.tim.enabled?'<span class="badge ok">IM 已启用</span>':'<span class="badge off">IM 未启用</span>',
    d.mail.enabled?'<span class="badge ok">邮件已配置</span>':'<span class="badge warn">邮件未配置</span>',
    d.config.exists?'<span class="badge ok">配置文件已加载</span>':'<span class="badge warn">配置文件缺失</span>',
    `<span class="badge ${d.config.debug_code?'warn':'ok'}">debug_code=${d.config.debug_code}</span>`,
    `<span class="badge off">WS 连接 ${d.ws_connections}</span>`
  ].join(' ');
  $('#stats').innerHTML=[
    stat('用户',d.users),stat('24h 新增',d.new_users_24h),stat('好友关系',d.friendships),
    stat('消息',d.messages),stat('24h 消息',d.new_messages_24h),stat('群组',d.groups),
    stat('待处理申请',d.pending_requests),stat('可用邀请码',d.invites)
  ].join('');
  const u=await api('/api/users');
  $('#recent').innerHTML=table(['ID','邮箱','昵称','注册时间','状态'],
    u.items.slice(0,8).map(x=>[x.id,esc(x.email),esc(x.nickname),dt(x.created_at),badge(x.status)]));
}

function table(head,rows){
  if(!rows.length) return '<div class="empty">暂无数据</div>';
  return '<table><thead><tr>'+head.map(h=>`<th>${h}</th>`).join('')+'</tr></thead><tbody>'+
    rows.map(r=>'<tr>'+r.map(c=>`<td>${c}</td>`).join('')+'</tr>').join('')+'</tbody></table>';
}
function badge(status){return status==='active'?'<span class="badge ok">正常</span>':'<span class="badge off">已停用</span>';}

async function loadInvites(){
  const d=await api('/api/invites');
  $('#invite-list').innerHTML=table(['邀请码','已用 / 上限','绑定用户','状态','创建时间','操作'],
    d.items.map(x=>[
      `<span class="mono">${esc(x.code)}</span>`,
      `${x.used} / ${x.max_uses||'∞'}`,
      x.bound_users,
      x.disabled?'<span class="badge off">已停用</span>':'<span class="badge ok">可用</span>',
      dt(x.created_at),
      `<button class="btn" onclick="copyCode('${esc(x.code)}')">复制</button>
       <button class="btn" onclick="toggleInvite('${esc(x.code)}')">${x.disabled?'启用':'停用'}</button>
       <button class="btn danger" onclick="delInvite('${esc(x.code)}')">删除</button>`
    ]));
}
async function genInvites(){
  const count=+$('#inv-count').value||1, uses=+$('#inv-uses').value||0;
  const d=await api('/api/invites',{method:'POST',body:JSON.stringify({count,uses})});
  toast('已生成 '+d.codes.length+' 个');
  loadInvites();
}
async function copyCode(c){try{await navigator.clipboard.writeText(c);toast('已复制 '+c);}catch(e){prompt('复制这个邀请码',c);}}
async function toggleInvite(c){await api('/api/invites/'+encodeURIComponent(c)+'/toggle',{method:'POST'});loadInvites();}
async function delInvite(c){if(!confirm('删除邀请码 '+c+'？'))return;await api('/api/invites/'+encodeURIComponent(c),{method:'DELETE'});loadInvites();}

async function loadUsers(){
  const q=$('#user-q').value||'';
  const d=await api('/api/users?q='+encodeURIComponent(q));
  $('#user-list').innerHTML=table(['ID','邮箱','昵称','好友','发信','绑定 RaySkin','注册时间','状态','操作'],
    d.items.map(x=>[
      x.id,esc(x.email),esc(x.nickname),x.friends,x.messages,
      x.rayskin?'<span class="badge ok">是</span>':'<span class="muted">否</span>',
      dt(x.created_at),badge(x.status),
      `<button class="btn" onclick="toggleUser(${x.id},'${x.status}')">${x.status==='active'?'停用':'启用'}</button>
       <button class="btn" onclick="resetPwd(${x.id})">重置密码</button>
       <button class="btn danger" onclick="delUser(${x.id},'${esc(x.email)}')">删除</button>`
    ]));
}
async function toggleUser(id,status){
  await api('/api/users/'+id,{method:'PATCH',body:JSON.stringify({status:status==='active'?'disabled':'active'})});
  loadUsers();
}
async function resetPwd(id){
  const p=prompt('输入新密码（至少 6 位）');if(!p)return;
  try{await api('/api/users/'+id,{method:'PATCH',body:JSON.stringify({password:p})});toast('密码已重置，旧登录已失效');}
  catch(e){toast(e.message);}
}
async function delUser(id,email){
  if(!confirm('删除用户 '+email+'？\n会同时删除其好友关系、会话和消息，不可恢复。'))return;
  await api('/api/users/'+id,{method:'DELETE'});loadUsers();
}

async function loadGroups(){
  const d=await api('/api/groups');
  $('#group-list').innerHTML=table(['ID','群名','群主','人数','创建时间','操作'],
    d.items.map(x=>[x.id,esc(x.name),esc(x.owner_name),x.member_count,dt(x.created_at),
      `<button class="btn danger" onclick="delGroup(${x.id},'${esc(x.name)}')">解散</button>`]));
}
async function delGroup(id,name){
  if(!confirm('解散群「'+name+'」？群消息会一并删除。'))return;
  await api('/api/groups/'+id,{method:'DELETE'});loadGroups();
}

async function loadMessages(){
  const q=$('#msg-q').value||'';
  const d=await api('/api/messages?q='+encodeURIComponent(q));
  $('#msg-list').innerHTML=table(['ID','会话','发送者','内容','时间','操作'],
    d.items.map(x=>[
      x.id,
      x.conv_type===1?('群 · '+esc(x.title||x.conv_key)):('单聊 '+esc(x.conv_key)),
      esc(x.from_name||('#'+x.from_id)),
      esc(x.body.length>80?x.body.slice(0,80)+'…':x.body),
      dt(x.created_at),
      `<button class="btn danger" onclick="delMsg(${x.id})">删除</button>`
    ]));
}
async function delMsg(id){if(!confirm('删除这条消息？'))return;await api('/api/messages/'+id,{method:'DELETE'});loadMessages();}

async function boot(){
  try{
    await api('/api/overview');
    showApp();loadOverview();
  }catch(e){ if(e.message!=='unauth') showLogin(); }
}
$('#pwd').addEventListener('keydown',e=>{if(e.key==='Enter')doLogin();});
boot();
</script>
</body></html>"""
