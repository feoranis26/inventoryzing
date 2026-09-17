from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="IZ_", extra="ignore")

    database_url: str = "postgresql+psycopg://localhost/inventoryzing"
    secure_cookies: bool = True
    allowed_hosts: list[str] = ["localhost", "127.0.0.1", "testserver"]
    public_origin: str = "http://localhost:8088"
    static_dir: Path | None = None
    session_hours: int = 12
    enabled_modules: tuple[str, ...] = ("labeling", "scanning")
