"""REST API。命令式接口 + 事件推送，客户端只依赖这一层。"""

import asyncio
import logging
import time

import httpx
from fastapi import APIRouter, Depends, Header, HTTPException, Query

from . import tim
from .config import CONFIG
from .db import one, rows, tx
from .hub import HUB, emit, friend_ids
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
        row = one(
            conn,
            "SELECT u.* FROM auth_token t JOIN user u ON u.id = t.user_id "
            "WHERE t.token = ? AND t.expires_at > ? AND u.status = 'active'",
            (token, now()),
        )
        if row is None:
            fail("E_AUTH", "登录状态已失效，请重新登录", 401)
        conn.execute("UPDATE user SET last_seen_at = ? WHERE id = ?", (now(), row["id"]))
    return row


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
    """消息表用的规范键。

    会话表按「每个人自己的视角」存 conv_key（= 对方 user_id），
    消息表必须用与视角无关的键，否则同一条消息在两个人那里对不上。
    """
    if conv_type == 1:
        return key
    a, b = sorted((int(user_id), int(key)))
    return f"{a}_{b}"


def conv_label(conv_type: int, peer_or_group: str) -> str:
    return f"{conv_type}:{peer_or_group}"


def user_public(row: dict, online: bool) -> dict:
    return {
        "id": int(row["id"]),
        "email": row["email"],
        "nickname": row["nickname"],
        "avatar": row.get("avatar") or "",
        "signature": row.get("signature") or "",
        "online": online,
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
                       msg_id: int, body: str, at: int, unread_delta: int) -> None:
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


def ensure_not_blocked(conn, user_id: int, peer_id: int) -> None:
    if one(conn, "SELECT 1 FROM block WHERE user_id = ? AND blocked_id = ?", (peer_id, user_id)) is not None:
        fail("E_BLOCKED", "对方已将你加入黑名单", 403)


def conversation_view(conn, row: dict, viewer_id: int) -> dict:
    conv_type = int(row["conv_type"])
    key = row["conv_key"]
    title = key
    online = False
    member_count = 0
    is_friend = True
    if conv_type == 0:
        peer = one(conn, "SELECT * FROM user WHERE id = ?", (int(key),))
        if peer is None:
            title = "已注销用户"
        else:
            friend = one(conn, "SELECT remark FROM friendship WHERE user_id = ? AND friend_id = ?", (viewer_id, int(key)))
            is_friend = friend is not None
            title = (friend or {}).get("remark") or peer["nickname"]
            online = HUB.is_online(int(key))
    else:
        grp = one(conn, "SELECT * FROM chat_group WHERE id = ?", (int(key),))
        if grp is not None:
            title = grp["name"]
            member_count = int(one(conn, "SELECT COUNT(*) AS c FROM group_member WHERE group_id = ?", (int(key),))["c"])
    return {
        "conv": f"{conv_type}:{key}",
        "conv_type": conv_type,
        "conv_key": key,
        "title": title,
        "last_body": row["last_body"],
        "last_at": int(row["last_at"]),
        "unread": int(row["unread"]),
        "pinned": bool(row["pinned"]),
        "muted": bool(row["muted"]),
        "online": online,
        "member_count": member_count,
        "is_friend": is_friend,
    }


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
    # TODO: 接入 SMTP；开发环境回传验证码
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
            "INSERT INTO user (email, nickname, pass_hash, invite_code, created_at, last_seen_at)\n"
            " VALUES (?, ?, ?, ?, ?, ?)",
            (email, nickname, hash_password(password), invite, now(), now()),
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
    return {"ok": True, "token": token, "user": user_public(user, True), "user_sig": tim.user_sig(str(uid)) if tim.enabled() else ""}


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
    return {"ok": True, "token": token, "user": user_public(user, True), "user_sig": tim.user_sig(str(user["id"])) if tim.enabled() else ""}


@router.post("/auth/logout")
async def logout(user: dict = Depends(current_user), authorization: str | None = Header(default=None)):
    token = (authorization or "")[7:].strip()
    with tx() as conn:
        conn.execute("DELETE FROM auth_token WHERE token = ?", (token,))
    return {"ok": True}


