#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
CSProj="$ROOT_DIR/src/GodotMonoMcp/GodotMonoMcp.csproj"
PROJECT_PATH="/tmp/godot_mono_smoke_project"
OUT_DIR="$ROOT_DIR/smoke-results"

python3 "$ROOT_DIR/scripts/smoke_all_tools.py" \
  --csproj "$CSProj" \
  --project "$PROJECT_PATH" \
  --output-dir "$OUT_DIR" \
  "$@"
