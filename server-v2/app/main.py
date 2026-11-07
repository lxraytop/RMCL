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
from pathlib import Path

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.openapi.docs import get_swagger_ui_html
from fastapi.responses import JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles

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


# FastAPI 默认的 /docs 从 cdn.jsdelivr.net 拉 swagger-ui，国内基本连不上，
# 页面会一直白屏。这里改成读仓库自带的 static/swagger（见 deploy 说明），
# 只有文件缺失时才回落到 CDN。
STATIC_DIR = Path(__file__).resolve().parent.parent / "static"
LOCAL_SWAGGER = (STATIC_DIR / "swagger" / "swagger-ui-bundle.js").is_file()
SWAGGER_JS = "/static/swagger/swagger-ui-bundle.js" if LOCAL_SWAGGER else \
    "https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui-bundle.js"
SWAGGER_CSS = "/static/swagger/swagger-ui.css" if LOCAL_SWAGGER else \
    "https://cdn.jsdelivr.net/npm/swagger-ui-dist@5/swagger-ui.css"

app = FastAPI(title="RMCL Friend Gateway", version="2.0.0", lifespan=lifespan,
              docs_url=None, redoc_url=None)

if STATIC_DIR.is_dir():
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")


@app.get("/docs", include_in_schema=False)
async def swagger_docs():
    return get_swagger_ui_html(
        openapi_url=app.openapi_url,
        title=app.title + " · API 文档",
        swagger_js_url=SWAGGER_JS,
        swagger_css_url=SWAGGER_CSS,
        swagger_favicon_url="",
    )


@app.get("/redoc", include_in_schema=False)
async def redoc_redirect():
    # Redoc 的脚本同样在 CDN 上，国内打不开，统一导到本地版 Swagger
    return RedirectResponse("/docs")

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
