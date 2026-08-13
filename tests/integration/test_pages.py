"""M4 页面集成测试：页面 shell + 静态资源 + 安全头 + 端到端冒烟。

前端 JS 行为（类型识别、XSS 消毒、错误页渲染）由代码审查 + 宿主机
``node --check`` 语法校验保证；本文件覆盖服务端契约：
- 页面路由对任意短码都返回 200 shell（不读数据库、不消耗访问次数）；
- /static 下 vendor 与自研资源均可访问（运行时零 CDN 依赖）；
- 页面响应携带完整安全头（含 CSP）；
- 端到端冒烟：POST 创建 → 提取 code → 页面 shell 可用，404/410 场景
  断言「页面 shell 200 + API 错误 type 正确」。
"""
import re
from collections.abc import Iterator
from datetime import timedelta

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text

from app.core.time import utcnow
from app.db.base import Base
from app.db.repository import ShareRepository
from app.db.session import SessionLocal, engine
from app.main import app

API_PREFIX = "/api/v1"
# vendor 与自研静态资源清单（vendor 版本锁定在 app/static/vendor/README.md）
STATIC_FILES = [
    "vendor/bootstrap.min.css",
    "vendor/bootstrap.bundle.min.js",
    "vendor/marked.min.js",
    "vendor/highlight.min.js",
    "vendor/highlight-github.min.css",
    "vendor/dompurify.min.js",
    "css/style.css",
    "js/app.js",
    "js/view.js",
]


@pytest.fixture(scope="session", autouse=True)
def _ensure_tables() -> Iterator[None]:
    """会话级幂等建表（与 test_shares_api.py 保持一致）。"""
    Base.metadata.create_all(engine)
    yield


@pytest.fixture(autouse=True)
def _clean_shares() -> Iterator[None]:
    """每个用例结束后清空 shares 表。"""
    yield
    with engine.begin() as conn:
        conn.execute(text("DELETE FROM shares"))


@pytest.fixture()
def client() -> Iterator[TestClient]:
    """页面测试客户端：复用生产应用实例（含安全头中间件与异常处理器）。"""
    with TestClient(app) as test_client:
        yield test_client


def _create_share(client: TestClient, content: str, **overrides: object) -> dict:
    """通过 API 创建分享并断言成功，返回响应体。"""
    payload: dict[str, object] = {"content": content, **overrides}
    response = client.post(f"{API_PREFIX}/shares", json=payload)
    assert response.status_code == 201, response.text
    return response.json()


def _assert_html(response: object) -> None:
    """断言响应为 UTF-8 HTML。"""
    assert response.status_code == 200
    assert response.headers["content-type"].startswith("text/html; charset=utf-8")


def test_index_page_renders_form(client: TestClient) -> None:
    """创建页：200 + 关键表单元素（内容输入、有效期/次数选择、提交按钮、结果区）。"""
    response = client.get("/")
    _assert_html(response)
    html = response.text
    assert 'id="create-form"' in html
    assert 'id="content"' in html
    assert 'name="expiry"' in html
    assert 'name="max_views"' in html
    assert 'id="create-result"' in html
    assert 'id="qr-img"' in html
    assert 'id="copy-btn"' in html
    # 响应式元信息（手机 + 电脑浏览器可用）
    assert 'name="viewport"' in html
    assert 'lang="zh-CN"' in html


def test_view_page_shell_for_any_code(client: TestClient) -> None:
    """查看页：任意短码均返回 200 shell，含内容容器、模式切换、元信息容器与 data-code。"""
    response = client.get("/s/abcdef")
    _assert_html(response)
    html = response.text
    assert 'id="view-root"' in html
    assert 'data-code="abcdef"' in html
    assert 'id="view-content"' in html
    assert 'id="view-error"' in html
    assert 'data-mode="text"' in html
    assert 'data-mode="markdown"' in html
    assert 'data-mode="code"' in html
    assert 'id="meta-expires"' in html
    assert 'id="meta-views"' in html


def test_view_page_shell_does_not_read_db(client: TestClient) -> None:
    """架构红线：查看页 shell 不读数据库——已创建的分享内容绝不出现在页面 HTML 里。"""
    marker = "SECRET_MARKER_9f3a"
    created = _create_share(client, content=marker)
    response = client.get(f"/s/{created['code']}")
    _assert_html(response)
    assert marker not in response.text
    # 页面 URL 上同样不出现内容（内容只由前端 JS 调 API 获取）
    assert str(response.request.url).endswith(f"/s/{created['code']}")


def test_pages_have_no_inline_scripts(client: TestClient) -> None:
    """CSP script-src 'self' 的配套约定：页面所有 <script> 必须带 src，禁内联脚本与事件处理器。"""
    for path in ("/", "/s/abc123"):
        html = client.get(path).text
        for tag in re.findall(r"<script\b[^>]*>", html):
            assert "src=" in tag, f"{path} 存在内联脚本: {tag}"
        assert not re.search(r"\s(onclick|onerror|onload|onmouseover)\s*=", html, re.I), (
            f"{path} 存在内联事件处理器"
        )


