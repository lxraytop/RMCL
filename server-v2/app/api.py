"""REST API（Lite：仅体验版能力）。

保留：好友列表、加/删好友、申请、会话、消息、群组。
不含：未读、在线、分组、备注、黑名单、漫游。
"""

import asyncio
import logging

import httpx
from fastapi import APIRouter, Depends, Header, Query

from . import tim
from .config import CONFIG
from .db import one, rows, tx
from .hub import HUB, emit
from .security import hash_password, new_code, new_token, now, verify_password

log = logging.getLogger("friend.api")
router = APIRouter(prefix="/api/v1")


class ApiError(Exception):
    def __init__(self, code: str, message: str, status: int = 400) -> None:
        self.code = code
        self.message = message
        self.status = status


def fail(code: str, message: str, status: int = 400):
    raise ApiError(code, message, status)


# ---------------------------------------------------------------- 依赖

def current_user(authorization: str | None = Header(default=None)) -> dict:
    token = ""
    if authorization and authorization.lower().startswith("bearer "):
        token = authorization[7:].strip()
    if not token:
        fail("E_AUTH", "未登录", 401)
    with tx() as conn:
        user = one(
            conn,
            "SELECT u.* FROM auth_token t JOIN user u ON u.id = t.user_id "
            "WHERE t.token = ? AND t.expires_at > ? AND u.status = 'active'",
            (token, now()),
        )
    if user is None:
        fail("E_AUTH", "登录状态已失效，请重新登录", 401)
    return user


# ---------------------------------------------------------------- 工具

def parse_conv(value: str) -> tuple[int, str]:
    """'0:123' / '1:45' -> (0, '123')"""
    if not value or ":" not in value:
        fail("E_PARAM", "会话标识不正确")
    left, right = value.split(":", 1)
    conv_type = 1 if left.strip() == "1" else 0
    key = right.strip()
    if not key:
        fail("E_PARAM", "会话标识不正确")
    return conv_type, key


def canonical_conv_key(conv_type: int, key: str, user_id: int) -> str:
    """消息表用与视角无关的键：单聊 min_max，群聊 group_id。

    会话表则按每个人自己的视角存 conv_key（= 对方 id），两者不能混用。
    """
    if conv_type == 1:
        return key
    a, b = sorted((int(user_id), int(key)))
    return f"{a}_{b}"


def user_public(row: dict) -> dict:
    return {
        "id": int(row["id"]),
        "email": row["email"],
        "nickname": row["nickname"],
        "avatar": row.get("avatar") or "",
    }


def self_public(conn, row: dict) -> dict:
    """只在登录/注册/me 返回：带上自己的邀请码与用量。"""
    item = user_public(row)
    code = row.get("invite_code") or ""
    used = 0
    max_uses = 0
    if code:
        used = int(one(conn, "SELECT COUNT(*) AS c FROM user WHERE invite_code = ?", (code,))["c"])
        inv = one(conn, "SELECT max_uses FROM invite WHERE code = ?", (code,))
        max_uses = int(inv["max_uses"]) if inv else 0
    item.update({"invite_code": code, "invite_used": used, "invite_max": max_uses})
    return item


def msg_payload(row: dict) -> dict:
    return {
        "id": int(row["id"]),
        "conv_type": int(row["conv_type"]),
        "conv_key": row["conv_key"],
        "from_id": int(row["from_id"]),
        "to_id": int(row["to_id"]),
        "group_id": int(row["group_id"]),
        "client_msg_id": row["client_msg_id"],
        "body": row["body"],
        "kind": row["kind"],
        "created_at": int(row["created_at"]),
        "recalled_at": int(row["recalled_at"]),
        "from_name": row.get("from_name") or "",
    }


def touch_conversation(conn, user_id: int, conv_type: int, conv_key: str,
                       msg_id: int, body: str, at: int, unread_delta: int = 0) -> None:
    conn.execute(
        "INSERT INTO conversation (user_id, conv_type, conv_key, last_msg_id, last_body, last_at, unread, hidden)\n"
        " VALUES (?, ?, ?, ?, ?, ?, ?, 0)\n"
        " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET\n"
        "   last_msg_id = excluded.last_msg_id,\n"
        "   last_body = excluded.last_body,\n"
        "   last_at = excluded.last_at,\n"
        "   unread = unread + ?,\n"
        "   hidden = 0",
        (user_id, conv_type, conv_key, msg_id, body[:120], at, max(0, unread_delta), unread_delta),
    )


def group_members(conn, group_id: int) -> list[int]:
    return [int(r["user_id"]) for r in rows(conn, "SELECT user_id FROM group_member WHERE group_id = ?", (group_id,))]


def require_member(conn, group_id: int, user_id: int) -> dict:
    row = one(conn, "SELECT * FROM group_member WHERE group_id = ? AND user_id = ?", (group_id, user_id))
    if row is None:
        fail("E_NOT_MEMBER", "你不在这个群里", 403)
    return row


