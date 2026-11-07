"""WebSocket 连接管理与事件分发。

对应腾讯云 IM SDK 的「事件派发」层：
- 所有状态变更先写 event_log（可补发），再推给在线连接
- presence 按连接数判定，断开后有宽限期，避免闪断造成好友列表闪烁
"""

import asyncio
import json
import logging
import time
from collections import defaultdict

from .config import CONFIG
from .db import one, rows, tx

log = logging.getLogger("friend.hub")


def emit(conn, user_ids, type_: str, payload: dict, ts: int | None = None) -> list[dict]:
    """在同一个事务里写入事件，返回待推送列表（提交后调用 hub.push_many）。"""
    ts = ts or int(time.time())
    body = json.dumps(payload, ensure_ascii=False)
    out: list[dict] = []
    seen: set[int] = set()
    for uid in user_ids:
        uid = int(uid)
        if uid <= 0 or uid in seen:
            continue
        seen.add(uid)
        cur = conn.execute(
            "INSERT INTO event_log (user_id, type, payload, ts) VALUES (?, ?, ?, ?)",
            (uid, type_, body, ts),
        )
        out.append({"seq": cur.lastrowid, "user_id": uid, "type": type_, "data": payload, "ts": ts})
    return out


def friend_ids(conn, user_id: int) -> list[int]:
    return [r["friend_id"] for r in rows(conn, "SELECT friend_id FROM friendship WHERE user_id = ?", (user_id,))]


def trim_events(conn) -> None:
    days = int(CONFIG.get("event_retention_days") or 7)
    cutoff = int(time.time()) - days * 86400
    conn.execute("DELETE FROM event_log WHERE ts < ?", (cutoff,))
    # 记录「可补发水位」：客户端 last_seq 早于它就不能靠补发恢复，必须全量 resync
    row = one(conn, "SELECT COALESCE(MIN(seq), 0) AS s FROM event_log")
    floor = max(0, int(row["s"]) - 1) if row else 0
    conn.execute("INSERT OR REPLACE INTO meta (k, v) VALUES ('event_floor', ?)", (str(floor),))


class Hub:
    def __init__(self) -> None:
        self._conns: dict[int, set] = defaultdict(set)

    def is_online(self, user_id: int) -> bool:
        return bool(self._conns.get(int(user_id)))

    async def register(self, user_id: int, ws) -> bool:
        user_id = int(user_id)
        became_online = not self._conns[user_id]
        self._conns[user_id].add(ws)
        with tx() as conn:
            conn.execute(
                "INSERT INTO presence (user_id, conn_count, last_seen_at) VALUES (?, 1, ?)\n"
                " ON CONFLICT(user_id) DO UPDATE SET conn_count = conn_count + 1, last_seen_at = excluded.last_seen_at",
                (user_id, int(time.time())),
            )
            conn.execute("UPDATE user SET last_seen_at = ? WHERE id = ?", (int(time.time()), user_id))
        return became_online

    async def unregister(self, user_id: int, ws) -> bool:
        """返回是否已成为离线（连接数为 0）。"""
        user_id = int(user_id)
        self._conns[user_id].discard(ws)
        if self._conns[user_id]:
            return False
        self._conns.pop(user_id, None)
        with tx() as conn:
            conn.execute(
                "INSERT INTO presence (user_id, conn_count, last_seen_at) VALUES (?, 0, ?)\n"
                " ON CONFLICT(user_id) DO UPDATE SET conn_count = 0, last_seen_at = excluded.last_seen_at",
                (user_id, int(time.time())),
            )
            conn.execute("UPDATE user SET last_seen_at = ? WHERE id = ?", (int(time.time()), user_id))
        return True

    async def push_many(self, events: list[dict]) -> None:
        for evt in events:
            await self.push(evt["user_id"], evt)

    async def push(self, user_id: int, evt: dict) -> None:
        targets = list(self._conns.get(int(user_id)) or [])
        if not targets:
            return
        message = {
            "op": "event",
            "seq": evt["seq"],
            "type": evt["type"],
            "data": evt["data"],
            "ts": evt["ts"],
        }
        text = json.dumps(message, ensure_ascii=False)
        for ws in targets:
            try:
                await ws.send_text(text)
            except Exception:
                # 发送失败交给 WS 循环清理，不在这里阻塞其它用户
                self._conns[int(user_id)].discard(ws)

    def snapshot_seq(self, user_id: int) -> int:
        with tx() as conn:
            row = one(conn, "SELECT COALESCE(MAX(seq), 0) AS s FROM event_log")
        return int(row["s"]) if row else 0


HUB = Hub()


async def broadcast_presence(user_id: int, online: bool) -> None:
    """向好友广播上下线。写事件 + 推送。"""
    with tx() as conn:
        targets = friend_ids(conn, user_id)
        if not targets:
            return
        events = emit(conn, targets, "presence.changed", {"user_id": user_id, "online": online})
    await HUB.push_many(events)


async def presence_after_disconnect(user_id: int) -> None:
    """断线宽限期：等一会儿再看是否真的没连接了。"""
    grace = int(CONFIG.get("presence_grace_seconds") or 30)
    if grace > 0:
        await asyncio.sleep(grace)
    if HUB.is_online(user_id):
        return
    await broadcast_presence(user_id, False)
