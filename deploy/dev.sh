#!/usr/bin/env bash
# Local build and deploy workflow for WebLens.
#
#   deploy/dev.sh            build images from working tree and start the stack
#   deploy/dev.sh build      build local images without starting
#   deploy/dev.sh reset      switch back to CI-published images from GHCR
#   deploy/dev.sh ps         show status of running stack
#   deploy/dev.sh logs [svc] tail logs
set -euo pipefail
cd "$(dirname "$0")/.."

BASE_COMPOSE="compose.yml"
if [ ! -f "$BASE_COMPOSE" ] && [ -f "compose.example.yml" ]; then
    BASE_COMPOSE="compose.example.yml"
fi

COMPOSE_DEV=(docker compose -f "$BASE_COMPOSE" -f compose.dev.yml)

case "${1:-up}" in
    up)
        "${COMPOSE_DEV[@]}" up -d --build
        ;;
    build)
        "${COMPOSE_DEV[@]}" build
        ;;
    reset)
        docker compose -f "$BASE_COMPOSE" up -d --pull always
        ;;
    ps)
        "${COMPOSE_DEV[@]}" ps
        ;;
    logs)
        shift
        "${COMPOSE_DEV[@]}" logs "$@"
        ;;
    down)
        "${COMPOSE_DEV[@]}" down
        ;;
    *)
        echo "Usage: $0 [up|build|reset|ps|logs|down]" >&2
        exit 1
        ;;
esac
