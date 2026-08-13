# dev 与 prod 共用同一镜像：INSTALL_DEV=true 时附带测试/代码检查工具
FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

RUN groupadd --system app && useradd --system --gid app --home-dir /app app

WORKDIR /app

COPY pyproject.toml README.md ./
COPY app ./app
COPY cli ./cli
COPY tests ./tests

# 国内网络默认走清华 PyPI 镜像加速；如需官方源：docker compose build --build-arg PIP_INDEX_URL=https://pypi.org/simple
ARG PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple
ARG INSTALL_DEV=false
# dev 镜像用可编辑安装（-e）：与热挂载源码实时同步；prod 镜像用常规安装（源码快照）
RUN pip install --upgrade pip -i "$PIP_INDEX_URL" \
    && if [ "$INSTALL_DEV" = "true" ]; then pip install -e ".[dev]" -i "$PIP_INDEX_URL"; else pip install . -i "$PIP_INDEX_URL"; fi \
    && chown -R app:app /app

USER app

EXPOSE 8000

HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
  CMD ["python", "-c", "import urllib.request, sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/healthz', timeout=3).status == 200 else 1)"]

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
