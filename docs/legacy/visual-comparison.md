# Chrona's screens against the legacy look

Work item: WI-0054 (CHX-005). This compares each of Chrona's screens with the legacy `time-tracking-application` captures in [`screenshots/`](screenshots/), as inventoried in [`look-and-feel-inventory.md`](look-and-feel-inventory.md). Chrona's screens are the visual baselines in `tests/visual/baselines/`:

- Every screen in light and dark.
- At 1440 and 390 CSS pixels, the widths of the legacy captures.
- Rendered by the pinned Chromium on CI's runner (`playwright.visual.config.js`).

Any change to a screen fails the build until its baseline is replaced on purpose.

## What is kept

All of these come from Forma tokens compiled from `brand/`:

- The warm paper surface (`#e8e4dc`) and the dark green sidebar and tracker.
- The circled-initial navigation marks, and the four sections: Today, Track, Month and More.
- The serif display titles over an uppercase eyebrow.
- Rounded cards.
- The ledger's signature time column, with its dot and rule.
- The mono durations.
- The huge mono elapsed time on the inverse "Timing now" hero.
- The light-on-dark Pause / Stop activity pair.
- The split sign-in page with its dark story panel.
- On a phone, the bottom tab bar replaces the sidebar, as in the legacy mobile captures.

## Screens

| Chrona baseline | Legacy capture | Differences, and why |
|---|---|---|
| `today-*` | [today](screenshots/today-desktop.jpg), [mobile](screenshots/today-mobile.jpg) | Record badges are Forma's text status lozenges, so tone is never carried by colour alone. Day status is Forma's `ef-facts`. "This period" (timesheet totals, WI-0036) is new. The date picker row replaces the arrow pair, so any day can be reached. |
| `track-*`, `timer-*` | [index](screenshots/index-desktop.jpg), [start](screenshots/start-desktop.jpg), [manual entry](screenshots/manual-entry-desktop.jpg) | The shipped legacy app put the timer and manual entry side by side; Chrona keeps that. "Recent" chips (WI-0062) replace the static Quick start grid. The timer title is the activity type, with the project and description beneath it. "Switch activity" is not built. |
| `completion-*` | [stop](screenshots/stop-desktop.jpg), [mobile](screenshots/stop-mobile.jpg) | Same structure: the exact held duration, then the record's details. "Save & start another" and "Discard" are not offered; stopping under thirty seconds discards on its own. |
| `month-*` | [month](screenshots/month-desktop.jpg), [mobile](screenshots/month-mobile.jpg) | Metric cards and by-activity totals are kept. The progress target and the daily-rhythm chart are reporting, and are not built. |
| `review-*` | [review](screenshots/review-desktop.jpg), [mobile](screenshots/review-mobile.jpg) | Same checks list and attestation card: each check with its mark, note and status, then the confirmation. |
| `activity-*` | [activity](screenshots/activity-desktop.jpg), plus [correction](screenshots/correction-desktop.jpg), [evidence](screenshots/evidence-desktop.jpg), [split](screenshots/split-desktop.jpg), [remove](screenshots/remove-desktop.jpg), [restore](screenshots/restore-desktop.jpg) | The legacy separate pages are sections of one activity screen: amend, void and restore, split, evidence and history. |
| `more-*` | [more](screenshots/more-desktop.jpg), [settings](screenshots/settings-desktop.jpg) | More holds the session, the people, the reference data, the period settings and the store's state, instead of a link list. |
| `reports-*` | none | New (WI-0039): reports and export through Folio. |
| `sign-in-*` | [sign-in](screenshots/sign-in-desktop.jpg) | Same split page; sign-in is GitHub through Fides. |

## Remaining gaps

Forma cannot yet express some of the legacy presentation: the dark sidebar, the large radius, the display scale, the inverse hero, the timeline and the filled primary action (G1-G6 in the inventory, filed as WI-0042). Chrona composes these from tokens, and the baselines hold that composition.

## Found while making the baselines

- In the narrow "Add time manually" panel, the side-by-side selects cut off their choices ("Choose an …"). The form grid now drops to one column where two fields do not fit.
- The "Recent" chips on the dark tracker had light text on a light chip, which was unreadable. They now take the tracker's inverse colours.

## Changing a baseline

1. Push the change.
2. CI's "Compare the screens with their baselines" step fails and uploads the rendered images in the `playwright-traces` artifact: `*-actual.png` for a changed screen, and a removed baseline rendered under `tests/visual/baselines/`.
3. Check them against this page.
4. Copy them over `tests/visual/baselines/` and push again.

`CHRONA_VISUAL_PREVIEW=1 npm run test:visual` renders the screens locally into `test-results/visual-preview/` for a look. That render is compared with nothing, because a local browser and its fonts differ from CI's.
