FROM node:24-alpine AS frontend
WORKDIR /app/web/main-ui
COPY web/main-ui/package.json web/main-ui/package-lock.json ./
RUN npm ci
COPY web/main-ui/ ./
COPY assets/ /app/assets/
RUN npm run build

FROM ghcr.io/astral-sh/uv:0.10.12 AS uv
FROM python:3.13.12-slim AS runtime
COPY --from=uv /uv /usr/local/bin/uv
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 UV_LINK_MODE=copy
WORKDIR /app
COPY server/coordinator/ /app/server/coordinator/
RUN uv sync --project server/coordinator --frozen --no-dev --no-editable
COPY db/ /app/db/
COPY assets/ /app/assets/
COPY --from=frontend /app/web/main-ui/dist /app/static
RUN useradd --system --uid 10001 --create-home inventoryzing
ENV PATH="/app/server/coordinator/.venv/bin:$PATH" IZ_STATIC_DIR=/app/static
USER inventoryzing
EXPOSE 8088
CMD ["uvicorn", "inventoryzing.app:create_app", "--factory", "--host", "0.0.0.0", "--port", "8088", "--no-access-log"]