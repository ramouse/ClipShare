"""Base62 短码生成：使用密码学安全随机源，用于不可猜测的分享链接。"""
import secrets

# Base62 字符表：数字 + 大小写字母，长度 62，避免 URL 中需要转义的字符
ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

# 短码冲突重试次数上限：唯一索引兜底 + 极低碰撞概率（62^6 空间），重试即可覆盖。
# 跨服务共享的契约常量（share_service / file_service 均引用），放 domain 统一出口。
SHORTCODE_MAX_RETRIES = 5


def generate_shortcode(length: int = 6) -> str:
    """生成指定长度的 Base62 随机短码。

    逐个字符使用 secrets.choice 取样，来源为操作系统级安全随机数，
    与自增 ID 相比不可枚举、不可猜测。
    """
    return "".join(secrets.choice(ALPHABET) for _ in range(length))
