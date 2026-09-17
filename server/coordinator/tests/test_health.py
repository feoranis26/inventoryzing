from fastapi.testclient import TestClient

from inventoryzing.app import create_app
from inventoryzing.config import Settings


def test_liveness_does_not_depend_on_database():
    app = create_app(Settings(database_url="postgresql+psycopg://localhost:1/unavailable"))
    with TestClient(app) as client:
        response = client.get("/health/live")
        assert response.status_code == 200
        assert response.json() == {"status": "ok"}


def test_untrusted_host_is_rejected():
    with TestClient(create_app()) as client:
        assert client.get("/health/live", headers={"host": "untrusted.invalid"}).status_code == 400


def test_core_starts_without_labeling_module():
    app = create_app(
        Settings(
            database_url="postgresql+psycopg://localhost:1/unavailable",
            enabled_modules=(),
        )
    )
    with TestClient(app) as client:
        assert client.get("/health/live").status_code == 200
        assert client.get("/api/objects/not-an-id/tag.png").status_code == 404
