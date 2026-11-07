"""端到端冒烟：注册 → 加好友 → 同意 → WS 实时消息 → 未读 → 撤回 → 群聊。

前置：uvicorn 跑在 127.0.0.1:8099，config.json 里 debug_code=true
运行：python tests/smoke.py
"""

import asyncio
import json
import random
import sys

import httpx
import websockets

BASE = "http://127.0.0.1:8099"
WS = "ws://127.0.0.1:8099/ws"
ok_count = 0
fail_count = 0


def check(name: str, cond: bool, extra: str = "") -> None:
    global ok_count, fail_count
    if cond:
        ok_count += 1
        print(f"  PASS  {name}")
    else:
        fail_count += 1
        print(f"  FAIL  {name} {extra}")


def api(token: str | None, method: str, path: str, body: dict | None = None) -> dict:
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    with httpx.Client(timeout=10) as c:
        r = c.request(method, BASE + path, json=body, headers=headers)
    try:
        return {"status": r.status_code, **r.json()}
    except Exception:
        return {"status": r.status_code, "text": r.text}


def register(prefix: str) -> tuple[str, int]:
    email = f"{prefix}{random.randint(1000, 9999)}@test.local"
    code = api(None, "POST", "/api/v1/auth/send-code", {"email": email})["debug_code"]
    res = api(None, "POST", "/api/v1/auth/register",
              {"email": email, "password": "test123456", "nickname": prefix, "code": code, "invite": ""})
    assert res.get("ok"), res
    return res["token"], int(res["user"]["id"])


