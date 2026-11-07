"""运维脚本：初始化数据库、建邀请码、建测试账号。

用法：
  python manage.py init
  python manage.py invite 5 1          # 生成 5 个、每个可用 1 次
  python manage.py user a@b.com 123456 测试用户
"""

import sys

from app.config import CONFIG
from app.db import init_db, one, tx
from app.security import hash_password, new_token, now


def cmd_init() -> None:
    init_db()
    print("数据库已初始化：", CONFIG["db"])


def cmd_invite(count: int, uses: int) -> None:
    import secrets

    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    with tx() as conn:
        for _ in range(count):
            code = "RMCL-" + "".join(secrets.choice(alphabet) for _ in range(4)) + "-" + "".join(
                secrets.choice(alphabet) for _ in range(4)
            )
            conn.execute(
                "INSERT INTO invite (code, max_uses, used, created_at) VALUES (?, ?, 0, ?)",
                (code, uses, now()),
            )
            print(code)


def cmd_user(email: str, password: str, nickname: str) -> None:
    with tx() as conn:
        if one(conn, "SELECT 1 FROM user WHERE email = ?", (email,)):
            print("邮箱已存在")
            return
        conn.execute(
            "INSERT INTO user (email, nickname, pass_hash, created_at) VALUES (?, ?, ?, ?)",
            (email, nickname, hash_password(password), now()),
        )
        uid = int(conn.execute("SELECT last_insert_rowid() AS id").fetchone()["id"])
        token = new_token()
        conn.execute(
            "INSERT INTO auth_token (token, user_id, device, created_at, expires_at) VALUES (?, ?, 'cli', ?, ?)",
            (token, uid, now(), now() + 86400 * 30),
        )
    print("user_id:", uid)
    print("token:", token)


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        raise SystemExit(1)
    if args[0] == "init":
        cmd_init()
    elif args[0] == "invite":
        cmd_invite(int(args[1]) if len(args) > 1 else 1, int(args[2]) if len(args) > 2 else 1)
    elif args[0] == "user":
        cmd_user(args[1], args[2], args[3] if len(args) > 3 else args[1].split("@")[0])
    else:
        print(__doc__)