@router.get("/me")
async def me(user: dict = Depends(current_user)):
    with tx() as conn:
        unread = one(conn, "SELECT COALESCE(SUM(unread), 0) AS c FROM conversation WHERE user_id = ? AND hidden = 0", (user["id"],))["c"]
        pending = one(
            conn,
            "SELECT COUNT(*) AS c FROM friend_request WHERE to_id = ? AND status = 'pending' AND read_at = 0",
            (user["id"],),
        )["c"]
    return {"user": user_public(user, True), "unread": int(unread), "pending_requests": int(pending)}


# ---------------------------------------------------------------- 好友

@router.get("/friends")
async def list_friends(user: dict = Depends(current_user)):
    with tx() as conn:
        groups = rows(conn, "SELECT * FROM friend_group WHERE user_id = ? ORDER BY sort, id", (user["id"],))
        items = rows(
            conn,
            "SELECT u.*, f.remark, f.group_id FROM friendship f JOIN user u ON u.id = f.friend_id\n"
            " WHERE f.user_id = ? AND u.status = 'active' ORDER BY u.nickname COLLATE NOCASE",
            (user["id"],),
        )
    grouped: dict[int, list] = {int(g["id"]): [] for g in groups}
    grouped[0] = []
    for item in items:
        pub = user_public(item, HUB.is_online(item["id"]))
        pub["remark"] = item["remark"] or ""
        pub["group_id"] = int(item["group_id"] or 0)
        pub["conv"] = f"0:{item['id']}"
        grouped.setdefault(int(item["group_id"] or 0), []).append(pub)
    result = [{"id": 0, "name": "我的好友", "count": len(grouped[0]), "items": grouped[0]}]
    for g in groups:
        gid = int(g["id"])
        result.append({"id": gid, "name": g["name"], "count": len(grouped.get(gid, [])), "items": grouped.get(gid, [])})
    return {"groups": result, "total": len(items)}


