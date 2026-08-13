"""页面路由：渲染页面 shell（Jinja2），不读数据库。

架构约定（M4）：
- 页面只渲染 HTML shell，查看页的实际内容由前端 JS 调用
  ``GET /api/v1/shares/{code}`` 获取——保证「访问计数只经 API 一次」：
  打开页面本身不消耗次数，只有真正调用读取接口才计数；
- ``GET /s/{code}`` 对任意短码都返回同一查看页 shell（不校验存在性），
  404 / 410 等错误由前端 JS 按 API 错误体的 ``type`` 字段渲染友好错误页；
- 因此页面路由完全不触碰数据库，也不受慢 API 限流影响；
- ``include_in_schema=False``：页面是 HTML 而非 REST 资源，不进入 OpenAPI 文档，
  OpenAPI 只描述 /api/v1 的机器接口。
"""
from pathlib import Path

from fastapi import APIRouter, Request
from fastapi.templating import Jinja2Templates
from starlette.responses import HTMLResponse

# 模板目录固定为 app/templates：用相对于本文件的路径解析，
# 不依赖进程工作目录（容器内 uvicorn 与测试的 cwd 可能不同）
TEMPLATES_DIR = Path(__file__).resolve().parent.parent.parent / "templates"

templates = Jinja2Templates(directory=str(TEMPLATES_DIR))

router = APIRouter(tags=["pages"])


@router.get("/", response_class=HTMLResponse, summary="创建分享页", include_in_schema=False)
async def index(request: Request) -> HTMLResponse:
    """创建分享页面 shell：表单在前端提交到 POST /api/v1/shares。"""
    return templates.TemplateResponse(
        request=request,
        name="index.html",
        context={"page_title": "ClipShare — 创建分享"},
    )


@router.get(
    "/s/{code}",
    response_class=HTMLResponse,
    summary="查看分享页",
    include_in_schema=False,
)
async def view(request: Request, code: str) -> HTMLResponse:
    """查看分享页面 shell：任意短码均返回 shell，内容与错误由前端 JS 调 API 获取。

    code 仅注入模板（Jinja2 自动转义，防注入），页面渲染不校验、不查询，
    保证访问计数只经 API 一次。
    """
    return templates.TemplateResponse(
        request=request,
        name="view.html",
        context={"page_title": "ClipShare — 查看分享", "code": code},
    )
