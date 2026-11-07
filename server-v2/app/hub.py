"""WebSocket 连接管理与事件分发。

对应腾讯云 IM SDK 的「事件派发」层：
- 状态变更先写 event_log（可补发），再推给在线连接
- 不含在线状态：Lite 版本不维护 presence
"""

import json
import logging
import time
from collections import defaultdict

from .config import CONFIG
from .db import one, tx

log = logging.getLogger("friend.hub")


def emit(conn, user_ids, type_: str, payload: dict, ts: int | None = None) -> list[dict]:
    """在同一事务里写事件，返回待推送列表（提交后调用 HUB.push_many）。"""
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


def trim_events(conn) -> None:
    days = int(CONFIG.get("event_retention_days") or 7)
    cutoff = int(time.time()) - days * 86400
    conn.execute("DELETE FROM event_log WHERE ts < ?", (cutoff,))
    # 记录可补发水位：客户端 last_seq 早于它就只能拉全量快照
    row = one(conn, "SELECT COALESCE(MIN(seq), 0) AS s FROM event_log")
    floor = max(0, int(row["s"]) - 1) if row else 0
    conn.execute("INSERT OR REPLACE INTO meta (k, v) VALUES ('event_floor', ?)", (str(floor),))


class Hub:
    def __init__(self) -> None:
        self._conns: dict[int, set] = defaultdict(set)

    async def register(self, user_id: int, ws) -> None:
        self._conns[int(user_id)].add(ws)

    async def unregister(self, user_id: int, ws) -> None:
        user_id = int(user_id)
        self._conns[user_id].discard(ws)
        if not self._conns[user_id]:
            self._conns.pop(user_id, None)

    def is_online(self, user_id: int) -> bool:
        """仅表示「当前有 WS 连接」，不对外暴露为好友在线状态。"""
        return bool(self._conns.get(int(user_id)))

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
                self._conns[int(user_id)].discard(ws)

    def snapshot_seq(self, user_id: int) -> int:
        with tx() as conn:
            row = one(conn, "SELECT COALESCE(MAX(seq), 0) AS s FROM event_log")
        return int(row["s"]) if row else 0


HUB = Hub()