def require_friend(conn, user_id: int, peer_id: int) -> None:
    if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user_id, peer_id)) is None:
        fail("E_NOT_FRIEND", "你们还不是好友", 403)


# ---------------------------------------------------------------- 认证

@router.post("/auth/send-code")
async def send_code(body: dict):
    email = (body.get("email") or "").strip().lower()
    purpose = body.get("purpose") or "register"
    if "@" not in email:
        fail("E_EMAIL", "邮箱格式不正确")
    code = new_code()
    with tx() as conn:
        if purpose == "register" and one(conn, "SELECT 1 FROM user WHERE email = ?", (email,)):
            fail("E_EXISTS", "这个邮箱已经注册过了")
        conn.execute(
            "INSERT INTO auth_code (email, purpose, code, expires_at, used) VALUES (?, ?, ?, ?, 0)\n"
            " ON CONFLICT(email, purpose) DO UPDATE SET code = excluded.code, expires_at = excluded.expires_at, used = 0",
            (email, purpose, code, now() + 600),
        )
    from . import mail

    if mail.enabled():
        sent = await mail.send(
            email,
            "RMCL 好友账号验证码",
            f"你的验证码是 {code}，10 分钟内有效。\n\n如果不是你本人操作，请忽略这封邮件。",
        )
        if not sent and not CONFIG.get("debug_code"):
            fail("E_MAIL", "验证码邮件发送失败，请稍后再试")
    else:
        log.info("验证码 %s -> %s", email, code)
        if not CONFIG.get("debug_code"):
            fail("E_MAIL", "服务端还没有配置邮件服务，请联系管理员")

    if CONFIG.get("debug_code"):
        return {"ok": True, "debug_code": code}
    return {"ok": True}


@router.post("/auth/register")
async def register(body: dict):
    email = (body.get("email") or "").strip().lower()
    password = body.get("password") or ""
    nickname = (body.get("nickname") or "").strip() or email.split("@")[0]
    code = (body.get("code") or "").strip()
    invite = (body.get("invite") or "").strip()
    if "@" not in email or len(password) < 6:
        fail("E_PARAM", "邮箱或密码不符合要求")
    with tx() as conn:
        rec = one(conn, "SELECT * FROM auth_code WHERE email = ? AND purpose = 'register'", (email,))
        if rec is None or rec["used"] or int(rec["expires_at"]) < now() or rec["code"] != code:
            fail("E_CODE", "验证码不正确或已过期")
        if one(conn, "SELECT 1 FROM user WHERE email = ?", (email,)):
            fail("E_EXISTS", "这个邮箱已经注册过了")
        if CONFIG.get("require_invite", True):
            inv = one(conn, "SELECT * FROM invite WHERE code = ? AND disabled = 0", (invite,))
            if inv is None or (int(inv["max_uses"]) > 0 and int(inv["used"]) >= int(inv["max_uses"])):
                fail("E_INVITE", "邀请码无效或已用完")
            conn.execute("UPDATE invite SET used = used + 1 WHERE code = ?", (invite,))
        conn.execute(
            "INSERT INTO user (email, nickname, pass_hash, invite_code, created_at) VALUES (?, ?, ?, ?, ?)",
            (email, nickname, hash_password(password), invite, now()),
        )
        uid = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        conn.execute("UPDATE auth_code SET used = 1 WHERE email = ? AND purpose = 'register'", (email,))
        token = new_token()
        conn.execute(
            "INSERT INTO auth_token (token, user_id, device, created_at, expires_at) VALUES (?, ?, ?, ?, ?)",
            (token, uid, body.get("device") or "", now(), now() + int(CONFIG["token_ttl_days"]) * 86400),
        )
        user = one(conn, "SELECT * FROM user WHERE id = ?", (uid,))
        self_view = self_public(conn, user)
    asyncio.create_task(tim.account_import(uid, nickname))
    return {"ok": True, "token": token, "user": self_view,
            "user_sig": tim.user_sig(str(uid)) if tim.enabled() else ""}


@router.post("/auth/login")
async def login(body: dict):
    email = (body.get("email") or "").strip().lower()
    password = body.get("password") or ""
    with tx() as conn:
        user = one(conn, "SELECT * FROM user WHERE email = ?", (email,))
        if user is None or user["status"] != "active" or not verify_password(password, user["pass_hash"]):
            fail("E_LOGIN", "邮箱或密码不正确", 401)
        token = new_token()
        conn.execute(
            "INSERT INTO auth_token (token, user_id, device, created_at, expires_at) VALUES (?, ?, ?, ?, ?)",
            (token, user["id"], body.get("device") or "", now(), now() + int(CONFIG["token_ttl_days"]) * 86400),
        )
        self_view = self_public(conn, user)
    return {"ok": True, "token": token, "user": self_view,
            "user_sig": tim.user_sig(str(user["id"])) if tim.enabled() else ""}


