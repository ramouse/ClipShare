"""有效期策略：四档有效期枚举与过期判定纯函数。

时间约定：全链路统一 naive UTC（应用与数据库容器均按 UTC 时钟运行），
禁止引入 aware datetime——naive/aware 混用比较会直接抛 TypeError。
"""
from datetime import datetime, timedelta
from enum import StrEnum


class Expiry(StrEnum):
    """分享有效期档位。

    value 为对外 API 使用的字面量：1h / 24h / 7d / forever。
    """

    ONE_HOUR = "1h"
    ONE_DAY = "24h"
    SEVEN_DAYS = "7d"
    FOREVER = "forever"


# 各有限档位对应的时长；FOREVER 不在此表（表示永久）
_DURATIONS: dict[Expiry, timedelta] = {
    Expiry.ONE_HOUR: timedelta(hours=1),
    Expiry.ONE_DAY: timedelta(days=1),
    Expiry.SEVEN_DAYS: timedelta(days=7),
}


def expires_at(expiry: Expiry, now: datetime) -> datetime | None:
    """计算到期时间；FOREVER 返回 None（表示永不过期）。"""
    if expiry is Expiry.FOREVER:
        return None
    return now + _DURATIONS[expiry]


def is_expired(expires_at: datetime | None, now: datetime) -> bool:
    """判定是否已过期：无到期时间（None）永不过期；now >= 到期时间视为已过期。"""
    return expires_at is not None and now >= expires_at
