"""邮件投递（SMTP）。

配置放在 config.json 的 smtp 段，或单独放 config.smtp.json（不进版本库）：
{
  "host": "smtp.qq.com", "port": 465, "encryption": "ssl",
  "user": "xxx@foxmail.com", "pass": "授权码",
  "from": "xxx@foxmail.com", "from_name": "RaySkin"
}
"""

import json
import logging
import smtplib
import ssl
from email.header import Header
from email.mime.text import MIMEText
from email.utils import formataddr, formatdate, make_msgid

from .config import CONFIG, ROOT

log = logging.getLogger("friend.mail")

_EXTRA = ROOT / "config.smtp.json"


def smtp_config() -> dict | None:
    conf = CONFIG.get("smtp") or {}
    if _EXTRA.is_file():
        try:
            conf = {**conf, **json.loads(_EXTRA.read_text(encoding="utf-8"))}
        except Exception as exc:
            log.warning("读取 config.smtp.json 失败: %s", exc)
    if not conf.get("host") or not conf.get("from"):
        return None
    return conf


def enabled() -> bool:
    return smtp_config() is not None


def _send_sync(to: str, subject: str, body: str) -> bool:
    conf = smtp_config()
    if conf is None:
        return False
    host = str(conf["host"])
    port = int(conf.get("port") or 465)
    encryption = str(conf.get("encryption") or "ssl").lower()
    timeout = int(conf.get("timeout") or 15)
    from_addr = str(conf["from"])
    from_name = str(conf.get("from_name") or "RMCL")

    msg = MIMEText(body, "plain", "utf-8")
    msg["Subject"] = Header(subject, "utf-8")
    msg["From"] = formataddr((str(Header(from_name, "utf-8")), from_addr))
    msg["To"] = to
    msg["Date"] = formatdate(localtime=True)
    msg["Message-ID"] = make_msgid(domain=host)

    try:
        if encryption == "ssl":
            client = smtplib.SMTP_SSL(host, port, timeout=timeout, context=ssl.create_default_context())
        else:
            client = smtplib.SMTP(host, port, timeout=timeout)
        with client:
            client.ehlo()
            if encryption == "tls":
                client.starttls(context=ssl.create_default_context())
                client.ehlo()
            if conf.get("user"):
                client.login(str(conf["user"]), str(conf.get("pass") or ""))
            client.sendmail(from_addr, [to], msg.as_string())
        return True
    except Exception as exc:
        log.warning("SMTP 发送失败 %s: %s", to, exc)
        return False


async def send(to: str, subject: str, body: str) -> bool:
    """异步包装：smtplib 是阻塞的，放到线程里跑，别卡住事件循环。"""
    import asyncio

    return await asyncio.to_thread(_send_sync, to, subject, body)


def self_check() -> dict:
    conf = smtp_config()
    return {
        "enabled": conf is not None,
        "host": (conf or {}).get("host", ""),
        "from": (conf or {}).get("from", ""),
    }
