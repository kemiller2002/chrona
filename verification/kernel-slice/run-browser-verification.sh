#!/usr/bin/env bash
# Serves the repository root and runs the browser verification against it.
set -euo pipefail
cd "$(dirname "$0")/../.."
# dist/ is gitignored, so build before serving: this must work from a clean
# checkout without a separate manual step.
npx tsc -p verification/kernel-slice/tsconfig.json

python3 -m http.server 4173 --bind 127.0.0.1 >/dev/null 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
for _ in $(seq 1 40); do
  curl -sf -o /dev/null "http://127.0.0.1:4173/verification/kernel-slice/index.html" && break
  sleep 0.25
done
node verification/kernel-slice/test/browser-verification.mjs
