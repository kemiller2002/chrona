---
id: EV-CHRONA-2026-0001
title: Legacy time-tracking-application look and feel, catalogued and expressed as Forma tokens
research_area: ui-reconstruction
evidence_type: primary
source_title: kemiller2002/time-tracking-application (static-ui-screens and web)
source_author: kemiller2002
source_uri: https://github.com/kemiller2002/time-tracking-application/tree/38a0b657e1c686098169630a5920acaff5dedd6f
source_date: 2026-10-08
retrieved: 2026-10-08
created_by_agent: anthropic/claude-code
confidence: high
supports: [DF-CHRONA-2026-0001]
contradicts: []
related_theories: []
tags: [legacy, ui, inventory, forma, brand]
provenance:
  contributions:
    EXE-20261008T083618828Z-5e4110e0:
      operations: [created]
      at: 2026-10-08T08:41:17.375Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Catalogue the legacy look and feel and express it as Forma tokens"
derived_from: [DF-CHRONA-2026-0001]
---

# Evidence Record

## Evidence summary

The legacy application's presentation was read in full (18 design-study
pages, the shipped single-page `web/index.html`, and both stylesheets) and
rendered in Chromium. Its look is a small, consistent visual language: a warm
stone canvas with lighter paper cards, a dark green-black sidebar and timer
hero, a mineral-green accent, a serif display face over a sans body, a mono
timer and time column, and rounded surfaces. The full catalogue is
[`docs/legacy/look-and-feel-inventory.md`](../../docs/legacy/look-and-feel-inventory.md).

## Exact claim supported or contradicted

Supports DF-CHRONA-2026-0001 decision 2: the legacy look and feel can be kept
while the code is rebuilt on Forma. The palette, type and radii fit Forma's
Brand Manifest contract exactly (`brand/chrona.brand.json`, compiled by
Forma 0.4.1's own compiler with every contrast gate passing). Six
presentation needs (sidebar and tab bar, large surface radius, display title
scale, inverse timer hero, time-column timeline, filled primary action and
control radius) are not expressible in Forma 0.4.1's public contract and are
recorded as gaps G1-G6.

## Source provenance

`kemiller2002/time-tracking-application` at
`38a0b657e1c686098169630a5920acaff5dedd6f` (local read-only checkout), files
`static-ui-screens/*.html`, `static-ui-screens/styles.css`, `web/index.html`,
`web/styles.css`, `web/dom-bindings.js`, `web/main.js`.

## Relevant excerpt or data

`web/styles.css` `:root` tokens (`--canvas #e8e4dc`, `--paper #f8f5ee`,
`--ink #1f2826`, `--mineral #215d57`, `--focus #0b6fb8`, the serif, sans and
mono stacks) and its dark, forced-colors and print media blocks; the 36
screenshots under `docs/legacy/screenshots/`.

## Interpretation

Keeping the look means keeping these tokens and compositions, not copying the
legacy CSS: the tokens move into a Forma brand, Forma components replace the
legacy primitives, and only the six gaps need Chrona composition until Forma
extends its contract.

## Limitations

The screenshots are of the design study with sample data, not of the shipped
application running against data; the shipped app reuses the same stylesheet
and differs only by folding screens into four tabs, which the inventory
describes from source. Webfonts are not bundled by the legacy app, so the
renders use the fallback faces available to Chromium.

## Counterevidence

Two legacy values fail WCAG 2.2 AA (`--line-strong` input borders at about
1.9:1, gold warning text below 4.5:1). Reproducing them exactly would
contradict "all our standards"; the brand corrects them and the inventory
says so.

## Reproduction or verification notes

Screenshots: Playwright 1.63.0 with the pinned Chromium, `file://` URLs of
`static-ui-screens/*.html`, 1440x900 and 390x844, full page, JPEG quality 70.
Brand: `tools/brand/compile.sh --check` (CI) and `BrandTokenTests`.