async def main() -> None:
    print("== 健康检查 ==")
    h = api(None, "GET", "/api/v1/health")
    check("health ok", h.get("ok") is True, str(h))

    print("== 注册 ==")
    ta, ida = register("alice")
    tb, idb = register("bob")
    check("两个账号注册成功", ida != idb)

    print("== 加好友 ==")
    r = api(ta, "POST", "/api/v1/friend-requests", {"to_id": idb, "wording": "我是 Alice"})
    check("发送申请", r.get("ok") is True, str(r))
    req_id = r["id"]

    bob_in = api(tb, "GET", "/api/v1/friend-requests?box=in")
    check("Bob 收到申请且未读", bob_in["unread"] == 1, str(bob_in))
    check("申请列表带验证消息", bob_in["items"][0]["wording"] == "我是 Alice", str(bob_in["items"][0]))

    api(tb, "POST", "/api/v1/friend-requests/read")
    bob_in = api(tb, "GET", "/api/v1/friend-requests?box=in")
    check("已读后红点清零", bob_in["unread"] == 0, str(bob_in))

    check("同意申请", api(tb, "POST", f"/api/v1/friend-requests/{req_id}/accept").get("ok") is True)
    fa = api(ta, "GET", "/api/v1/friends")
    fb = api(tb, "GET", "/api/v1/friends")
    check("双方好友列表都有对方", fa["total"] == 1 and fb["total"] == 1, f"{fa['total']}/{fb['total']}")

    print("== 分组 / 备注 ==")
    g = api(ta, "POST", "/api/v1/friend-groups", {"name": "同学"})
    check("创建分组", g.get("ok") is True, str(g))
    api(ta, "PATCH", f"/api/v1/friends/{idb}", {"remark": "小 B", "group_id": g["id"]})
    fa = api(ta, "GET", "/api/v1/friends")
    grp = next(x for x in fa["groups"] if x["id"] == g["id"])
    check("备注与分组生效", grp["count"] == 1 and grp["items"][0]["remark"] == "小 B", json.dumps(grp, ensure_ascii=False))

    print("== WebSocket 实时消息 ==")
    async with websockets.connect(f"{WS}?token={tb}") as wsb:
        hello = json.loads(await asyncio.wait_for(wsb.recv(), timeout=5))
        check("hello 带 server_seq", hello.get("op") == "hello" and "server_seq" in hello, str(hello))

        client_id = "c-" + str(random.randint(10000, 99999))
        res = api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "你好 Bob", "client_msg_id": client_id})
        check("发送成功", res.get("ok") is True, str(res))

        evt = json.loads(await asyncio.wait_for(wsb.recv(), timeout=5))
        while evt.get("type") != "message.new":
            evt = json.loads(await asyncio.wait_for(wsb.recv(), timeout=5))
        check("Bob 通过 WS 收到消息", evt["data"]["message"]["body"] == "你好 Bob", str(evt)[:200])
        check("事件 seq 单调", isinstance(evt.get("seq"), int) and evt["seq"] > 0, str(evt.get("seq")))

        convs = api(tb, "GET", "/api/v1/conversations")
        bob_conv = next(c for c in convs["items"] if c["conv"] == f"0:{ida}")
        check("Bob 会话未读为 1", bob_conv["unread"] == 1, str(bob_conv))

        read = api(tb, "POST", f"/api/v1/conversations/0:{ida}/read")
        check("已读返回剩余总数", read.get("unread") == 0, str(read))
        convs = api(tb, "GET", "/api/v1/conversations")
        check("会话未读清零", next(c for c in convs["items"] if c["conv"] == f"0:{ida}")["unread"] == 0)

        print("== 幂等 ==")
        r1 = api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "重复", "client_msg_id": "dup-1"})
        r2 = api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "重复", "client_msg_id": "dup-1"})
        check("同一 client_msg_id 只落一条", r2.get("duplicated") is True and r1["message"]["id"] == r2["message"]["id"],
              f"{r1.get('message',{}).get('id')}/{r2.get('message',{}).get('id')}")

        print("== 撤回 ==")
        mid = r1["message"]["id"]
        check("撤回成功", api(ta, "POST", f"/api/v1/messages/{mid}/recall").get("ok") is True)
        msgs = api(tb, "GET", f"/api/v1/messages?conv=0:{ida}")
        recalled = [m for m in msgs["items"] if m["id"] == mid][0]
        check("对方看到已撤回", recalled["kind"] == "recalled" and recalled["body"] == "", str(recalled))

        print("== 断线补发 ==")
        seq_before = 0
        while True:
            try:
                e = json.loads(await asyncio.wait_for(wsb.recv(), timeout=1))
            except asyncio.TimeoutError:
                break
            seq_before = max(seq_before, e.get("seq") or 0)
        api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "离线期间的消息"})

    async with websockets.connect(f"{WS}?token={tb}") as wsb2:
        hello = json.loads(await asyncio.wait_for(wsb2.recv(), timeout=5))
        check("hello 带 server_seq", hello.get("op") == "hello", str(hello))
        # 客户端应该用「自己最后收到的 seq」续传，而不是服务端当前最大值
        check("断线前有已收到的 seq", seq_before > 0, str(seq_before))
        await wsb2.send(json.dumps({"op": "resume", "since": seq_before}))
        got = []
        while True:
            e = json.loads(await asyncio.wait_for(wsb2.recv(), timeout=5))
            if e.get("op") == "resumed":
                break
            got.append(e)
        check("重连补发离线消息", any(x.get("type") == "message.new" and x["data"]["message"]["body"] == "离线期间的消息" for x in got),
              str(got)[:200])

        print("== 群聊 ==")
        grp_res = api(ta, "POST", "/api/v1/groups", {"name": "测试群", "members": [idb]})
        check("建群成功", grp_res.get("ok") is True, str(grp_res))
        gid = grp_res["id"]
        groups_b = api(tb, "GET", "/api/v1/groups")
        check("成员看到群", any(x["id"] == gid for x in groups_b["items"]), str(groups_b))
        api(ta, "POST", "/api/v1/messages", {"conv": f"1:{gid}", "body": "群里好"})
        convs_b = api(tb, "GET", "/api/v1/conversations")
        check("群会话出现且未读", any(c["conv"] == f"1:{gid}" and c["unread"] >= 1 for c in convs_b["items"]), str(convs_b["items"])[:300])
        check("踢人成功", api(ta, "DELETE", f"/api/v1/groups/{gid}/members/{idb}").get("ok") is True)
        check("被踢后不能发言", api(tb, "POST", "/api/v1/messages", {"conv": f"1:{gid}", "body": "还在吗"}).get("error", {}).get("code") == "E_NOT_MEMBER")

    print("== 删除好友 ==")
    api(ta, "DELETE", f"/api/v1/friends/{idb}")
    fa = api(ta, "GET", "/api/v1/friends")
    check("好友列表清空", fa["total"] == 0, str(fa["total"]))
    check("非好友不能发消息", api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "x"}).get("error", {}).get("code") == "E_NOT_FRIEND")

    print(f"\n结果：{ok_count} 通过 / {fail_count} 失败")
    if fail_count:
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main())