async def _rayskin_verify(server: str, uuid: str, name: str, access_token: str, server_id: str) -> dict | None:
    """校验 RaySkin 身份。优先 accessToken（join 被代理拦截时的兜底），否则 hasJoined。"""
    base = server.rstrip("/")
    async with httpx.AsyncClient(timeout=8) as client:
        if access_token:
            try:
                r = await client.post(f"{base}/authserver/validate", json={"accessToken": access_token})
                if r.status_code in (401, 403):
                    return None
                if r.status_code == 204:
                    p = await client.get(f"{base}/sessionserver/session/minecraft/profile/{uuid}")
                    if p.status_code == 200:
                        prof = p.json()
                        if str(prof.get("id", "")).replace("-", "").lower() == uuid.replace("-", "").lower():
                            return prof
            except Exception as exc:
                log.warning("RaySkin validate 失败: %s", exc)
        if name and server_id:
            try:
                r = await client.get(
                    f"{base}/sessionserver/session/minecraft/hasJoined",
                    params={"username": name, "serverId": server_id},
                )
                if r.status_code == 200 and r.text.strip():
                    return r.json()
            except Exception as exc:
                log.warning("RaySkin hasJoined 失败: %s", exc)
    return None


@router.post("/auth/rayskin")
async def rayskin_login(body: dict):
    email = (body.get("email") or "").strip().lower()
    uuid = (body.get("uuid") or "").strip().replace("-", "").lower()
    name = (body.get("name") or "").strip()
    server = (body.get("server") or "").strip()
    server_id = (body.get("server_id") or "").strip()
    access_token = (body.get("access_token") or "").strip()
    if "@" not in email or not uuid or not server:
        fail("E_PARAM", "RaySkin 返回的数据不完整")
    if not server.lower().startswith("https://"):
        fail("E_RAYSKIN", "认证服务器必须使用 https")

    profile = await _rayskin_verify(server, uuid, name, access_token, server_id)
    if profile is None:
        fail("E_RAYSKIN", "RaySkin 校验失败，请到启动页重新登录后再试")
    if str(profile.get("id", "")).replace("-", "").lower() != uuid:
        fail("E_RAYSKIN", "RaySkin 校验结果与提交的账号不一致")
    name = (profile.get("name") or name or email.split("@")[0])[:32]

    with tx() as conn:
        user = one(conn, "SELECT * FROM user WHERE rayskin_uuid = ? OR email = ?", (uuid, email))
        if user is None:
            if not CONFIG.get("rayskin_auto_register", True):
                fail("E_NOACCOUNT", "还没有绑定这个 RaySkin 账号，请先注册", 404)
            conn.execute(
                "INSERT INTO user (email, nickname, rayskin_uuid, created_at) VALUES (?, ?, ?, ?)",
                (email, name, uuid, now()),
            )
            uid = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        else:
            if user["status"] != "active":
                fail("E_DISABLED", "账号已被停用", 403)
            uid = int(user["id"])
            conn.execute("UPDATE user SET rayskin_uuid = ?, nickname = COALESCE(NULLIF(nickname, ''), ?) WHERE id = ?",
                         (uuid, name, uid))
        token = new_token()
        conn.execute(
            "INSERT INTO auth_token (token, user_id, device, created_at, expires_at) VALUES (?, ?, ?, ?, ?)",
            (token, uid, body.get("device") or "", now(), now() + int(CONFIG["token_ttl_days"]) * 86400),
        )
        user = one(conn, "SELECT * FROM user WHERE id = ?", (uid,))
        self_view = self_public(conn, user)
    asyncio.create_task(tim.account_import(uid, user["nickname"]))
    return {"ok": True, "token": token, "user": self_view,
            "user_sig": tim.user_sig(str(uid)) if tim.enabled() else ""}


@router.post("/auth/logout")
async def logout(user: dict = Depends(current_user), authorization: str | None = Header(default=None)):
    token = (authorization or "")[7:].strip()
    with tx() as conn:
        conn.execute("DELETE FROM auth_token WHERE token = ?", (token,))
    return {"ok": True}


@router.get("/me")
async def me(user: dict = Depends(current_user)):
    with tx() as conn:
        unread = int(one(conn, "SELECT COALESCE(SUM(unread), 0) AS c FROM conversation WHERE user_id = ?",
                         (user["id"],))["c"])
        pending = int(one(conn, "SELECT COUNT(*) AS c FROM friend_request WHERE to_id = ? AND status = 'pending'",
                          (user["id"],))["c"])
        self_view = self_public(conn, user)
    return {"user": self_view, "unread": unread, "pending_requests": pending}


# ---------------------------------------------------------------- 好友

