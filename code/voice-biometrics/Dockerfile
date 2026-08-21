FROM python:3.12-slim-trixie AS builder
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /bin/
RUN apt-get update -y && apt-get install -y git build-essential

ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy
ARG max_workers=1
ENV MAX_WORKERS=$max_workers
ENV EMBEDDINGS_DIR=/app/embeddings
ENV GRPC_PORT=50051
ENV HTTP_HEALTH_PORT=8080
ENV SIMILARITY_THRESHOLD=0.25

# Configure the Python directory so it is consistent
ENV UV_PYTHON_INSTALL_DIR=/python

# Only use the managed Python version
ENV UV_PYTHON_PREFERENCE=only-managed

# Install Python before the project for caching
RUN uv python install 3.12

WORKDIR /app
RUN --mount=type=cache,target=/root/.cache/uv \
    --mount=type=bind,source=uv.lock,target=uv.lock \
    --mount=type=bind,source=pyproject.toml,target=pyproject.toml \
    uv sync --locked --no-install-project --no-dev
COPY pyproject.toml .
COPY uv.lock .
COPY build_protos.py .
COPY protos/ ./protos/
COPY models/ ./models/
COPY server.py .
RUN uv run build-protos
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --locked --no-dev

# Then, use a final image without uv
FROM debian:trixie-slim

# Setup a non-root user
RUN groupadd --system --gid 999 nonroot \
 && useradd --system --gid 999 --uid 999 --create-home nonroot

# Copy the Python version
COPY --from=builder --chown=python:python /python /python

# Copy the application from the builder
COPY --from=builder --chown=nonroot:nonroot /app /app

# Place executables in the environment at the front of the path
ENV PATH="/app/.venv/bin:$PATH"

# Use the non-root user to run our application
USER nonroot

# Use `/app` as the working directory
WORKDIR /app

# Create directories for data persistence
RUN mkdir -p /app/embeddings

# Expose gRPC and HTTP health ports
EXPOSE 50051
EXPOSE 8080

# Run the server
CMD ["python", "server.py"]
