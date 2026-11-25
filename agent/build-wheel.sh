#!/usr/bin/env bash
# Builds the agent wheel into agent/dist/ (gitignored). The mod's csproj deploy copies it to Engine/agent/.
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist build
if command -v uv >/dev/null 2>&1; then
  uv build --wheel --out-dir dist
else
  python3 -m build --wheel --outdir dist
fi
ls -l dist/*.whl
