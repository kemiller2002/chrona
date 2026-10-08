---
id: DF-CHRONA-2026-0006
title: Visual baselines are rendered by CI's pinned Chromium and re-rendered by a workflow that opens a pull request
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - chrona
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - research/decisions/DF-CHRONA-2026-0001--reconstruct-on-limen-forma-keeping-legacy-look-and-feel.md
  - docs/legacy/visual-comparison.md
tags: [testing, visual-regression, ci, look-and-feel]
derived_from: [DF-CHRONA-2026-0001]
provenance:
  contributions:
    EXE-20261008T190112021Z-53a6293f:
      operations: [created]
      at: 2026-10-08T19:01:12.470Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Visual baselines rendered by CI, re-rendered by a workflow (WI-0070)"
---

# DF-CHRONA-2026-0006 — Visual baselines rendered by CI, re-rendered by a workflow

## Context

WI-0054 holds every screen, in light and dark, at desktop and phone width, as a baseline image. A failure is a visual change.

A screenshot depends on two things:

- **The browser build.** The pinned Playwright 1.63 runs Chromium 153. A development container may ship another, such as 141.
- **The fonts the operating system offers.** The brand's Inter and Iowan Old Style are not installed on the runner, so text falls back to what is there.

Baselines rendered anywhere but CI therefore fail on CI, and the reverse is also true. GitHub also updates the runner image from time to time, and a font change can then alter every screen at once with no change to Chrona.

On 2026-10-08 the coordinator, for the user, accepted baselines rendered by CI on one condition: regenerating them must be routine, not a manual chore.

## Decision

1. **One renderer.** The baselines are rendered only by the Chromium build that the pinned Playwright version downloads, on CI's Ubuntu runner.
   - `playwright.visual.config.js` never substitutes another browser.
   - CI compares the screens in its own step, "Compare the screens with their baselines".
   - A local render (`CHRONA_VISUAL_PREVIEW=1`) is for looking only and is compared with nothing.
2. **A missing baseline fails.** It is written on the runner and uploaded with the failure's artifact, so a new screen's first image comes from CI and is reviewed. A run with a missing baseline is never green.
3. **Re-rendering is a workflow.** "Re-render the visual baselines" (`.github/workflows/visual-baselines.yml`) is run by hand with a reason, from the Actions tab or with `gh workflow run visual-baselines.yml --ref <branch> -f reason=...`. It:
   1. renders every baseline again;
   2. compares against what it just rendered, so an unstable render fails there;
   3. commits the images to `visual/rebaseline-<run>`;
   4. opens a pull request that lists each changed image;
   5. starts the Build workflow on that branch, because a push made with the workflow's token starts no workflow by itself.

   The pull request merges under the usual gate: every check green on its head.
4. **Review.** Each changed image is reviewed against `docs/legacy/visual-comparison.md` before merging. A font-only re-render is reviewed like any other change.

## Consequences

- Nobody copies images by hand, and no local environment needs the pinned browser.
- A runner-image font change is one workflow run and one reviewed pull request.
- The workflow needs `contents`, `pull-requests` and `actions` write permission on its token. The repository must also allow GitHub Actions to create pull requests (Settings > Actions > General). Without that, the run fails with that explanation and leaves the images on their branch.

## Revisit when

Limen or Forma ships a visual-testing harness of its own; the brand's fonts are bundled with the page, which would make renders independent of the runner's fonts; or Playwright's pinned version changes the browser build. A Playwright upgrade is itself a re-render.