@router.get("/friends/{friend_id}")
async def friend_detail(friend_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        row = one(
            conn,
            "SELECT u.*, f.remark, f.group_id, f.created_at AS friend_since FROM friendship f\n"
            " JOIN user u ON u.id = f.friend_id WHERE f.user_id = ? AND f.friend_id = ?",
            (user["id"], friend_id),
        )
        if row is None:
            fail("E_NOT_FRIEND", "你们还不是好友", 404)
        blocked = one(conn, "SELECT 1 FROM block WHERE user_id = ? AND blocked_id = ?", (user["id"], friend_id)) is not None
    pub = user_public(row, HUB.is_online(friend_id))
    pub.update({"remark": row["remark"] or "", "group_id": int(row["group_id"] or 0),
                "friend_since": int(row["friend_since"] or 0), "blocked": blocked, "conv": f"0:{friend_id}"})
    return pub


@router.patch("/friends/{friend_id}")
async def update_friend(friend_id: int, body: dict, user: dict = Depends(current_user)):
    with tx() as conn:
        if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], friend_id)) is None:
            fail("E_NOT_FRIEND", "你们还不是好友", 404)
        if "remark" in body:
            conn.execute("UPDATE friendship SET remark = ? WHERE user_id = ? AND friend_id = ?",
                         ((body.get("remark") or "")[:96], user["id"], friend_id))
        if "group_id" in body:
            gid = int(body.get("group_id") or 0)
            if gid and one(conn, "SELECT 1 FROM friend_group WHERE id = ? AND user_id = ?", (gid, user["id"])) is None:
                fail("E_GROUP", "分组不存在")
            conn.execute("UPDATE friendship SET group_id = ? WHERE user_id = ? AND friend_id = ?",
                         (gid, user["id"], friend_id))
        row = one(conn, "SELECT * FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], friend_id))
        events = emit(conn, [user["id"]], "friend.updated", {"friend_id": friend_id, "remark": row["remark"],
                                                            "group_id": int(row["group_id"] or 0)})
    await HUB.push_many(events)
    return {"ok": True}


@router.delete("/friends/{friend_id}")
async def delete_friend(friend_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        conn.execute("DELETE FROM friendship WHERE (user_id = ? AND friend_id = ?) OR (user_id = ? AND friend_id = ?)",
                     (user["id"], friend_id, friend_id, user["id"]))
        # 各自收到「对方已不是好友」，前端据此把条目与会话标成非好友
        events = emit(conn, [user["id"]], "friend.removed", {"user_id": friend_id})
        events += emit(conn, [friend_id], "friend.removed", {"user_id": user["id"]})
    await HUB.push_many(events)
    asyncio.create_task(tim.friend_delete(user["id"], [friend_id]))
    return {"ok": True}


# ---------------------------------------------------------------- 好友申请

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
    out = []
    for it in items:
        out.append({
            "id": int(it["id"]),
            "user": {"id": int(it["to_id"] if box == "out" else it["from_id"]),
                     "nickname": it["nickname"], "avatar": it["avatar"], "email": it["email"]},
            "wording": it["wording"],
            "source": it["source"],
            "status": it["status"],
            "unread": not bool(it["read_at"]) and it["status"] == "pending",
            "created_at": int(it["created_at"]),
        })
    return {"items": out, "unread": sum(1 for x in out if x["unread"])}


@router.post("/friend-requests")
async def create_request(body: dict, user: dict = Depends(current_user)):
    to_id = int(body.get("to_id") or 0)
    if to_id <= 0 or to_id == user["id"]:
        fail("E_PARAM", "目标用户不正确")
    with tx() as conn:
        target = one(conn, "SELECT * FROM user WHERE id = ? AND status = 'active'", (to_id,))
        if target is None:
            fail("E_NOT_FOUND", "用户不存在", 404)
        if one(conn, "SELECT 1 FROM friendship WHERE user_id = ? AND friend_id = ?", (user["id"], to_id)):
            fail("E_ALREADY_FRIEND", "你们已经是好友了")
        if one(conn, "SELECT 1 FROM block WHERE user_id = ? AND blocked_id = ?", (to_id, user["id"])):
            fail("E_BLOCKED", "对方已将你加入黑名单", 403)
        pending = one(conn, "SELECT * FROM friend_request WHERE from_id = ? AND to_id = ? AND status = 'pending'",
                      (user["id"], to_id))
        if pending is not None:
            return {"ok": True, "id": int(pending["id"]), "duplicated": True}
        conn.execute(
            "INSERT INTO friend_request (from_id, to_id, wording, source, remark, group_id, status, created_at)\n"
            " VALUES (?, ?, ?, ?, ?, ?, 'pending', ?)",
            (user["id"], to_id, (body.get("wording") or "")[:256], body.get("source") or "search",
             (body.get("remark") or "")[:96], int(body.get("group_id") or 0), now()),
        )
        req_id = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        events = emit(conn, [to_id], "friend.request", {
            "id": req_id,
            "user": user_public(user, True),
            "wording": body.get("wording") or "",
            "source": body.get("source") or "search",
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
        conn.execute("UPDATE friend_request SET status = 'accepted', handled_at = ?, read_at = ? WHERE id = ?",
                     (ts, ts, req_id))
        # 双向关系；备注只对发起方生效（他申请时填的）
        conn.execute("INSERT OR REPLACE INTO friendship (user_id, friend_id, remark, group_id, created_at) VALUES (?, ?, '', ?, ?)",
                     (user["id"], from_id, 0, ts))
        conn.execute("INSERT OR REPLACE INTO friendship (user_id, friend_id, remark, group_id, created_at) VALUES (?, ?, ?, ?, ?)",
                     (from_id, user["id"], req["remark"] or "", int(req["group_id"] or 0), ts))
        # 双方各建一个会话，未读为 0（还没有消息）
        for uid, peer in ((user["id"], from_id), (from_id, user["id"])):
            conn.execute(
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, unread, hidden)\n"
                " VALUES (?, 0, ?, '', ?, 0, 0)\n"
                " ON CONFLICT(user_id, conv_type, conv_key) DO UPDATE SET hidden = 0",
                (uid, str(peer), ts),
            )
        me_row = one(conn, "SELECT * FROM user WHERE id = ?", (user["id"],))
        peer_row = one(conn, "SELECT * FROM user WHERE id = ?", (from_id,))
        events = emit(conn, [from_id], "friend.accepted", {"user": user_public(me_row, True), "conv": f"0:{user['id']}"})
        events += emit(conn, [user["id"]], "friend.accepted", {"user": user_public(peer_row, True), "conv": f"0:{from_id}"})
    await HUB.push_many(events)
    asyncio.create_task(tim.friend_import(user["id"], [{"To_Account": str(from_id)}]))
    asyncio.create_task(tim.friend_import(from_id, [{"To_Account": str(user["id"]), "Remark": req["remark"] or ""}]))
    return {"ok": True}


@router.post("/friend-requests/{req_id}/reject")
async def reject_request(req_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        req = one(conn, "SELECT * FROM friend_request WHERE id = ? AND to_id = ?", (req_id, user["id"]))
        if req is None:
            fail("E_NOT_FOUND", "申请不存在", 404)
        if req["status"] != "pending":
            fail("E_STATE", "这条申请已经处理过了")
        conn.execute("UPDATE friend_request SET status = 'rejected', handled_at = ?, read_at = ? WHERE id = ?",
                     (now(), now(), req_id))
        events = emit(conn, [int(req["from_id"])], "friend.request.handled", {"id": req_id, "status": "rejected"})
    await HUB.push_many(events)
    return {"ok": True}


@router.post("/friend-requests/read")
async def read_requests(user: dict = Depends(current_user)):
    with tx() as conn:
        conn.execute("UPDATE friend_request SET read_at = ? WHERE to_id = ? AND read_at = 0", (now(), user["id"]))
    return {"ok": True}


# ---------------------------------------------------------------- 好友分组

@router.get("/friend-groups")
async def list_friend_groups(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(conn, "SELECT * FROM friend_group WHERE user_id = ? ORDER BY sort, id", (user["id"],))
    return {"items": [{"id": int(x["id"]), "name": x["name"], "sort": int(x["sort"])} for x in items]}


@router.post("/friend-groups")
async def create_friend_group(body: dict, user: dict = Depends(current_user)):
    name = (body.get("name") or "").strip()[:30]
    if not name:
        fail("E_PARAM", "分组名不能为空")
    with tx() as conn:
        if one(conn, "SELECT 1 FROM friend_group WHERE user_id = ? AND name = ?", (user["id"], name)):
            fail("E_EXISTS", "同名分组已经存在")
        conn.execute("INSERT INTO friend_group (user_id, name, sort, created_at) VALUES (?, ?, 0, ?)",
                     (user["id"], name, now()))
        gid = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        events = emit(conn, [user["id"]], "friend.group.updated", {"id": gid, "name": name, "deleted": False})
    await HUB.push_many(events)
    return {"ok": True, "id": gid}


@router.patch("/friend-groups/{group_id}")
async def rename_friend_group(group_id: int, body: dict, user: dict = Depends(current_user)):
    name = (body.get("name") or "").strip()[:30]
    with tx() as conn:
        if one(conn, "SELECT 1 FROM friend_group WHERE id = ? AND user_id = ?", (group_id, user["id"])) is None:
            fail("E_NOT_FOUND", "分组不存在", 404)
        conn.execute("UPDATE friend_group SET name = ? WHERE id = ?", (name, group_id))
        events = emit(conn, [user["id"]], "friend.group.updated", {"id": group_id, "name": name, "deleted": False})
    await HUB.push_many(events)
    return {"ok": True}


@router.delete("/friend-groups/{group_id}")
async def delete_friend_group(group_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        conn.execute("UPDATE friendship SET group_id = 0 WHERE user_id = ? AND group_id = ?", (user["id"], group_id))
        conn.execute("DELETE FROM friend_group WHERE id = ? AND user_id = ?", (group_id, user["id"]))
        events = emit(conn, [user["id"]], "friend.group.updated", {"id": group_id, "deleted": True})
    await HUB.push_many(events)
    return {"ok": True}


# ---------------------------------------------------------------- 黑名单

@router.get("/blocks")
async def list_blocks(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(conn, "SELECT u.*, b.created_at AS blocked_at FROM block b JOIN user u ON u.id = b.blocked_id WHERE b.user_id = ?",
                     (user["id"],))
    return {"items": [{**user_public(x, False), "blocked_at": int(x["blocked_at"])} for x in items]}


@router.post("/blocks")
async def add_block(body: dict, user: dict = Depends(current_user)):
    target = int(body.get("user_id") or 0)
    if target <= 0 or target == user["id"]:
        fail("E_PARAM", "目标用户不正确")
    with tx() as conn:
        conn.execute("INSERT OR IGNORE INTO block (user_id, blocked_id, created_at) VALUES (?, ?, ?)",
                     (user["id"], target, now()))
        events = emit(conn, [user["id"]], "block.updated", {"user_id": target, "blocked": True})
    await HUB.push_many(events)
    return {"ok": True}


@router.delete("/blocks/{target_id}")
async def remove_block(target_id: int, user: dict = Depends(current_user)):
    with tx() as conn:
        conn.execute("DELETE FROM block WHERE user_id = ? AND blocked_id = ?", (user["id"], target_id))
        events = emit(conn, [user["id"]], "block.updated", {"user_id": target_id, "blocked": False})
    await HUB.push_many(events)
    return {"ok": True}


# ---------------------------------------------------------------- 会话

@router.get("/conversations")
async def list_conversations(user: dict = Depends(current_user)):
    with tx() as conn:
        items = rows(
            conn,
            "SELECT * FROM conversation WHERE user_id = ? AND hidden = 0 ORDER BY pinned DESC, last_at DESC LIMIT 200",
            (user["id"],),
        )
        views = [conversation_view(conn, it, user["id"]) for it in items]
    return {"items": views, "unread": sum(v["unread"] for v in views)}


@router.post("/conversations/{conv:path}/read")
async def read_conversation(conv: str, user: dict = Depends(current_user)):
    conv_type, key = parse_conv(conv)
    with tx() as conn:
        conn.execute("UPDATE conversation SET unread = 0 WHERE user_id = ? AND conv_type = ? AND conv_key = ?",
                     (user["id"], conv_type, key))
        total = int(one(conn, "SELECT COALESCE(SUM(unread), 0) AS c FROM conversation WHERE user_id = ?", (user["id"],))["c"])
        events = emit(conn, [user["id"]], "conversation.updated", {"conv": conv, "unread": 0})
    await HUB.push_many(events)
    return {"ok": True, "unread": total}


@router.delete("/conversations/{conv:path}")
async def hide_conversation(conv: str, user: dict = Depends(current_user)):
    conv_type, key = parse_conv(conv)
    with tx() as conn:
        conn.execute("UPDATE conversation SET hidden = 1, unread = 0 WHERE user_id = ? AND conv_type = ? AND conv_key = ?",
                     (user["id"], conv_type, key))
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
        params = [conv_type, canonical]
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
            exist = one(conn, "SELECT * FROM message WHERE from_id = ? AND client_msg_id = ?", (user["id"], client_msg_id))
            if exist is not None:
                return {"ok": True, "message": msg_payload(exist), "duplicated": True}

        if conv_type == 0:
            peer = int(key)
            require_friend(conn, user["id"], peer)
            ensure_not_blocked(conn, user["id"], peer)
            to_id, group_id = peer, 0
            # (收件人, 该收件人视角下的会话键, 未读增量)
            recipients = [(user["id"], str(peer), 0), (peer, str(user["id"]), 1)]
        else:
            group_id = int(key)
            require_member(conn, group_id, user["id"])
            to_id = 0
            members = group_members(conn, group_id)
            recipients = [(uid, str(group_id), 0 if uid == user["id"] else 1) for uid in members]

        canonical = canonical_conv_key(conv_type, key, user["id"])
        ts = now()
        conn.execute(
            "INSERT INTO message (conv_type, conv_key, from_id, to_id, group_id, client_msg_id, body, kind, created_at)\n"
            " VALUES (?, ?, ?, ?, ?, ?, ?, 'text', ?)",
            (conv_type, canonical, user["id"], to_id, group_id, client_msg_id, text, ts),
        )
        msg_id = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        row = one(conn, "SELECT * FROM message WHERE id = ?", (msg_id,))
        payload = msg_payload(row)

        events: list[dict] = []
        for uid, own_key, delta in recipients:
            touch_conversation(conn, uid, conv_type, own_key, msg_id, text, ts, delta)
            events += emit(conn, [uid], "message.new",
                           {"message": payload, "conv": conv_label(conv_type, own_key), "unread_delta": delta})

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
        conn.execute("UPDATE message SET kind = 'recalled', body = '', recalled_at = ? WHERE id = ?", (now(), msg_id))
        conv_type = int(row["conv_type"])
        if conv_type == 0:
            from_id, to_id = int(row["from_id"]), int(row["to_id"])
            recipients = [(from_id, str(to_id)), (to_id, str(from_id))]
        else:
            gid = int(row["group_id"])
            recipients = [(uid, str(gid)) for uid in group_members(conn, gid)]
        # 只改「最后一条正好是它」的会话预览，按每人视角各改一次
        for uid, own_key in recipients:
            conn.execute(
                "UPDATE conversation SET last_body = '[已撤回]' WHERE user_id = ? AND conv_type = ? AND conv_key = ? AND last_msg_id = ?",
                (uid, conv_type, own_key, msg_id),
            )
        events: list[dict] = []
        for uid, own_key in recipients:
            events += emit(conn, [uid], "message.recalled",
                           {"message_id": msg_id, "conv": conv_label(conv_type, own_key)})
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
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, unread, hidden)\n"
                " VALUES (?, 1, ?, '', ?, 0, 0)\n"
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
    return {"id": int(g["id"]), "name": g["name"], "owner_id": int(g["owner_id"]), "notice": g["notice"],
            "conv": f"1:{group_id}",
            "members": [{**user_public(x, HUB.is_online(x["id"])), "role": int(x["role"]),
                         "joined_at": int(x["joined_at"])} for x in members]}


@router.patch("/groups/{group_id}")
async def update_group(group_id: int, body: dict, user: dict = Depends(current_user)):
    with tx() as conn:
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        if g is None:
            fail("E_NOT_FOUND", "群不存在", 404)
        if int(g["owner_id"]) != user["id"]:
            fail("E_NOAUTH", "只有群主可以修改群资料", 403)
        if "name" in body:
            conn.execute("UPDATE chat_group SET name = ? WHERE id = ?", ((body.get("name") or g["name"])[:30], group_id))
        if "notice" in body:
            conn.execute("UPDATE chat_group SET notice = ? WHERE id = ?", ((body.get("notice") or "")[:500], group_id))
        g = one(conn, "SELECT * FROM chat_group WHERE id = ?", (group_id,))
        events = emit(conn, group_members(conn, group_id), "group.updated",
                      {"id": group_id, "name": g["name"], "notice": g["notice"], "owner_id": int(g["owner_id"])})
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
                "INSERT INTO conversation (user_id, conv_type, conv_key, last_body, last_at, unread, hidden)\n"
                " VALUES (?, 1, ?, '', ?, 0, 0)\n"
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


# ---------------------------------------------------------------- 搜索 / 在线

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
            out.append({**user_public(it, HUB.is_online(it["id"])), "relation": rel})
    return {"items": out}


@router.get("/presence")
async def presence(ids: str = Query(""), user: dict = Depends(current_user)):
    wanted = []
    for part in ids.split(","):
        part = part.strip()
        if part.isdigit():
            wanted.append(int(part))
    if not wanted:
        return {"items": {}}
    with tx() as conn:
        allowed = {r["friend_id"] for r in rows(conn, "SELECT friend_id FROM friendship WHERE user_id = ?", (user["id"],))}
    return {"items": {str(uid): HUB.is_online(uid) for uid in wanted if uid in allowed}}


@router.get("/snapshot")
async def snapshot(user: dict = Depends(current_user)):
    """全量快照：客户端 resume 失败时用，一次拿齐所有状态。"""
    friends = await list_friends(user)
    conversations = await list_conversations(user)
    requests = await list_requests("in", user)
    with tx() as conn:
        groups = rows(conn, "SELECT g.*, m.role FROM group_member m JOIN chat_group g ON g.id = m.group_id WHERE m.user_id = ?",
                      (user["id"],))
        seq = int(one(conn, "SELECT COALESCE(MAX(seq), 0) AS s FROM event_log")["s"])
    return {"me": user_public(user, True), "friends": friends, "conversations": conversations,
            "requests": requests, "groups_len": len(groups), "server_seq": seq}


# ---------------------------------------------------------------- 健康检查

@router.get("/health")
async def health():
    return {"ok": True, "tim": tim.self_check(), "ws_online": len(HUB._conns)}
