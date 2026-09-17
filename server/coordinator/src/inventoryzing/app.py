import json
import logging
import time
from contextlib import asynccontextmanager
from uuid import uuid4

from fastapi import FastAPI, Request, Response
from fastapi.middleware.trustedhost import TrustedHostMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from sqlalchemy import create_engine, text
from sqlalchemy.exc import SQLAlchemyError
from starlette.exceptions import HTTPException as StarletteHTTPException

from inventoryzing.commands import CommandError
from inventoryzing.config import Settings
from inventoryzing.modules import register_enabled_modules
from inventoryzing.properties import register as register_properties
from inventoryzing.routes import router


class SPAFiles(StaticFiles):
    async def get_response(self, path, scope):
        if path.split("/", 1)[0] in {"api", "health"}:
            raise StarletteHTTPException(404)
        try:
            return await super().get_response(path, scope)
        except StarletteHTTPException as error:
            if error.status_code != 404 or "." in path.rsplit("/", 1)[-1]:
                raise
            return await super().get_response("index.html", scope)


def create_app(settings: Settings | None = None) -> FastAPI:
    settings = settings or Settings()
    engine = create_engine(
        settings.database_url,
        pool_pre_ping=True,
        isolation_level="READ COMMITTED",
        connect_args={"connect_timeout": 5},
    )

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        yield
        engine.dispose()

    app = FastAPI(title="inventoryzing", version="0.1.0", lifespan=lifespan)
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=settings.allowed_hosts)
    app.state.settings = settings
    app.state.engine = engine
    app.include_router(router)
    register_properties(app)
    register_enabled_modules(app, settings.enabled_modules)

    @app.middleware("http")
    async def request_boundary(request: Request, call_next):
        request_id = str(uuid4())
        started = time.monotonic()
        if (
            request.method not in {"GET", "HEAD", "OPTIONS"}
            and not request.url.path.startswith("/api/agent/")
            and request.headers.get("origin") != settings.public_origin
        ):
            return JSONResponse({"detail": "Request origin is not permitted."}, status_code=403)
        response = await call_next(request)
        response.headers["X-Request-ID"] = request_id
        response.headers["X-Content-Type-Options"] = "nosniff"
        response.headers["X-Frame-Options"] = "DENY"
        response.headers["Referrer-Policy"] = "same-origin"
        response.headers["Content-Security-Policy"] = (
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
            "img-src 'self' data:; font-src 'self'; connect-src 'self'; "
            "frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
        )
        if request.url.path.startswith("/api/"):
            response.headers["Cache-Control"] = "no-store"
        logging.getLogger("inventoryzing.requests").info(
            json.dumps(
                {
                    "request_id": request_id,
                    "method": request.method,
                    "status": response.status_code,
                    "duration_ms": round((time.monotonic() - started) * 1000),
                }
            )
        )
        return response

    @app.exception_handler(CommandError)
    async def command_error(request: Request, error: CommandError):
        return JSONResponse({"code": error.code, "detail": error.detail}, status_code=error.status)

    @app.get("/health/live")
    def live() -> dict[str, str]:
        return {"status": "ok"}

    @app.get("/health/ready")
    def ready(response: Response) -> dict[str, str]:
        try:
            with engine.connect() as connection:
                connection.execute(text("SELECT epoch FROM iz.command_epochs LIMIT 1"))
        except SQLAlchemyError:
            response.status_code = 503
            return {"status": "unavailable"}
        return {"status": "ready"}

    if settings.static_dir is not None:
        app.mount("/", SPAFiles(directory=settings.static_dir, html=True), name="ui")
    return app
