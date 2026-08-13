"""健康检查路由。"""
from fastapi import APIRouter

router = APIRouter(tags=["health"])


@router.get(
    "/healthz",
    summary="存活检查",
    description="返回服务存活状态，供容器健康检查与监控系统使用。",
)
async def healthz() -> dict[str, str]:
    return {"status": "ok"}
