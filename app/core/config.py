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


@lru_cache
def get_settings() -> Settings:
    """进程内单例配置。"""
    return Settings()
