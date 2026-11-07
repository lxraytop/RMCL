import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONFIG_PATH = Path(os.environ.get("FRIEND_CONFIG", ROOT / "config.json"))

DEFAULTS = {
    "db": "data/friend.db",
    "token_ttl_days": 30,
    "debug_code": False,
    "presence_grace_seconds": 30,
    "event_retention_days": 7,
    "tim": {
        "enabled": False,
        "sdkappid": 0,
        "key_file": "",
        "admin": "administrator",
        "group_type": "Private",
        "friendship_mirror": False,
    },
}


def _merge(base: dict, extra: dict) -> dict:
    out = dict(base)
    for k, v in (extra or {}).items():
        if isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = _merge(out[k], v)
        else:
            out[k] = v
    return out


def load() -> dict:
    data = {}
    if CONFIG_PATH.is_file():
        data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    return _merge(DEFAULTS, data)


CONFIG = load()

DB_PATH = (ROOT / CONFIG["db"]).resolve() if not os.path.isabs(CONFIG["db"]) else Path(CONFIG["db"])
TIM_KEY_FILE = (
    (ROOT / CONFIG["tim"]["key_file"]).resolve()
    if CONFIG["tim"]["key_file"] and not os.path.isabs(CONFIG["tim"]["key_file"])
    else Path(CONFIG["tim"]["key_file"] or "")
)


def describe() -> dict:
    """健康检查用：告诉运维进程实际读的是哪个配置文件。"""
    return {
        "path": str(CONFIG_PATH),
        "exists": CONFIG_PATH.is_file(),
        "debug_code": bool(CONFIG.get("debug_code")),
        "tim_enabled": bool(CONFIG.get("tim", {}).get("enabled")),
        "tim_key": str(TIM_KEY_FILE),
        "tim_key_exists": bool(TIM_KEY_FILE and TIM_KEY_FILE.is_file()),
        "smtp_file": (ROOT / "config.smtp.json").is_file(),
    }
