# UI conformance: defects owned upstream

Chrona#112 (GH-112) records here what its UI conformance run
(`npm run ui:conformance`, contract `visual-engineering/UI-LAYOUT-INTERACTION@1.0.0`)
found in shared packages. Chrona does not patch these locally (ULI-L03,
ULI-L13). Each one stays FAIL in the evidence until the pinned package fixes
it. The test that observes it fails when the defect stops reproducing, so the
entry cannot go stale.

## F-1 — Forma: a facts value cannot wrap (ULI-L05) — fixed in Forma 1.0.1

- **Status:** fixed upstream. With `@echelon-foundry/design-system` 1.0.1
  (echelon-current 1.42.0, WI-0087) the run passes at 320 CSS px and 200%
  text, so the entry was removed from `TEXT_SCALE_KNOWN`. Forma's containers
  now break a token too long for its line (Forma 0.13 changelog), and 1.0.0
  renamed the class `.forma-facts__item`. The record below is as found.

- **Package:** `@echelon-foundry/design-system` 0.6.1 (Forma), `dist/all.css`.
- **Rule:** `.ef-facts__item > dd` sets margin, colour and size and nothing for
  long values. An unbreakable value cannot wrap inside its item.
- **Where Chrona sees it:** More (`#/more`), "This session" facts, time zone
  `America/New_York`, at 320 CSS px with the default text size at 200%. The
  `dd` runs from x=109 to x=362, so the page scrolls sideways by 42 px.
- **Evidence:** `tests/conformance/app.spec.js`, test "app-more: at 200% text,
  nothing overflows or clips…". It records FAIL with the escaping text run and
  asserts that the run still fails.
- **Suggested upstream fix:** `overflow-wrap: anywhere` (or `break-word`) on
  `.ef-facts__item > dd`. The item already has `min-inline-size: 0`.
- **Not affected:** 100% text at every contract width, and 200% text at 390,
  768 and 1280.