@router.get("/friends")
async def list_friends(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(
            conn,
            "SELECT u.* FROM friendship f JOIN user u ON u.id = f.friend_id\n"
            " WHERE f.user_id = ? AND u.status = 'active' ORDER BY u.nickname COLLATE NOCASE",
            (user["id"],),
        )
    out = []
    for it in items:
        item = user_public(it)
        item["conv"] = f"0:{it['id']}"
        out.append(item)
    return {"items": out, "total": len(out)}


@router.get("/friends/{friend_id}")
async def friend_detail(friend_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        row = one(
            conn,
            "SELECT u.*, f.created_at AS friend_since FROM friendship f\n"
            " JOIN user u ON u.id = f.friend_id WHERE f.user_id = ? AND f.friend_id = ?",
            (user["id"], friend_id),
        )
        if row is None:
            fail("E_NOT_FRIEND", "你们还不是好友", 404)
    item = user_public(row)
    item.update({"friend_since": int(row["friend_since"] or 0), "conv": f"0:{friend_id}"})
    return item


@router.delete("/friends/{friend_id}")
async def delete_friend(friend_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        conn.execute(
            "DELETE FROM friendship WHERE (user_id = ? AND friend_id = ?) OR (user_id = ? AND friend_id = ?)",
            (user["id"], friend_id, friend_id, user["id"]),
        )
        events = emit(conn, [user["id"]], "friend.removed", {"user_id": friend_id})
        events += emit(conn, [friend_id], "friend.removed", {"user_id": user["id"]})
    await HUB.push_many(events)
    asyncio.create_task(tim.friend_delete(user["id"], [friend_id]))
    return {"ok": True}


# ---------------------------------------------------------------- 好友申请

def _request_view(it: dict, box: str) -> dict:
    peer_id = int(it["to_id"] if box == "out" else it["from_id"])
    return {
        "id": int(it["id"]),
        "user": {"id": peer_id, "nickname": it["nickname"], "avatar": it["avatar"], "email": it["email"]},
        "wording": it["wording"],
        "source": it["source"],
        "status": it["status"],
        "created_at": int(it["created_at"]),
    }


@router.get("/friend-requests")
async def list_requests(box: str = Query("in"), include_handled: bool = Query(False),
                        user: dict = Depends(current_user)):
    """默认只返回待处理的申请。已同意/已拒绝的留在列表里会让人点了没反应。"""
    cond = "" if include_handled else " AND r.status = 'pending'"
    with tx() as conn:
        if box == "out":
            items = rows(
                conn,
                "SELECT r.*, u.nickname, u.avatar, u.email FROM friend_request r JOIN user u ON u.id = r.to_id\n"
                " WHERE r.from_id = ?" + cond + " ORDER BY r.id DESC LIMIT 100",
                (user["id"],),
            )
        else:
            items = rows(
                conn,
                "SELECT r.*, u.nickname, u.avatar, u.email FROM friend_request r JOIN user u ON u.id = r.from_id\n"
                " WHERE r.to_id = ?" + cond + " ORDER BY r.id DESC LIMIT 100",
                (user["id"],),
            )
    return {"items": [_request_view(it, box) for it in items]}


@router.post("/friend-requests")
async def create_request(body: dict, user: dict = Depends(current_user)):
    to_id = int(body.get("to_id") or 0)
    if to_id <= 0 or to_id == user["id"]:
        fail("E_PARAM", "目标用户不正确")
    with tx() as conn:
        if one(conn, "SELECT 1 FROM user WHERE id = ? AND status = 'active'", (to_id,)) is None:
            fail("E_NOT_FOUND", "用户不存在", 404)
        if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], to_id)):
            fail("E_ALREADY_FRIEND", "你们已经是好友了")
        pending = one(
            conn,
            "SELECT * FROM friend_request WHERE from_id = ? AND to_id = ? AND status = 'pending'",
            (user["id"], to_id),
        )
        if pending is not None:
            return {"ok": True, "id": int(pending["id"]), "duplicated": True}
        conn.execute(
            "INSERT INTO friend_request (from_id, to_id, wording, source, status, created_at)\n"
            " VALUES (?, ?, ?, ?, 'pending', ?)",
            (user["id"], to_id, (body.get("wording") or "")[:256], body.get("source") or "search", now()),
        )
        req_id = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        events = emit(conn, [to_id], "friend.request", {
            "id": req_id,
            "user": user_public(user),
            "wording": body.get("wording") or "",
            "created_at": now(),
        })
    await HUB.push_many(events)
    return {"ok": True, "id": req_id}


@router.post("/friend-requests/{req_id}/accept")
async def accept_request(req_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        req = one(conn, "SELECT * FROM friend_request WHERE id = ? AND to_id = ?", (req_id, user["id"]))
        if req is None:
            fail("E_NOT_FOUND", "申请不存在", 404)
        if req["status"] != "pending":
            fail("E_STATE", "这条申请已经处理过了")
        from_id = int(req["from_id"])
        ts = now()
        conn.execute("UPDATE friend_request SET status = 'accepted', handled_at = ? WHERE id = ?", (ts, req_id))
        conn.execute("INSERT OR REPLACE INTO friendship (user_id, friend_id, created_at) VALUES (?, ?, ?)",
                     (user["id"], from_id, ts))
        conn.execute("INSERT OR REPLACE INTO friendship (user_id, friend_id, created_at) VALUES (?, ?, ?)",
                     (from_id, user["id"], ts))
        for uid, peer in ((user["id"], from_id), (from_id, user["id"])):
            conn.execute(
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, hidden)\n"
                " VALUES (?, 0, ?, '', ?, 0)\n"
                " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET hidden = 0",
                (uid, str(peer), ts),
            )
        me_row = one(conn, "SELECT * FROM user WHERE id = ?", (user["id"],))
        peer_row = one(conn, "SELECT * FROM user WHERE id = ?", (from_id,))
        events = emit(conn, [from_id], "friend.accepted", {"user": user_public(me_row), "conv": f"0:{user['id']}"})
        events += emit(conn, [user["id"]], "friend.accepted", {"user": user_public(peer_row), "conv": f"0:{from_id}"})
    await HUB.push_many(events)
    asyncio.create_task(tim.friend_import(user["id"], [{"To_Account": str(from_id)}]))
    asyncio.create_task(tim.friend_import(from_id, [{"To_Account": str(user["id"])}]))
    return {"ok": True}


