#!/usr/bin/env bash
# Re-renders the visual baselines on CI's pinned Chromium and proposes them as
# a pull request (DF-CHRONA-2026-0006):
#
#   npm run visual:rebaseline -- <branch> "<why the screens change>"
#
# It starts the "Re-render the visual baselines" workflow on <branch>, waits
# for it, and, when the workflow committed new images but could not open the
# pull request itself (a repository may not let workflows open them), opens
# it with your GitHub CLI credentials. Needs `gh`, signed in, and push access.
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: npm run visual:rebaseline -- <branch> \"<reason>\"" >&2
  exit 2
fi
base="$1"
reason="$2"
repo="$(git remote get-url origin | sed -E 's#(git@github.com:|https://github.com/)##; s#\.git$##')"
workflow="visual-baselines.yml"

latest() { gh api "repos/$repo/actions/workflows/$workflow/runs?branch=$base&event=workflow_dispatch&per_page=1" --jq '.workflow_runs[0].id // empty'; }

before="$(latest)"
gh api -X POST "repos/$repo/actions/workflows/$workflow/dispatches" -f ref="$base" -f "inputs[reason]=$reason" > /dev/null
echo "Started the re-render on $base; waiting for its run..."
run=""
for _ in $(seq 1 60); do
  run="$(latest)"
  [ -n "$run" ] && [ "$run" != "$before" ] && break
  sleep 5
done
[ -n "$run" ] && [ "$run" != "$before" ] || { echo "The workflow run did not appear." >&2; exit 1; }

echo "Run $run: https://github.com/$repo/actions/runs/$run"
while [ "$(gh api "repos/$repo/actions/runs/$run" --jq .status)" != "completed" ]; do sleep 15; done
conclusion="$(gh api "repos/$repo/actions/runs/$run" --jq .conclusion)"
[ "$conclusion" = "success" ] || { echo "The re-render failed ($conclusion); see the run." >&2; exit 1; }

branch="visual/rebaseline-$run"
if ! gh api "repos/$repo/branches/$branch" > /dev/null 2>&1; then
  echo "The baselines are unchanged; nothing to propose."
  exit 0
fi
existing="$(gh api "repos/$repo/pulls?head=${repo%%/*}:$branch&state=open" --jq '.[0].html_url // empty')"
if [ -n "$existing" ]; then
  echo "Pull request: $existing"
  exit 0
fi
changed="$(gh api "repos/$repo/compare/$base...$branch" --jq '.files[].filename' | sed 's/^/- /')"
body="$(printf 'Re-rendered by run %s with the pinned Chromium on CI.\n\nReason: %s\n\nChanged baselines:\n%s\n\nReview each image against docs/legacy/visual-comparison.md before merging.' "$run" "$reason" "$changed")"
url="$(gh api "repos/$repo/pulls" -f base="$base" -f head="$branch" -f title="Re-render the visual baselines: $reason" -f body="$body" --jq .html_url)"
echo "Pull request: $url"
