"""应用配置：从环境变量 / .env 文件加载。"""
from functools import lru_cache

from pydantic import ValidationInfo, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

from app.domain.filename import split_extensions


class Settings(BaseSettings):
    """全部配置项均有默认值，生产环境通过环境变量覆盖。"""

    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    app_name: str = "ClipShare"
    environment: str = "development"
    database_url: str = "postgresql+psycopg://clipshare:clipshare@localhost:5432/clipshare"
    log_level: str = "INFO"
    # 分享网页公网地址：与 /s/{code} 拼接形成分享链接（部署时覆盖为真实域名）
    public_base_url: str = "http://localhost:8000"
    # 单条分享内容长度上限（字符数）
    share_max_content_length: int = 100000
    # 速率限制（slowapi 语法：次数/单位）：创建与读取按 IP 限额
    rate_limit_create: str = "30/minute"
    rate_limit_read: str = "60/minute"

    # ---- v0.2 文件分享：大小上限与存储 ----
    # 单文件上限 100MB（nginx client_max_body_size 配套 110m）
    file_max_size: int = 100 * 1024 * 1024
    # E2E 加密上限 10MB：浏览器全内存加密，超过必须明文流式直传
    file_encrypt_max_size: int = 10 * 1024 * 1024
    # 预览内容截断上限 200KB（预览只需文件头部）
    file_preview_max_size: int = 200 * 1024
    # 文件落盘目录（相对项目根；磁盘名 secrets 生成，无扩展名）
    file_storage_dir: str = "data/files"
    # 允许上传的扩展名白名单（逗号分隔，小写；解析结果见 file_allowed_extensions_set）
    file_allowed_extensions: str = (
        "txt,md,json,py,js,ts,jsx,tsx,html,css,scss,java,c,cpp,h,go,rs,php,rb,sh,"
        "yaml,yml,toml,ini,cfg,conf,log,csv,tsv,xml,sql,"
        "pdf,doc,docx,xls,xlsx,ppt,pptx,rtf,odt,ods,odp,"
        "png,jpg,jpeg,gif,webp,bmp,ico,svg,avif,"
        "zip,rar,7z,tar,gz,bz2,xz,"
        "mp3,wav,ogg,flac,mp4,webm,mov,mkv"
    )
    # 允许文本预览的扩展名（浏览器内直接渲染头部内容；解析结果见 file_preview_extensions_set）
    file_preview_extensions: str = (
        "txt,md,json,py,js,ts,jsx,tsx,html,css,java,c,cpp,h,go,rs,php,rb,sh,"
        "yaml,yml,toml,ini,cfg,conf,log,csv,tsv,xml,sql"
    )
    # 解析后的扩展名集合（由上方字符串派生，供文件类型校验使用）
    file_allowed_extensions_set: set[str] = set()
    file_preview_extensions_set: set[str] = set()
    # 文件相关限流（独立 key，与文本分享预算互不影响）
    rate_limit_upload: str = "30/minute"
    rate_limit_file_read: str = "60/minute"
    rate_limit_file_download: str = "60/minute"

    @field_validator("file_allowed_extensions_set", mode="before")
    @classmethod
    def _parse_allowed_extensions(cls, value: object, info: ValidationInfo) -> set[str]:
        """把 file_allowed_extensions 字符串解析为扩展名集合（小写、去前导点）。"""
        return split_extensions(str(info.data.get("file_allowed_extensions", "")))

    @field_validator("file_preview_extensions_set", mode="before")
    @classmethod
    def _parse_preview_extensions(cls, value: object, info: ValidationInfo) -> set[str]:
        """把 file_preview_extensions 字符串解析为扩展名集合（小写、去前导点）。"""
        return split_extensions(str(info.data.get("file_preview_extensions", "")))


@lru_cache
def get_settings() -> Settings:
    """进程内单例配置。"""
    return Settings()
