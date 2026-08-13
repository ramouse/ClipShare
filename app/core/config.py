"""应用配置：从环境变量 / .env 文件加载。"""
from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


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


@lru_cache
def get_settings() -> Settings:
    """进程内单例配置。"""
    return Settings()
