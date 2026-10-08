#!/bin/sh
# Compiles Chrona's Forma Brand Manifest (brand/chrona.brand.json) with the
# brand compiler of the exact Forma release the page consumes, so the CSS is
# Forma's own output and never hand-written.
#
#   tools/brand/compile.sh            regenerate web/brand/
#   tools/brand/compile.sh --check    fail if web/brand/ is not what the
#                                     pinned compiler produces (CI)
#
# Needs git, the .NET SDK and network access to github.com.
set -eu

FORMA_REPOSITORY="https://github.com/kemiller2002/forma.git"
# Must match the Forma release pinned in package.json.
FORMA_TAG="v0.4.1"

root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$FORMA_TAG" --filter=blob:none --sparse \
  "$FORMA_REPOSITORY" "$work/forma"
git -C "$work/forma" sparse-checkout set tools/BrandCompiler

out="$work/out"
dotnet run --project "$work/forma/tools/BrandCompiler/BrandCompiler.fsproj" -- "$root/brand" "$out"

if [ "${1:-}" = "--check" ]; then
  if diff -r "$out" "$root/web/brand"; then
    echo "web/brand/ is current with brand/ and Forma $FORMA_TAG."
  else
    echo "web/brand/ is stale: run tools/brand/compile.sh and commit the result." >&2
    exit 1
  fi
else
  rm -rf "$root/web/brand"
  cp -R "$out" "$root/web/brand"
  echo "Regenerated web/brand/ with Forma $FORMA_TAG."
fi