@router.post("/friend-requests/{req_id}/reject")
async def reject_request(req_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        req = one(conn, "SELECT * FROM friend_request WHERE id = ? AND to_id = ?", (req_id, user["id"]))
        if req is None:
            fail("E_NOT_FOUND", "申请不存在", 404)
        if req["status"] != "pending":
            fail("E_STATE", "这条申请已经处理过了")
        conn.execute("UPDATE friend_request SET status = 'rejected', handled_at = ? WHERE id = ?", (now(), req_id))
        events = emit(conn, [int(req["from_id"])], "friend.request.handled", {"id": req_id, "status": "rejected"})
    await HUB.push_many(events)
    return {"ok": True}


# ---------------------------------------------------------------- 会话

@router.get("/conversations")
async def list_conversations(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(
            conn,
            "SELECT * FROM conversation WHERE user_id = ? AND hidden = 0 ORDER BY last_at DESC, rowid DESC LIMIT 200",
            (user["id"],),
        )
        out = []
        for it in items:
            conv_type = int(it["conv_type"])
            key = it["conv_key"]
            title = key
            member_count = 0
            if conv_type == 0:
                peer = one(conn, "SELECT nickname FROM user WHERE id = ?", (int(key),))
                if peer is not None:
                    title = peer["nickname"]
            else:
                grp = one(conn, "SELECT name FROM chat_group WHERE id = ?", (int(key),))
                if grp is not None:
                    title = grp["name"]
                    member_count = int(one(conn, "SELECT COUNT(*) AS c FROM group_member WHERE group_id = ?", (int(key),))["c"])
            out.append({
                "conv": f"{conv_type}:{key}",
                "conv_type": conv_type,
                "conv_key": key,
                "title": title,
                "last_body": it["last_body"],
                "last_at": int(it["last_at"]),
                "unread": int(it["unread"]),
                "member_count": member_count,
            })
    return {"items": out}


@router.post("/conversations/{conv:path}/read")
async def read_conversation(conv: str, user: dict = Depends(current_user)):
    conv_type, key = parse_conv(conv)
    with tx() as conn:
        conn.execute(
            "UPDATE conversation SET unread = 0 WHERE user_id = ? AND conv_type = ? AND conv_key = ?",
            (user["id"], conv_type, key),
        )
        total = int(one(conn, "SELECT COALESCE(SUM(unread), 0) AS c FROM conversation WHERE user_id = ?",
                        (user["id"],))["c"])
        events = emit(conn, [user["id"]], "conversation.updated", {"conv": conv, "unread": 0})
    await HUB.push_many(events)
    return {"ok": True, "unread": total}


@router.delete("/conversations/{conv:path}")
async def hide_conversation(conv: str, user: dict = Depends(current_user)):
    conv_type, key = parse_conv(conv)
    with tx() as conn:
        conn.execute(
            "UPDATE conversation SET hidden = 1 WHERE user_id = ? AND conv_type = ? AND conv_key = ?",
            (user["id"], conv_type, key),
        )
        events = emit(conn, [user["id"]], "conversation.removed", {"conv": conv})
    await HUB.push_many(events)
    return {"ok": True}


# ---------------------------------------------------------------- 消息

@router.get("/messages")
async def list_messages(conv: str, before: int = 0, limit: int = 40, user: dict = Depends(current_user)):
    conv_type, key = parse_conv(conv)
    limit = max(1, min(100, limit))
    canonical = canonical_conv_key(conv_type, key, user["id"])
    with tx() as conn:
        if conv_type == 1:
            require_member(conn, int(key), user["id"])
        else:
            require_friend(conn, user["id"], int(key))
        params: list = [conv_type, canonical]
        sql = ("SELECT m.*, u.nickname AS from_name FROM message m "
               "LEFT JOIN user u ON u.id = m.from_id WHERE m.conv_type = ? AND m.conv_key = ?")
        if before > 0:
            sql += " AND m.id < ?"
            params.append(before)
        sql += " ORDER BY m.id DESC LIMIT ?"
        params.append(limit + 1)
        items = rows(conn, sql, tuple(params))
    has_more = len(items) > limit
    items = items[:limit]
    items.reverse()
    return {"items": [msg_payload(x) for x in items], "has_more": has_more}


@router.post("/messages")
async def send_message(body: dict, user: dict = Depends(current_user)):
    conv = body.get("conv") or ""
    conv_type, key = parse_conv(conv)
    text = (body.get("body") or "").strip()
    client_msg_id = (body.get("client_msg_id") or "").strip()[:64]
    if not text or len(text) > 2000:
        fail("E_PARAM", "消息内容为空或过长")

    with tx() as conn:
        if client_msg_id:
            exist = one(conn, "SELECT * FROM message WHERE from_id = ? AND client_msg_id = ?",
                        (user["id"], client_msg_id))
            if exist is not None:
                return {"ok": True, "message": msg_payload(exist), "duplicated": True}

        if conv_type == 0:
            peer = int(key)
            require_friend(conn, user["id"], peer)
            to_id, group_id = peer, 0
            recipients = [(user["id"], str(peer), 0), (peer, str(user["id"]), 1)]
        else:
            group_id = int(key)
            require_member(conn, group_id, user["id"])
            to_id = 0
            recipients = [(uid, str(group_id), 0 if uid == user["id"] else 1) for uid in group_members(conn, group_id)]

        canonical = canonical_conv_key(conv_type, key, user["id"])
        ts = now()
        conn.execute(
            "INSERT INTO message (conv_type, conv_key, from_id, to_id, group_id, client_msg_id, body, kind, created_at)\n"
            " VALUES (?, ?, ?, ?, ?, ?, ?, 'text', ?)",
            (conv_type, canonical, user["id"], to_id, group_id, client_msg_id, text, ts),
        )
        msg_id = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        payload = msg_payload(one(
            conn,
            "SELECT m.*, u.nickname AS from_name FROM message m LEFT JOIN user u ON u.id = m.from_id WHERE m.id = ?",
            (msg_id,),
        ))

        events: list[dict] = []
        for uid, own_key, delta in recipients:
            touch_conversation(conn, uid, conv_type, own_key, msg_id, text, ts, delta)
            events += emit(conn, [uid], "message.new", {
                "message": payload,
                "conv": f"{conv_type}:{own_key}",
                "unread_delta": delta,
            })
    await HUB.push_many(events)
    if conv_type == 0:
        asyncio.create_task(_deliver_c2c(msg_id, user["id"], int(key), text))
    else:
        asyncio.create_task(_deliver_group(msg_id, int(key), user["id"], text))
    return {"ok": True, "message": payload}


async def _deliver_c2c(msg_id: int, from_id: int, to_id: int, text: str) -> None:
    tim_id = await tim.send_c2c(from_id, to_id, text)
    if tim_id:
        with tx() as conn:
            conn.execute("UPDATE message SET tim_msg_id = ? WHERE id = ?", (tim_id, msg_id))


async def _deliver_group(msg_id: int, group_id: int, from_id: int, text: str) -> None:
    tim_id = await tim.send_group(group_id, from_id, text)
    if tim_id:
        with tx() as conn:
            conn.execute("UPDATE message SET tim_msg_id = ? WHERE id = ?", (tim_id, msg_id))


@router.post("/messages/{msg_id}/recall")
async def recall_message(msg_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        row = one(conn, "SELECT * FROM message WHERE id = ?", (msg_id,))
        if row is None:
            fail("E_NOT_FOUND", "消息不存在", 404)
        if int(row["from_id"]) != user["id"]:
            fail("E_NOAUTH", "只能撤回自己发的消息", 403)
        if row["kind"] == "recalled":
            return {"ok": True}
        if now() - int(row["created_at"]) > 120:
            fail("E_EXPIRE", "只能撤回 2 分钟内的消息")
        conn.execute("UPDATE message SET kind = 'recalled', body = '', recalled_at = ? WHERE id = ?",
                     (now(), msg_id))
        conv_type = int(row["conv_type"])
        if conv_type == 0:
            recipients = [(int(row["from_id"]), str(row["to_id"])), (int(row["to_id"]), str(row["from_id"]))]
        else:
            gid = int(row["group_id"])
            recipients = [(uid, str(gid)) for uid in group_members(conn, gid)]
        for uid, own_key in recipients:
            conn.execute(
                "UPDATE conversation SET last_body = '[已撤回]'\n"
                " WHERE user_id = ? AND conv_type = ? AND conv_key = ? AND last_msg_id = ?",
                (uid, conv_type, own_key, msg_id),
            )
        events: list[dict] = []
        for uid, own_key in recipients:
            events += emit(conn, [uid], "message.recalled",
                           {"message_id": msg_id, "conv": f"{conv_type}:{own_key}"})
    await HUB.push_many(events)
    return {"ok": True}


# ---------------------------------------------------------------- 群组

@router.get("/groups")
async def list_groups(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(
            conn,
            "SELECT g.*, m.role FROM group_member m JOIN chat_group g ON g.id = m.group_id\n"
            " WHERE m.user_id = ? ORDER BY g.id DESC",
            (user["id"],),
        )
        out = []
        for g in items:
            count = int(one(conn, "SELECT COUNT(*) AS c FROM group_member WHERE group_id = ?", (g["id"],))["c"])
            out.append({"id": int(g["id"]), "name": g["name"], "owner_id": int(g["owner_id"]),
                        "role": int(g["role"]), "member_count": count, "conv": f"1:{g['id']}"})
    return {"items": out}


@router.post("/groups")
async def create_group(body: dict, user: dict = Depends(current_user)):
    name = (body.get("name") or "").strip()[:30]
    members = [int(x) for x in (body.get("members") or []) if int(x) > 0]
    if not name:
        fail("E_PARAM", "群名称不能为空")
    with tx() as conn:
        for uid in members:
            if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], uid)) is None:
                fail("E_PARAM", "只能邀请自己的好友入群")
        conn.execute("INSERT INTO chat_group (name, owner_id, created_at) VALUES (?, ?, ?)", (name, user["id"], now()))
        gid = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        conn.execute("INSERT INTO group_member (group_id, user_id, role, joined_at) VALUES (?, ?, 1, ?)",
                     (gid, user["id"], now()))
        for uid in members:
            if uid != user["id"]:
                conn.execute("INSERT OR IGNORE INTO group_member (group_id, user_id, role, joined_at) VALUES (?, ?, 0, ?)",
                             (gid, uid, now()))
        all_members = group_members(conn, gid)
        for uid in all_members:
            conn.execute(
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, hidden)\n"
                " VALUES (?, 1, ?, '', ?, 0)\n"
                " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET hidden = 0",
                (uid, str(gid), now()),
            )
        payload = {"id": gid, "name": name, "owner_id": user["id"], "member_count": len(all_members), "conv": f"1:{gid}"}
        events = emit(conn, all_members, "group.updated", payload)
    await HUB.push_many(events)
    asyncio.create_task(_ensure_tim_group(gid, name, user["id"], members))
    return {"ok": True, **payload}


