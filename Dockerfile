# dev 与 prod 共用同一镜像：INSTALL_DEV=true 时附带测试/代码检查工具
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# 固定 uid 1000：与生产宿主目录挂载（./data/files 归 uid 1000）对齐，容器内外权限一致
RUN groupadd --system app && useradd --system --uid 1000 --gid app --home-dir /app app

WORKDIR /app

COPY pyproject.toml README.md ./
COPY app ./app
COPY cli ./cli
# 注：tests 不入镜像——dev 环境靠 compose 源码热挂载运行 pytest；
# 生产镜像按最小攻击面原则不含测试代码（.dockerignore 同步排除 tests）
# M6：alembic 迁移必需文件必须打进镜像——生产容器无源码挂载，
# deploy.sh 在容器内执行 alembic upgrade head（dev 环境靠热挂载拿到这些文件，prod 靠 COPY）
COPY alembic.ini ./
COPY migrations ./migrations

# 国内网络默认走清华 PyPI 镜像加速；如需官方源：docker compose build --build-arg PIP_INDEX_URL=https://pypi.org/simple
ARG PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple
ARG INSTALL_DEV=false
# dev 镜像用可编辑安装（-e）：与热挂载源码实时同步；prod 镜像用常规安装（源码快照）
RUN pip install --upgrade pip -i "$PIP_INDEX_URL" \
    && if [ "$INSTALL_DEV" = "true" ]; then pip install -e ".[dev]" -i "$PIP_INDEX_URL"; else pip install . -i "$PIP_INDEX_URL"; fi \
    && chown -R app:app /app

# 文件分享落盘目录：生产卷挂载 ./data/files:/app/data/files 的容器内落点，
# 提前建好并归属 app 用户，保证挂载后应用可写（dev 靠热挂载同样生效）
RUN mkdir -p /app/data/files && chown -R app:app /app

USER app

EXPOSE 8000

HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
  CMD ["python", "-c", "import urllib.request, sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/healthz', timeout=3).status == 200 else 1)"]

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
