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
    }


def touch_conversation(conn, user_id: int, conv_type: int, conv_key: str,
                       msg_id: int, body: str, at: int) -> None:
    conn.execute(
        "INSERT INTO conversation (user_id, conv_type, conv_key, last_msg_id, last_body, last_at, hidden)\n"
        " VALUES (?, ?, ?, ?, ?, ?, 0)\n"
        " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET\n"
        "   last_msg_id = excluded.last_msg_id,\n"
        "   last_body = excluded.last_body,\n"
        "   last_at = excluded.last_at,\n"
        "   hidden = 0",
        (user_id, conv_type, conv_key, msg_id, body[:120], at),
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
    # TODO: 接 SMTP；开发环境直接回传
    log.info("验证码 %s -> %s", email, code)
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
    asyncio.create_task(tim.account_import(uid, nickname))
    return {"ok": True, "token": token, "user": user_public(user),
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
    return {"ok": True, "token": token, "user": user_public(user),
            "user_sig": tim.user_sig(str(user["id"])) if tim.enabled() else ""}


@router.post("/auth/logout")
async def logout(user: dict = Depends(current_user), authorization: str | None = Header(default=None)):
    token = (authorization or "")[7:].strip()
    with tx() as conn:
        conn.execute("DELETE FROM auth_token WHERE token = ?", (token,))
    return {"ok": True}


@router.get("/me")
async def me(user: dict = Depends(current_user)):
    return {"user": user_public(user)}


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
async def list_requests(box: str = Query("in"), user: dict = Depends(current_user)):
    with tx() as conn:
        if box == "out":
            items = rows(
                conn,
                "SELECT r.*, u.nickname, u.avatar, u.email FROM friend_request r JOIN user u ON u.id = r.to_id\n"
                " WHERE r.from_id = ? ORDER BY r.id DESC LIMIT 100",
                (user["id"],),
            )
        else:
            items = rows(
                conn,
                "SELECT r.*, u.nickname, u.avatar, u.email FROM friend_request r JOIN user u ON u.id = r.from_id\n"
                " WHERE r.to_id = ? ORDER BY r.id DESC LIMIT 100",
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
                "member_count": member_count,
            })
    return {"items": out}


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
        sql = "SELECT * FROM message WHERE conv_type = ? AND conv_key = ?"
        if before > 0:
            sql += " AND id < ?"
            params.append(before)
        sql += " ORDER BY id DESC LIMIT ?"
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
            recipients = [(user["id"], str(peer)), (peer, str(user["id"]))]
        else:
            group_id = int(key)
            require_member(conn, group_id, user["id"])
            to_id = 0
            recipients = [(uid, str(group_id)) for uid in group_members(conn, group_id)]

        canonical = canonical_conv_key(conv_type, key, user["id"])
        ts = now()
        conn.execute(
            "INSERT INTO message (conv_type, conv_key, from_id, to_id, group_id, client_msg_id, body, kind, created_at)\n"
            " VALUES (?, ?, ?, ?, ?, ?, ?, 'text', ?)",
            (conv_type, canonical, user["id"], to_id, group_id, client_msg_id, text, ts),
        )
        msg_id = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        payload = msg_payload(one(conn, "SELECT * FROM message WHERE id = ?", (msg_id,)))

        events: list[dict] = []
        for uid, own_key in recipients:
            touch_conversation(conn, uid, conv_type, own_key, msg_id, text, ts)
            events += emit(conn, [uid], "message.new", {
                "message": payload,
                "conv": f"{conv_type}:{own_key}",
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
            "SELECT * FROM user WHERE status = 'active' AND id <> ? AND (email = ? OR nickname LIKE ?) LIMIT 20",
            (user["id"], q.lower(), f"%{q}%"),
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


@router.get("/snapshot")
async def snapshot(user: dict = Depends(current_user)):
    friends = await list_friends(user)
    conversations = await list_conversations(user)
    requests = await list_requests("in", user)
    with tx() as conn:
        seq = int(one(conn, "SELECT COALESCE(MAX(seq), 0) AS s FROM event_log")["s"])
    return {"me": user_public(user), "friends": friends, "conversations": conversations,
            "requests": requests, "server_seq": seq}


@router.get("/health")
async def health():
    return {"ok": True, "tim": tim.self_check(), "ws_connections": sum(len(v) for v in HUB._conns.values())}
