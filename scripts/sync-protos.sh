#!/usr/bin/env bash
# Sync vendored .proto files from the Vertex spec repo at the pinned SHA.
# CI runs this and `git diff --exit-code` to enforce that protos/ tracks the
# spec. To bump: edit scripts/.spec-ref, re-run this script, commit the result.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SHA_FILE="$SCRIPT_DIR/.spec-ref"

if [[ ! -f "$SHA_FILE" ]]; then
  echo "error: $SHA_FILE not found" >&2
  exit 1
fi

SPEC_SHA="$(tr -d '[:space:]' < "$SHA_FILE")"
if [[ -z "$SPEC_SHA" ]]; then
  echo "error: $SHA_FILE is empty; put a dengxuan/Vertex commit SHA in it" >&2
  exit 1
fi

# List of proto files to vendor (spec-relative paths).
PROTOS=(
  "protos/vertex/transport/grpc/v1/bidi.proto"
)

SPEC_RAW_BASE="https://raw.githubusercontent.com/dengxuan/Vertex/$SPEC_SHA"

for p in "${PROTOS[@]}"; do
  dst="$REPO_ROOT/$p"
  mkdir -p "$(dirname "$dst")"
  echo "syncing $p @ $SPEC_SHA"
  curl -fsSL "$SPEC_RAW_BASE/$p" -o "$dst"
done

echo "done. If git diff is non-empty the vendored protos drifted; commit the update."