def test_static_vendor_assets_served(client: TestClient) -> None:
    """静态资源：vendor 库与自研 JS/CSS 全部可访问且非空（运行时零 CDN 依赖）。"""
    for path in STATIC_FILES:
        response = client.get(f"/static/{path}")
        assert response.status_code == 200, f"/static/{path} 返回 {response.status_code}"
        assert len(response.content) > 0, f"/static/{path} 内容为空"


def test_vendor_versions_pinned(client: TestClient) -> None:
    """vendor 版本锁定：文件头注释标记与 vendor/README.md 记录的版本一致。"""
    pairs = [
        ("vendor/bootstrap.min.css", "Bootstrap  v5.3.3"),
        ("vendor/bootstrap.bundle.min.js", "Bootstrap v5.3.3"),
        ("vendor/marked.min.js", "marked v12.0.2"),
        ("vendor/highlight.min.js", "Highlight.js v11.9.0"),
        ("vendor/dompurify.min.js", "DOMPurify 3.1.5"),
    ]
    for path, marker in pairs:
        content = client.get(f"/static/{path}").text
        assert marker in content, f"{path} 缺少版本标记 {marker!r}"


def test_page_security_headers(client: TestClient) -> None:
    """安全头：页面响应携带 nosniff / DENY / no-referrer / CSP（含 default-src 'self'）。"""
    response = client.get("/")
    assert response.headers["x-content-type-options"] == "nosniff"
    assert response.headers["x-frame-options"] == "DENY"
    assert response.headers["referrer-policy"] == "no-referrer"
    csp = response.headers["content-security-policy"]
    assert "default-src 'self'" in csp
    assert "script-src 'self'" in csp
    assert "img-src 'self' data:" in csp
    assert "frame-ancestors 'none'" in csp


def test_e2e_smoke_create_then_view_page(client: TestClient) -> None:
    """端到端冒烟（模拟前端）：POST 创建 → 提取 code → 页面 shell 与二维码均可用。"""
    created = _create_share(client, content="端到端冒烟", expiry="1h", max_views=5)
    code = created["code"]

    page = client.get(f"/s/{code}")
    _assert_html(page)
    assert f'data-code="{code}"' in page.text

    qr = client.get(f"{API_PREFIX}/shares/{code}/qr")
    assert qr.status_code == 200
    assert qr.headers["content-type"] == "image/png"


def test_page_load_does_not_consume_views(client: TestClient) -> None:
    """访问计数只经 API 一次：打开页面 shell 不消耗次数，只有调用读取接口才消耗。"""
    created = _create_share(client, content="计数验证", max_views=5)
    code = created["code"]

    first = client.get(f"{API_PREFIX}/shares/{code}")
    assert first.json()["remaining_views"] == 4

    # 打开页面两次（模拟浏览器查看），不触发任何读取接口
    for _ in range(2):
        page = client.get(f"/s/{code}")
        assert page.status_code == 200

    second = client.get(f"{API_PREFIX}/shares/{code}")
    assert second.json()["remaining_views"] == 3  # 只被第二次 API 读取消耗一次


def test_unknown_code_page_shell_and_api_404(client: TestClient) -> None:
    """未知短码：页面 shell 仍 200（错误在 JS 层展示），API 返回 share_not_found。"""
    page = client.get("/s/zzzz99")
    _assert_html(page)
    assert 'id="view-root"' in page.text

    api = client.get(f"{API_PREFIX}/shares/zzzz99")
    assert api.status_code == 404
    assert api.json()["type"] == "share_not_found"


def test_expired_share_page_shell_and_api_410(client: TestClient) -> None:
    """过期分享：页面 shell 仍 200，API 返回 share_expired。"""
    with SessionLocal() as session:
        ShareRepository.create(
            session,
            code="exppge",
            content="过期内容",
            expires_at=utcnow() - timedelta(hours=1),
            max_views=None,
        )
        session.commit()

    page = client.get("/s/exppge")
    _assert_html(page)

    api = client.get(f"{API_PREFIX}/shares/exppge")
    assert api.status_code == 410
    assert api.json()["type"] == "share_expired"


def test_exhausted_share_page_shell_and_api_410(client: TestClient) -> None:
    """次数耗尽：页面 shell 仍 200，API 第二次读取返回 share_views_exhausted。"""
    created = _create_share(client, content="一次性", max_views=1)
    code = created["code"]

    first = client.get(f"{API_PREFIX}/shares/{code}")
    assert first.status_code == 200

    page = client.get(f"/s/{code}")
    _assert_html(page)

    second = client.get(f"{API_PREFIX}/shares/{code}")
    assert second.status_code == 410
    assert second.json()["type"] == "share_views_exhausted"
