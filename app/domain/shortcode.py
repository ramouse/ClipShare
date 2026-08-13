"""Base62 短码生成：使用密码学安全随机源，用于不可猜测的分享链接。"""
import secrets

# Base62 字符表：数字 + 大小写字母，长度 62，避免 URL 中需要转义的字符
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"


def generate_shortcode(length: int = 6) -> str:
    """生成指定长度的 Base62 随机短码。

    逐个字符使用 secrets.choice 取样，来源为操作系统级安全随机数，
    与自增 ID 相比不可枚举、不可猜测。
    """
    return "".join(secrets.choice(ALPHABET) for _ in range(length))
