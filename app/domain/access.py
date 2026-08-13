"""访问次数策略：剩余次数计算与耗尽判定纯函数。"""


def remaining_views(max_views: int | None, view_count: int) -> int | None:
    """计算剩余可访问次数；max_views 为 None（不限次）返回 None，已超限返回 0。"""
    if max_views is None:
        return None
    return max(0, max_views - view_count)


def is_exhausted(max_views: int | None, view_count: int) -> bool:
    """判定是否耗尽：不限次（None）永不耗尽；view_count >= max_views 视为耗尽。"""
    return max_views is not None and view_count >= max_views