async def _ensure_tim_group(group_id: int, name: str, owner: int, members: list[int]) -> None:
    tim_id = await tim.create_group(name, owner, members)
    if tim_id:
        with tx() as conn:
            conn.execute("UPDATE chat_group SET tim_group_id = ? WHERE id = ?", (tim_id, group_id))


@router.get("/groups/{group_id}")
async def group_detail(group_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        require_member(conn, group_id, user["id"])
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        members = rows(
            conn,
            "SELECT u.*, m.role, m.joined_at FROM group_member m JOIN user u ON u.id = m.user_id\n"
            " WHERE m.group_id = ? ORDER BY m.role DESC, m.joined_at",
            (group_id,),
        )
    return {"id": int(g["id"]), "name": g["name"], "owner_id": int(g["owner_id"]),
            "conv": f"1:{group_id}",
            "members": [{**user_public(x), "role": int(x["role"]), "joined_at": int(x["joined_at"])} for x in members]}


@router.patch("/groups/{group_id}")
async def rename_group(group_id: int, body: dict, user: dict = Depends(current_user)):
    name = (body.get("name") or "").strip()[:30]
    if not name:
        fail("E_PARAM", "群名称不能为空")
    with tx() as conn:
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        if g is None:
            fail("E_NOT_FOUND", "群不存在", 404)
        if int(g["owner_id"]) != user["id"]:
            fail("E_NOAUTH", "只有群主可以改名", 403)
        conn.execute("UPDATE chat_group SET name = ? WHERE id = ?", (name, group_id))
        events = emit(conn, group_members(conn, group_id), "group.updated",
                      {"id": group_id, "name": name, "owner_id": int(g["owner_id"])})
    await HUB.push_many(events)
    return {"ok": True}


@router.post("/groups/{group_id}/members")
async def add_group_members(group_id: int, body: dict, user: dict = Depends(current_user)):
    members = [int(x) for x in (body.get("members") or []) if int(x) > 0]
    with tx() as conn:
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        if g is None:
            fail("E_NOT_FOUND", "群不存在", 404)
        require_member(conn, group_id, user["id"])
        if int(g["owner_id"]) != user["id"]:
            fail("E_NOAUTH", "只有群主可以邀请成员", 403)
        existing = set(group_members(conn, group_id))
        added = []
        for uid in members:
            if uid in existing:
                continue
            if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], uid)) is None:
                continue
            conn.execute("INSERT OR IGNORE INTO group_member (group_id, user_id, role, joined_at) VALUES (?, ?, 0, ?)",
                         (group_id, uid, now()))
            conn.execute(
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, hidden)\n"
                " VALUES (?, 1, ?, '', ?, 0)\n"
                " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET hidden = 0",
                (uid, str(group_id), now()),
            )
            added.append(uid)
        if not added:
            fail("E_PARAM", "没有可邀请的成员")
        count = len(group_members(conn, group_id))
        events = emit(conn, group_members(conn, group_id), "group.updated",
                      {"id": group_id, "name": g["name"], "member_count": count})
    await HUB.push_many(events)
    asyncio.create_task(tim.add_group_members(group_id, added))
    return {"ok": True, "added": len(added)}


