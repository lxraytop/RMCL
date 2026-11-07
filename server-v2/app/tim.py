"""腾讯云 IM REST 客户端。

设计原则：
1. 密钥只在服务端；客户端只拿 UserSig。
2. 所有调用失败只记日志，不向上抛 —— 本地数据库才是事实来源，TIM 只是投递通道。
3. 关系链接口（v4/sns/*）默认关闭，等套餐支持再开；接口名集中在这里，改动只需一处。
"""

import base64
import hashlib
import hmac
import json
import logging
import random
import time
import urllib.parse
import zlib

import httpx

from .config import CONFIG, TIM_KEY_FILE
from .security import now

log = logging.getLogger("friend.tim")

BASE = "https://console.tim.qq.com/v4"


def _tim() -> dict:
    return CONFIG.get("tim", {})


def api_key() -> str:
    key = (_tim().get("key") or "").strip()
    if key:
        return key
    try:
        if TIM_KEY_FILE.is_file():
            return TIM_KEY_FILE.read_text(encoding="utf-8").strip()
    except Exception:
        pass
    return ""


def enabled() -> bool:
    t = _tim()
    return bool(t.get("enabled")) and int(t.get("sdkappid") or 0) > 0 and bool(api_key())


def user_sig(identifier: str, expire: int = 604800) -> str:
    """TLS 2.0 UserSig。URL 安全 Base64 的替换表是 + => * / / => - / = => _。"""
    sdkappid = int(_tim().get("sdkappid") or 0)
    ts = int(time.time())
    raw = (
        f"TLS.identifier:{identifier}\n"
        f"TLS.sdkappid:{sdkappid}\n"
        f"TLS.time:{ts}\n"
        f"TLS.expire:{expire}\n"
    )
    sig = base64.b64encode(
        hmac.new(api_key().encode(), raw.encode(), hashlib.sha256).digest()
    ).decode()
    payload = {
        "TLS.ver": "2.0",
        "TLS.identifier": identifier,
        "TLS.sdkappid": sdkappid,
        "TLS.expire": expire,
        "TLS.time": ts,
        "TLS.sig": sig,
    }
    compressed = zlib.compress(json.dumps(payload, separators=(",", ":")).encode())
    return (
        base64.b64encode(compressed)
        .decode()
        .replace("+", "*")
        .replace("/", "-")
        .replace("=", "_")
    )


async def _call(service: str, command: str, body: dict, identifier: str | None = None) -> dict | None:
    if not enabled():
        return None
    ident = identifier or (_tim().get("admin") or "administrator")
    query = urllib.parse.urlencode(
        {
            "sdkappid": int(_tim().get("sdkappid") or 0),
            "identifier": ident,
            "usersig": user_sig(ident),
            "random": random.randint(0, 2**31 - 1),
            "contenttype": "json",
        }
    )
    url = f"{BASE}/{service}/{command}?{query}"
    try:
        async with httpx.AsyncClient(timeout=8) as client:
            resp = await client.post(url, json=body)
            data = resp.json()
            if data.get("ActionStatus") != "OK" or int(data.get("ErrorCode") or 0) != 0:
                log.warning("TIM %s/%s 失败: %s", service, command, data)
            return data
    except Exception as exc:  # 网络问题不影响本地流程
        log.warning("TIM %s/%s 异常: %s", service, command, exc)
        return None


async def account_import(user_id: int, nickname: str, avatar: str = "") -> None:
    await _call(
        "im_open_login_svc",
        "account_import",
        {"UserID": str(user_id), "Nick": nickname, "FaceUrl": avatar},
    )


async def send_c2c(from_id: int, to_id: int, body: str) -> str:
    data = await _call(
        "openim",
        "sendmsg",
        {
            "SyncOtherMachine": 2,
            "From_Account": str(from_id),
            "To_Account": str(to_id),
            "MsgLifeTime": 604800,
            "MsgRandom": random.randint(0, 2**31 - 1),
            "MsgBody": [{"MsgType": "TIMTextElem", "MsgContent": {"Text": body}}],
        },
    )
    return str((data or {}).get("MsgId") or "")


async def send_group(group_id: int, from_id: int, body: str) -> str:
    data = await _call(
        "group_open_http_svc",
        "send_group_msg",
        {
            "GroupId": str(group_id),
            "From_Account": str(from_id),
            "Random": random.randint(0, 2**31 - 1),
            "MsgBody": [{"MsgType": "TIMTextElem", "MsgContent": {"Text": body}}],
        },
    )
    return str((data or {}).get("MsgId") or "")


async def create_group(name: str, owner_id: int, members: list[int]) -> str:
    member_list = [{"Member_Account": str(owner_id)}]
    for uid in members:
        if uid != owner_id:
            member_list.append({"Member_Account": str(uid)})
    data = await _call(
        "group_open_http_svc",
        "create_group",
        {
            "Owner_Account": str(owner_id),
            "Type": _tim().get("group_type") or "Private",
            "Name": name[:30],
            "MemberList": member_list,
        },
    )
    return str((data or {}).get("GroupId") or "")


async def add_group_members(group_id: int, members: list[int]) -> None:
    if not members:
        return
    await _call(
        "group_open_http_svc",
        "add_group_member",
        {"GroupId": str(group_id), "MemberList": [{"Member_Account": str(m)} for m in members]},
    )


async def remove_group_member(group_id: int, user_id: int) -> None:
    await _call(
        "group_open_http_svc",
        "delete_group_member",
        {"GroupId": str(group_id), "MemberToDel_Account": [str(user_id)]},
    )


async def destroy_group(group_id: int) -> None:
    await _call("group_open_http_svc", "destroy_group", {"GroupId": str(group_id)})


async def friend_import(user_id: int, friends: list[dict]) -> None:
    """关系链镜像（可选）。friends: [{To_Account, Remark, AddSource}]"""
    if not _tim().get("friendship_mirror") or not friends:
        return
    await _call(
        "sns",
        "friend_import",
        {"From_Account": str(user_id), "AddFriendItem": friends},
    )


async def friend_delete(user_id: int, friend_ids: list[int]) -> None:
    if not _tim().get("friendship_mirror") or not friend_ids:
        return
    await _call(
        "sns",
        "friend_delete",
        {"From_Account": str(user_id), "To_Account": [str(x) for x in friend_ids], "DeleteType": "Delete_Type_Both"},
    )


def self_check() -> dict:
    return {
        "enabled": enabled(),
        "sdkappid": int(_tim().get("sdkappid") or 0),
        "has_key": bool(api_key()),
        "friendship_mirror": bool(_tim().get("friendship_mirror")),
        "now": now(),
    }
