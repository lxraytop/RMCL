"""FastAPI 入口：REST + WebSocket。

WS 协议（与方案文档一致）：
  → {"op":"ping"}                    ← {"op":"pong"}
  ← {"op":"hello","user_id":1,"server_seq":120}
  → {"op":"resume","since":100}      ← 补发 seq>100 的事件 + {"op":"resumed","seq":120}
"""

import asyncio
import json
import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from . import tim
from .admin import AdminAuthError, router as admin_router
from .api import ApiError, router
from .db import init_db, one, rows, tx
from .hub import HUB, trim_events
from .security import now

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("friend.main")

IDLE_TIMEOUT = 90  # 客户端 25 秒 ping，连续三次没声音就断开


@asynccontextmanager
async def lifespan(app: FastAPI):
    init_db()
    with tx() as conn:
        trim_events(conn)
    log.info("friend gateway ready, tim=%s", tim.self_check())
    yield


app = FastAPI(title="RMCL Friend Gateway", version="2.0.0", lifespan=lifespan)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(router)
app.include_router(admin_router)


@app.exception_handler(ApiError)
async def api_error_handler(request, exc: ApiError):
    return JSONResponse(status_code=exc.status, content={"error": {"code": exc.code, "message": exc.message}})


@app.exception_handler(AdminAuthError)
async def admin_auth_handler(request, exc: AdminAuthError):
    return JSONResponse(status_code=401, content={"error": "需要登录"})


@app.get("/")
async def index():
    return {"name": "RMCL Friend Gateway", "version": "2.0.0", "docs": "/docs", "ws": "/ws?token=..."}


def _load_events(user_id: int, since: int, limit: int = 500) -> list[dict]:
    with tx() as conn:
        items = rows(
            conn,
            "SELECT seq, type, payload, ts FROM event_log WHERE user_id = ? AND seq > ? ORDER BY seq ASC LIMIT ?",
            (user_id, since, limit),
        )
    out = []
    for it in items:
        out.append({"seq": int(it["seq"]), "type": it["type"], "data": json.loads(it["payload"]), "ts": int(it["ts"])})
    return out


@app.websocket("/ws")
async def websocket_endpoint(ws: WebSocket, token: str = ""):
    await ws.accept()
    with tx() as conn:
        user = one(
            conn,
            "SELECT u.* FROM auth_token t JOIN user u ON u.id = t.user_id "
            "WHERE t.token = ? AND t.expires_at > ? AND u.status = 'active'",
            (token, now()),
        )
    if user is None:
        await ws.send_text(json.dumps({"op": "error", "code": "E_AUTH", "message": "登录状态已失效"}, ensure_ascii=False))
        await ws.close()
        return

    uid = int(user["id"])
    await HUB.register(uid, ws)
    server_seq = HUB.snapshot_seq(uid)
    await ws.send_text(json.dumps({"op": "hello", "user_id": uid, "server_seq": server_seq}, ensure_ascii=False))

    async def send_events(since: int) -> int:
        with tx() as conn:
            floor_row = one(conn, "SELECT v FROM meta WHERE k = 'event_floor'")
        floor = int(floor_row["v"]) if floor_row else 0
        if since < floor:
            # 事件已被清理，补发不可靠，让客户端拉一次全量快照
            await ws.send_text(json.dumps({"op": "resync", "reason": "event_expired"}, ensure_ascii=False))
            return since
        cursor = since
        while True:
            batch = _load_events(uid, cursor)
            if not batch:
                break
            for evt in batch:
                await ws.send_text(json.dumps({"op": "event", **evt}, ensure_ascii=False))
                cursor = evt["seq"]
            if len(batch) < 500:
                break
        await ws.send_text(json.dumps({"op": "resumed", "seq": cursor}, ensure_ascii=False))
        return cursor

    try:
        while True:
            try:
                raw = await asyncio.wait_for(ws.receive_text(), timeout=IDLE_TIMEOUT)
            except asyncio.TimeoutError:
                log.info("ws 空闲超时，断开 user=%s", uid)
                break
            try:
                msg = json.loads(raw)
            except Exception:
                continue
            op = msg.get("op")
            if op == "ping":
                await ws.send_text('{"op":"pong"}')
            elif op == "resume":
                since = int(msg.get("since") or 0)
                await send_events(max(0, since))
            elif op == "sync":
                # 显式要求全量：由客户端拉 /snapshot，这里只回一个标记
                await ws.send_text(json.dumps({"op": "resync", "reason": "client_request"}, ensure_ascii=False))
    except WebSocketDisconnect:
        pass
    except Exception as exc:
        log.warning("ws 异常 user=%s: %s", uid, exc)
    finally:
        await HUB.unregister(uid, ws)