@router.delete("/groups/{group_id}/members/{target_id}")
async def remove_group_member(group_id: int, target_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        if g is None:
            fail("E_NOT_FOUND", "群不存在", 404)
        require_member(conn, group_id, user["id"])
        if target_id != user["id"] and int(g["owner_id"]) != user["id"]:
            fail("E_NOAUTH", "只有群主可以移除成员", 403)
        conn.execute("DELETE FROM group_member WHERE group_id = ? AND user_id = ?", (group_id, target_id))
        conn.execute("DELETE FROM conversation WHERE user_id = ? AND conv_type = 1 AND conv_key = ?",
                     (target_id, str(group_id)))
        count = len(group_members(conn, group_id))
        payload = {"id": group_id, "name": g["name"], "member_count": count, "removed": target_id}
        events = emit(conn, group_members(conn, group_id), "group.updated", payload)
        events += emit(conn, [target_id], "group.removed", {"id": group_id, "name": g["name"]})
    await HUB.push_many(events)
    asyncio.create_task(tim.remove_group_member(group_id, target_id))
    return {"ok": True}


# ---------------------------------------------------------------- 搜索 / 快照

@router.get("/search")
async def search(q: str = Query(""), user: dict = Depends(current_user)):
    q = q.strip()
    if not q:
        return {"items": []}
    with tx() as conn:
        items = rows(
            conn,
            "SELECT * FROM user WHERE status = 'active' AND id <> ? AND (email = ? OR email LIKE ? OR nickname LIKE ?) LIMIT 20",
            (user["id"], q.lower(), f"%{q.lower()}%", f"%{q}%"),
        )
        out = []
        for it in items:
            rel = "none"
            if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], it["id"])):
                rel = "friend"
            elif one(conn, "SELECT 1 FROM friend_request WHERE from_id = ? AND to_id = ? AND status = 'pending'",
                     (user["id"], it["id"])):
                rel = "requested"
            elif one(conn, "SELECT 1 FROM friend_request WHERE from_id = ? AND to_id = ? AND status = 'pending'",
                     (it["id"], user["id"])):
                rel = "incoming"
            out.append({**user_public(it), "relation": rel})
    return {"items": out}


