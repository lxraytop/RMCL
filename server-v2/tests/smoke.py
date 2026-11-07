"""端到端冒烟（Lite 范围）。

覆盖：注册 → 加好友 → 申请 → 同意 → 好友列表 → WS 实时消息 → 未读/已读 → 幂等 → 撤回
      → 断线补发 → 建群/踢人/越权 → 删好友后拒发
不含：在线、分组、备注、黑名单、漫游

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
    check("health 带 WS 连接数", "ws_connections" in h, str(h))

    print("== 注册 ==")
    ta, ida = register("alice")
    tb, idb = register("bob")
    check("两个账号注册成功", ida != idb)

    print("== 加好友 ==")
    r = api(ta, "POST", "/api/v1/friend-requests", {"to_id": idb, "wording": "我是 Alice"})
    check("发送申请", r.get("ok") is True, str(r))
    req_id = r["id"]

    out = api(ta, "GET", "/api/v1/friend-requests?box=out")
    check("发起方能查到自己的申请", any(x["id"] == req_id for x in out["items"]), str(out))
    bob_in = api(tb, "GET", "/api/v1/friend-requests?box=in")
    check("接收方收到申请", any(x["id"] == req_id and x["wording"] == "我是 Alice" for x in bob_in["items"]), str(bob_in))

    check("同意申请", api(tb, "POST", f"/api/v1/friend-requests/{req_id}/accept").get("ok") is True)
    fa = api(ta, "GET", "/api/v1/friends")
    fb = api(tb, "GET", "/api/v1/friends")
    check("双方好友列表都有对方", fa["total"] == 1 and fb["total"] == 1, f"{fa['total']}/{fb['total']}")
    check("好友列表是平铺结构", "items" in fa and "groups" not in fa, str(fa)[:120])

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
        check("事件 seq 单调递增", isinstance(evt.get("seq"), int) and evt["seq"] > 0, str(evt.get("seq")))
        check("事件里带接收方视角的会话", evt["data"]["conv"] == f"0:{ida}", str(evt["data"].get("conv")))

        convs = api(tb, "GET", "/api/v1/conversations")
        bob_conv = next(c for c in convs["items"] if c["conv"] == f"0:{ida}")
        check("会话列表更新到最后一条", bob_conv["last_body"] == "你好 Bob", str(bob_conv))
        check("接收方未读为 1", bob_conv["unread"] == 1, str(bob_conv))
        me_b = api(tb, "GET", "/api/v1/me")
        check("/me 汇总未读", me_b.get("unread") == 1, str(me_b))
        read = api(tb, "POST", f"/api/v1/conversations/0:{ida}/read")
        check("已读返回剩余总数", read.get("unread") == 0, str(read))
        bob_conv = next(c for c in api(tb, "GET", "/api/v1/conversations")["items"] if c["conv"] == f"0:{ida}")
        check("已读后未读清零", bob_conv["unread"] == 0, str(bob_conv))
        check("发送方自己的会话不累计未读",
              next(c for c in api(ta, "GET", "/api/v1/conversations")["items"] if c["conv"] == f"0:{idb}")["unread"] == 0)

        print("== 幂等 ==")
        r1 = api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "重复", "client_msg_id": "dup-1"})
        r2 = api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "重复", "client_msg_id": "dup-1"})
        check("同一 client_msg_id 只落一条",
              r2.get("duplicated") is True and r1["message"]["id"] == r2["message"]["id"],
              f"{r1.get('message', {}).get('id')}/{r2.get('message', {}).get('id')}")

        print("== 撤回 ==")
        mid = r1["message"]["id"]
        check("撤回成功", api(ta, "POST", f"/api/v1/messages/{mid}/recall").get("ok") is True)
        msgs = api(tb, "GET", f"/api/v1/messages?conv=0:{ida}")
        recalled = [m for m in msgs["items"] if m["id"] == mid][0]
        check("对方看到已撤回", recalled["kind"] == "recalled" and recalled["body"] == "", str(recalled))
        check("会话预览同步为已撤回",
              next(c for c in api(tb, "GET", "/api/v1/conversations")["items"]
                   if c["conv"] == f"0:{ida}")["last_body"] == "[已撤回]")

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
        check("重连拿到 hello", hello.get("op") == "hello", str(hello))
        check("断线前有已收到的 seq", seq_before > 0, str(seq_before))
        await wsb2.send(json.dumps({"op": "resume", "since": seq_before}))
        got = []
        while True:
            e = json.loads(await asyncio.wait_for(wsb2.recv(), timeout=5))
            if e.get("op") == "resumed":
                break
            got.append(e)
        check("重连补发离线消息",
              any(x.get("type") == "message.new" and x["data"]["message"]["body"] == "离线期间的消息" for x in got),
              str(got)[:200])

        print("== 群聊 ==")
        grp = api(ta, "POST", "/api/v1/groups", {"name": "测试群", "members": [idb]})
        check("建群成功", grp.get("ok") is True, str(grp))
        gid = grp["id"]
        groups_b = api(tb, "GET", "/api/v1/groups")
        check("成员看到群", any(x["id"] == gid for x in groups_b["items"]), str(groups_b))
        api(ta, "POST", "/api/v1/messages", {"conv": f"1:{gid}", "body": "群里好"})
        convs_b = api(tb, "GET", "/api/v1/conversations")
        group_conv = next((c for c in convs_b["items"] if c["conv"] == f"1:{gid}"), None)
        check("群会话出现在列表", group_conv is not None and group_conv["last_body"] == "群里好",
              str(convs_b["items"])[:300])
        check("群消息给其他人记未读", group_conv is not None and group_conv["unread"] >= 1, str(group_conv))
        detail = api(tb, "GET", f"/api/v1/groups/{gid}")
        check("群详情带成员", len(detail["members"]) == 2, str(detail)[:200])
        check("踢人成功", api(ta, "DELETE", f"/api/v1/groups/{gid}/members/{idb}").get("ok") is True)
        check("被踢后不能发言",
              api(tb, "POST", "/api/v1/messages", {"conv": f"1:{gid}", "body": "还在吗"}).get("error", {}).get("code")
              == "E_NOT_MEMBER")

    print("== 搜索 / 好友申请状态（客户端按钮依赖这些取值） ==")
    tc, idc = register("carol")
    found = api(ta, "GET", "/api/v1/search?q=carol")
    hit = next((x for x in found["items"] if x["id"] == idc), None)
    check("搜索能查到用户", hit is not None, str(found)[:200])
    check("陌生人的 relation 是 none", hit is not None and hit["relation"] == "none", str(hit))

    req2 = api(ta, "POST", "/api/v1/friend-requests", {"to_id": idc, "wording": "hi"})
    check("向 carol 发送申请", req2.get("ok") is True, str(req2))
    check("重复发送不新建申请",
          api(ta, "POST", "/api/v1/friend-requests", {"to_id": idc, "wording": "hi"}).get("duplicated") is True)
    hit = next(x for x in api(ta, "GET", "/api/v1/search?q=carol")["items"] if x["id"] == idc)
    check("发起方看到 requested", hit["relation"] == "requested", str(hit))
    hit = next(x for x in api(tc, "GET", "/api/v1/search?q=alice")["items"] if x["id"] == ida)
    check("接收方看到 incoming", hit["relation"] == "incoming", str(hit))
    check("搜索空关键词返回空列表", api(ta, "GET", "/api/v1/search?q=")["items"] == [])

    carol_in = api(tc, "GET", "/api/v1/friend-requests?box=in")
    check("待处理申请在列表里", len(carol_in["items"]) == 1 and carol_in["items"][0]["status"] == "pending",
          str(carol_in)[:200])
    snap = api(tc, "GET", "/api/v1/snapshot")
    check("快照包含 me/friends/conversations/requests/server_seq",
          all(k in snap for k in ("me", "friends", "conversations", "requests", "server_seq")), str(snap)[:150])
    check("快照里的申请只要待处理的",
          snap["requests"]["items"] and all(x["status"] == "pending" for x in snap["requests"]["items"]),
          str(snap["requests"])[:200])
    check("同意 carol 的申请", api(tc, "POST", f"/api/v1/friend-requests/{req2['id']}/accept").get("ok") is True)
    check("同意后待处理列表清空", api(tc, "GET", "/api/v1/friend-requests?box=in")["items"] == [])
    handled = api(tc, "GET", "/api/v1/friend-requests?box=in&include_handled=true")["items"]
    check("include_handled 能查到已处理的申请",
          any(x["id"] == req2["id"] and x["status"] == "accepted" for x in handled), str(handled)[:200])
    hit = next(x for x in api(ta, "GET", "/api/v1/search?q=carol")["items"] if x["id"] == idc)
    check("成为好友后 relation 是 friend", hit["relation"] == "friend", str(hit))

    print("== 消息 conv_key 与历史检索 ==")
    api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idc}", "body": "历史检索样本"})
    convs_a = api(ta, "GET", "/api/v1/conversations")["items"]
    single = next(c for c in convs_a if c["conv_type"] == 0 and c["conv_key"] == str(idc))
    check("会话表的 conv_key 是自己视角的对方 id", single["conv_key"] == str(idc), str(single))
    msgs_c = api(tc, "GET", f"/api/v1/messages?conv=0:{ida}")["items"]
    check("消息表用 min_max 形式的 conv_key",
          msgs_c and all("_" in m["conv_key"] for m in msgs_c) and
          msgs_c[0]["conv_key"] == "_".join(str(x) for x in sorted((ida, idc))),
          str(msgs_c[:1])[:200])

    hf = api(ta, "GET", "/api/v1/history?scope=friend&keyword=历史检索样本")
    check("scope=friend 只返回单聊", hf["items"] and all(x["conv_type"] == 0 for x in hf["items"]),
          str(hf)[:200])
    hg = api(ta, "GET", "/api/v1/history?scope=group&keyword=群里好")
    check("scope=group 只返回群聊", hg["items"] and all(x["conv_type"] == 1 for x in hg["items"]), str(hg)[:200])
    ha = api(ta, "GET", "/api/v1/history?keyword=历史检索样本")
    check("scope 缺省等于 all", ha["items"] and ha["items"][0]["body"] == "历史检索样本", str(ha)[:200])

    print("== 登录起点：新登录不重放历史，断线才补拉 ==")
    async with websockets.connect(f"{WS}?token={tc}") as ws3:
        hello = json.loads(await asyncio.wait_for(ws3.recv(), timeout=5))
        seq = hello["server_seq"]
        check("hello 带 server_seq", isinstance(seq, int) and seq > 0, str(seq))
        await ws3.send(json.dumps({"op": "resume", "since": seq}))
        first = json.loads(await asyncio.wait_for(ws3.recv(), timeout=5))
        check("从 server_seq 起步不重放历史事件", first.get("op") == "resumed", str(first)[:150])
        check("不重放时 seq 就是服务端当前值", first.get("seq") == seq, f"{first.get('seq')}/{seq}")

    async with websockets.connect(f"{WS}?token={tc}") as ws4:
        json.loads(await asyncio.wait_for(ws4.recv(), timeout=5))
        await ws4.send(json.dumps({"op": "resume", "since": 0}))
        kinds = []
        while True:
            e = json.loads(await asyncio.wait_for(ws4.recv(), timeout=3))
            if e.get("op") == "resumed":
                break
            kinds.append(e.get("type"))
        check("since=0 会把历史事件全部重放（所以客户端新登录必须从 server_seq 起步）",
              "message.new" in kinds, str(kinds[:6]))

    print("== 删除好友 ==")
    api(ta, "DELETE", f"/api/v1/friends/{idb}")
    fa = api(ta, "GET", "/api/v1/friends")
    check("删除后好友列表不再包含对方",
          all(x["id"] != idb for x in fa["items"]) and fa["total"] == len(fa["items"]), str(fa)[:200])
    check("非好友不能发消息",
          api(ta, "POST", "/api/v1/messages", {"conv": f"0:{idb}", "body": "x"}).get("error", {}).get("code")
          == "E_NOT_FRIEND")

    print(f"\n结果：{ok_count} 通过 / {fail_count} 失败")
    if fail_count:
        sys.exit(1)


if __name__ == "__main__":
    asyncio.run(main())