@router.get("/history")
async def history(keyword: str = Query(""), scope: str = Query("all"),
                  before: int = 0, limit: int = 40, user: dict = Depends(current_user)):
    """按关键词检索本人可见的消息。scope: all / friend / group"""
    limit = max(1, min(100, limit))
    where = ["(m.to_id = ? OR m.from_id = ? OR (m.conv_type = 1 AND m.group_id IN "
             "(SELECT group_id FROM group_member WHERE user_id = ?)))"]
    params: list = [user["id"], user["id"], user["id"]]
    if scope == "friend":
        where.append("m.conv_type = 0")
    elif scope == "group":
        where.append("m.conv_type = 1")
    if keyword.strip():
        where.append("m.body LIKE ? ESCAPE '\\'")
        params.append("%" + keyword.strip().replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%")
    if before > 0:
        where.append("m.id < ?")
        params.append(before)
    params.append(limit)
    with tx() as conn:
        items = rows(
            conn,
            "SELECT m.*, u.nickname AS from_name FROM message m LEFT JOIN user u ON u.id = m.from_id "
            "WHERE " + " AND ".join(where) + " ORDER BY m.id DESC LIMIT ?",
            tuple(params),
        )
        titles = {}
        for it in items:
            if int(it["conv_type"]) == 0:
                peer = int(it["to_id"]) if int(it["from_id"]) == user["id"] else int(it["from_id"])
                if peer not in titles:
                    row = one(conn, "SELECT nickname FROM user WHERE id = ?", (peer,))
                    titles[peer] = row["nickname"] if row else ""
            else:
                gid = int(it["group_id"])
                if gid not in titles:
                    row = one(conn, "SELECT name FROM chat_group WHERE id = ?", (gid,))
                    titles[gid] = row["name"] if row else ""
    out = []
    for it in items:
        item = msg_payload(it)
        key = int(it["to_id"]) if int(it["conv_type"]) == 0 and int(it["from_id"]) == user["id"] else int(it["from_id"])
        item["title"] = titles.get(key, "")
        item["from_name"] = it["from_name"] or ""
        out.append(item)
    return {"items": out}


@router.get("/snapshot")
async def snapshot(user: dict = Depends(current_user)):
    friends = await list_friends(user)
    conversations = await list_conversations(user)
    requests = await list_requests("in", False, user)
    with tx() as conn:
        seq = int(one(conn, "SELECT COALESCE(MAX(seq), 0) AS s FROM event_log")["s"])
    return {"me": user_public(user), "friends": friends, "conversations": conversations,
            "requests": requests, "server_seq": seq}


@router.get("/health")
async def health():
    from . import config, mail
    return {"ok": True, "config": config.describe(), "tim": tim.self_check(), "mail": mail.self_check(),
            "ws_connections": sum(len(v) for v in HUB._conns.values())}
