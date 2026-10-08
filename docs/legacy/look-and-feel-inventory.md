---
id: CHRONA-LEGACY-LOOK-AND-FEEL
title: Legacy time-tracking-application look-and-feel inventory
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - chrona
related_documents:
  - research/decisions/DF-CHRONA-2026-0001--reconstruct-on-limen-forma-keeping-legacy-look-and-feel.md
  - research/evidence/EV-CHRONA-2026-0001--legacy-look-and-feel-inventory.md
  - brand/chrona.brand.json
tags: [legacy, ui, inventory, forma, brand]
provenance:
  contributions:
    EXE-20261008T083618828Z-5e4110e0:
      operations: [created]
      at: 2026-10-08T08:41:17.808Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Catalogue the legacy look and feel and express it as Forma tokens"
---

# Legacy look-and-feel inventory

Work item: WI-0026. Decision: DF-CHRONA-2026-0001 (reconstruct, keep the look
and feel). Source: `kemiller2002/time-tracking-application` at
`38a0b657e1c686098169630a5920acaff5dedd6f`, read-only.

The legacy application has two presentation sources, and both were read in
full:

- `static-ui-screens/` is the design study: 18 static pages with sample data
  and one shared stylesheet (`styles.css`, 1,855 lines). It is the most
  complete statement of the intended look.
- `web/` is the shipped single-page application (`index.html`, `styles.css`
  1,921 lines, `dom-bindings.js`). It reuses the design study's stylesheet
  plus a short "host addendum", and folds the screens into four tabs.

Screenshots were rendered from the design study with Playwright and the
pinned Chromium (desktop 1440x900 full page, JPEG q70; mobile 390x844 for the
six priority screens) and are stored in [`screenshots/`](screenshots/).

## 1. Visual language

| Aspect | Legacy | Character |
|---|---|---|
| Canvas | `--canvas #e8e4dc`, warm stone | Page background, darker than the cards. |
| Paper | `--paper #f8f5ee`, `--paper-raised #fffdf8` | Cards (`.surface`) sit lighter than the page. |
| Ink | `--ink #1f2826`, `--ink-soft #53605c` | Near-black green; soft secondary text. |
| Rules | `--line #d2cdc2`, `--line-strong #aaa499` | Hairlines between records and around cards. |
| Accent | `--mineral #215d57` (dark `#17443f`, pale `#dce9e5`) | Primary actions, eyebrows, active nav, timeline dots. |
| Secondary accents | `--rust #9a4d32`, `--gold #9b742e` (pale variants) | Category bars, warnings, active-nav rail (`#d9b964`). |
| Inverse | sidebar `#23312e`, tracker `#243a36` | Dark green-black chrome with warm light text. |
| Focus | `3px solid #0b6fb8`, offset 3px | Blue, independent of the accent. |
| Danger | `#8c3529` | Errors and destructive actions. |
| Display type | `Iowan Old Style, Baskerville, "Times New Roman", serif` | Page titles (`clamp(2.2rem, 5vw, 4.4rem)`, weight 500, tight tracking, line-height 0.98), section titles, activity titles, metric values. |
| Body type | `Inter, ui-sans-serif, -apple-system, ...` | Everything else, line-height 1.5. |
| Mono type | `SFMono-Regular, Consolas, "Liberation Mono"` | Timer display (`clamp(3rem, 10vw, 6.4rem)`), record times, durations. |
| Eyebrow | 0.72rem, weight 900, letter-spacing 0.13em, uppercase, mineral | Above every page title. |
| Radii | 0.55rem / 0.9rem / 1.35rem; buttons 0.7rem; tracker 1.5rem | Soft, rounded cards and controls. |
| Elevation | `0 18px 50px rgba(31,40,38,.1)` on the tracker only | One raised object per screen. |
| Spacing | `0.35, 0.65, 1, 1.5, 2.25, 3.5rem` | Generous; page padding `clamp(1.25rem, 4vw, 4rem)`. |
| Dark mode | `prefers-color-scheme: dark` remaps every token (`#141b19` canvas, `#84bdb0` mineral, `#78c8ff` focus) | Sidebar and tracker stay dark (`#192824`). |
| Forced colors | Borders become `CanvasText`; dots, bars and progress fills become `CanvasText` | State keeps a shape, not only a color. |
| Print | Sidebar, bottom nav, top-bar actions and form actions hidden; single column | Records print as a document. |

## 2. Layout

- **App shell.** Two-column grid: a sticky 15.5rem dark sidebar (brand mark
  and serif name, four navigation items with two-letter circular marks, a
  sync status block pinned to the bottom) and a centred page column
  (`min(100%, 78rem)`).
- **Top bar.** Left: date eyebrow or previous/next day buttons. Right: a
  compact running-timer chip (inverse surface, mono time) and a circular
  identity mark (initials).
- **Page heading.** Eyebrow, large serif title, one-paragraph intro.
- **Two-column body** (`1.45fr / 0.75fr`, collapses below 900px): the main
  work surface left, a stack of summary cards, notices and the secondary
  action right.
- **Page footer.** A hairline and one sentence restating the rule the page
  depends on (for example "Exact seconds remain authoritative").
- **Mobile (<= 760px).** The sidebar disappears; a fixed bottom navigation
  bar with four equal tabs (mono two-letter mark above the label, pale
  mineral pill on the current tab) replaces it; form actions stack full
  width; the tracker bleeds slightly into the gutter.

## 3. Screens

| Screen | Legacy file | Purpose | Key elements | Screenshot |
|---|---|---|---|---|
| Track (timer running) | `index.html` | Time current work. | Dark tracker card: "Timing now" pulse label, serif activity title, project line, huge mono elapsed time, "Started at" note, Pause / Stop activity / Switch activity. Quick-start grid. Mini-ledger of today, "Add time manually", warning notice. | [desktop](screenshots/index-desktop.jpg), [mobile](screenshots/index-mobile.jpg) |
| Start | `start.html` | Choose work and start a timer. | "What are you working on?" form (activity type, project, optional note), full-width Start timer; favourites grid; mini-ledger. | [desktop](screenshots/start-desktop.jpg) |
| Stop / complete | `stop.html` | Complete a stopped timer. | Exact stopped duration in serif, record details form (type, project, description, business purpose, outcome), optional evidence link; "Recorded exactly" facts card (started, stopped, method, status); "Your time is safe" notice; Discard with explanation / Save / Save & start another. | [desktop](screenshots/stop-desktop.jpg), [mobile](screenshots/stop-mobile.jpg) |
| Manual entry | `manual-entry.html` | Add time already worked. | Start/end or duration, common-duration options, reason for after-the-fact entry, "Before saving" checks. | [desktop](screenshots/manual-entry-desktop.jpg), [mobile](screenshots/manual-entry-mobile.jpg) |
| Today | `today.html` | The day's ledger. | Day summary (exact total, entry count), vertical timeline of records (mono start/end times, dot on a rule, serif title, project line, description, badges: Timer / Manual entry / Corrected once / Purpose missing / N evidence), day status facts, "Review this day". | [desktop](screenshots/today-desktop.jpg), [mobile](screenshots/today-mobile.jpg) |
| Month | `month.html` | Monthly factual summary. | Total with progress bar against a target, decimal hours, metric cards (active days, evidence coverage, timer/manual, corrections/voids), daily-rhythm bar chart, by-activity bars, View warnings / Export monthly report. | [desktop](screenshots/month-desktop.jpg), [mobile](screenshots/month-mobile.jpg) |
| Daily review | `review.html` | Attest a day. | Effective total, metric cards, review checks list (check or warning mark, title, note, mono status, Fix link), attestation card with confirmation checkbox, Attest / Defer, "Attestation is not locked" notice. | [desktop](screenshots/review-desktop.jpg), [mobile](screenshots/review-mobile.jpg) |
| Activity detail | `activity.html` | One effective record. | Current record facts, history (original and corrections with markers), evidence list, record actions. | [desktop](screenshots/activity-desktop.jpg) |
| Correction | `correction.html` | Amend a record. | Edit form, change preview (old value struck, new value), reason. | [desktop](screenshots/correction-desktop.jpg) |
| Evidence | `evidence.html` | Attach evidence. | Linked evidence rows (type chip, title, meta, Unlink), attach form. | [desktop](screenshots/evidence-desktop.jpg) |
| Split | `split.html` | Divide one session. | Two parts with duration, live balance (part + part = original), evidence assignment. | [desktop](screenshots/split-desktop.jpg) |
| Merge | `merge.html` | Join related records. | Selected sources, merge preview, merged details. | [desktop](screenshots/merge-desktop.jpg) |
| Remove (void) | `remove.html` | Exclude time, keep history. | Impact facts, reason, "Remove from totals". | [desktop](screenshots/remove-desktop.jpg) |
| Restore | `restore.html` | Return voided time. | Impact facts, overlap note, "Restore to totals". | [desktop](screenshots/restore-desktop.jpg) |
| More | `more.html` | Index of record-keeping and setup pages. | Link list with title, note and arrow. | [desktop](screenshots/more-desktop.jpg) |
| Settings | `settings.html` | Preferences. | Quick starts, display and accessibility toggles, defaults, review reminders. | [desktop](screenshots/settings-desktop.jpg) |
| Sign in | `sign-in.html` | Authenticate. | Split page: dark story panel ("A record you can explain") and sign-in form. | [desktop](screenshots/sign-in-desktop.jpg) |
| System states | `states.html` | Reference for loading, empty, offline, failed, session-expired, saved and version-conflict states. | State cards (mono state code, serif title, copy, one next action); conflict comparison (currently saved / your proposed change, Keep saved / Edit my change / Apply again). | [desktop](screenshots/states-desktop.jpg) |

The shipped `web/index.html` folds these into four tabs: **Today** (timeline,
void/restore, merge selection, day status, attestation warnings), **Track**
(timer and manual entry side by side), **Month** (metric cards and the month's
activities) and **More** (activity detail with amend, void, restore, split and
evidence; merge; day review and attestation; reference-data administration;
reports in JSON, Markdown and CSV; preferences; GitHub sync settings).

## 4. Components

| Legacy component | Forma 0.4.1 equivalent | Chrona reconstruction |
|---|---|---|
| Skip link `.skip-link` | `skip-link` pattern | Forma. |
| App shell, sidebar, bottom nav | `workspace-shell`, `sidebar`, `navigation-shell`, `mobile-action-bar` | Forma tokens; the dark rounded sidebar and the four-tab bottom bar are Chrona composition (gap G1). |
| Brand mark, nav mark (circled initials) | none | Chrona composition on `--ef-color-*` tokens. |
| Eyebrow, page title, page intro | `text-roles`, `section-heading` | Brand display font; the oversized title scale is Chrona composition (gap G3). |
| Surface `.surface` | `surface` | Forma `ef-surface`; the 1.35rem radius is gap G2. |
| Tracker (dark timer hero) | none | Chrona composition on `surface-inverse` / `text-inverse` (gap G4). |
| Compact timer chip | none | Chrona composition on `surface-inverse`. |
| Timeline and record | `timeline`, `feed-item`, `dense-ledger` | Chrona composition: the time column, dot and rule are the legacy signature (gap G5). |
| Badge (good / warn / change) | `badge`, `status-lozenge` | Forma `ef-status-lozenge` with text; tone never carried by color alone. |
| Notice (info / warning / danger) | `alert`, `callout` | Forma `ef-alert` with `data-tone`. |
| Field, form grid, field pair, required mark, help | `text-field`, `select`, `textarea`, `date-time-field`, `validation-message` | Forma `ef-field`. |
| Field error (role=alert) | `validation-message`, `validation-summary` | Forma; restrained live regions. |
| Button primary / secondary / ghost-light / light / danger / text-link | native `button`, `ef-button`, `ef-actions` | Forma supplies one neutral square button; the filled primary, the light-on-dark tracker buttons and the rounded shape are gap G6. |
| Detail grid (label / value) | `facts`, `key-value-list` | Forma `ef-facts`. |
| Metric card, day summary | `metric-card` | Forma `ef-metric-card`. |
| Progress bar, bars, week chart | `progress-bar`, `measure` | Forma where it exists; the daily-rhythm chart is reporting (WI-0039). |
| Review list (check / warn mark) | `readiness-checklist`, `obligation-panel` | Forma. |
| Evidence row | `evidence-relationship`, `key-value-list` | Forma. |
| State card, conflict grid | `empty-state`, `conflict-review`, `operation-status`, `fault-*` | Forma; Aegis faults through Forma's fault family. |
| Segmented control, toggle, duration options | `segmented-control`, `switch`, `choice-group` | Forma. |
| Quick-start card | `card-grid` | Chrona composition (quick entry, CHX-330). |
| Report `<pre>` | Folio print surface | Folio (WI-0039). |

## 5. Interactions

- **Navigation.** Four destinations (Today, Track, Month, More) in the sidebar
  and, on mobile, the bottom bar; the current one carries `aria-current="page"`
  and a gold inset rail. The shipped app switches screens in place
  (`ViewScreen`); the design study used separate pages.
- **Timer.** Start (activity type and project required, description
  optional), Pause, Resume, Stop. The shipped app refreshes the elapsed label
  every six seconds only while a timer is running, and the label is always
  the engine's own computation, never a client-side counter. Stopping leads
  to a completion form; the stopped duration is exact and the session stays
  recoverable.
- **Manual entry.** Activity type, project, start and end (date and time
  pairs), description, business purpose, tags; a reason is required when the
  date is not today. Errors appear under the form with `role="alert"`.
- **Lifecycle.** Void and restore from the record row; amend, split (two
  parts, minutes) and evidence attach/unlink from the activity detail; merge
  by checking two or more contiguous same-day records. Every change takes a
  reason.
- **Review.** A per-day attestation statement; warnings list records amended
  after the last attestation.
- **Date navigation.** Native date and month inputs, and previous/next
  buttons in the design study.
- **Feedback.** Every state has text and a next step (loading, empty,
  offline, failed, session expired, saved, version conflict); colour is never
  the only signal.
- **Keyboard and accessibility.** Skip link, visible focus, 44-48px targets,
  semantic landmarks (`aside`, `nav`, `main`, `section` with
  `aria-labelledby`), `role="alert"` errors, forced-colors and dark-mode
  support, reduced chrome in print.

## 5a. Legacy UI documentation, PWA shell and offline cues

The legacy `docs/UI-*.md` set was read alongside the code. What it adds to
the look and feel:

- **Principles** (`UI-VISUAL-ENGINEERING-INTERPRETATION.md`): one dominant
  task per view (Today: see the day; Track: control the timer); timer and
  sync state visible on every route; one continuous chronological ledger, not
  a grid of dashboard cards; effective values lead, history discloses
  progressively; mobile recomposes rather than reorders; restrained colour
  with text and shape redundancy; the timer gets scale and position, never
  alarm colour or animation; a compact running-timer control above the
  navigation everywhere except Track; stop first, then a short completion
  form.
- **State model** (`UI-STATE-MODEL.md`): timer `unknown -> idle -> running
  <-> paused -> stopped`; elapsed time derived from timestamps, never an
  interval counter; commands `local-only -> sending -> saved` or
  `needs-attention` / `conflict`; drafts and routes are local UI state.
- **Components** (`UI-COMPONENTS.md`): semantic CSS patterns, no framework
  runtime; persistent timer, six-minute duration picker, chronological
  timeline item, in-page detail workspaces (no modals), conflict comparison,
  monthly bars with exact-value labels, bottom navigation; 44px targets.
- **Accessibility** (`UI-ACCESSIBILITY.md`, `UI-VISUAL-REVIEW.md`): WCAG 2.2
  AA target (conformance not claimed); tabular timer numerals; a polite live
  region for timer transitions only, never per second; in-page workspaces
  move focus to Back and restore the invoking control; reviewed at 320px with
  no overflow. iPhone VoiceOver, 200% zoom and forced-colors checks were
  listed as still required.
- **PWA shell** (`web/manifest.webmanifest`, `web/service-worker.js`):
  standalone display, `background_color #e8e4dc`, `theme_color #215d57`, an
  SVG icon; the service worker caches the shell and the WASM `_framework`
  files at runtime, never intercepts writes or cross-origin requests, and
  falls back to the cached shell for an offline navigation.
- **Offline cues** (`UI-OFFLINE-BEHAVIOR.md`): every command wrote to
  `localStorage` first, so there was no pending state to show; the sidebar
  status line ("Saved to this browser", "All changes saved / Synced at") and
  the GitHub sync notice carried sync state; a failed push retried on the
  browser's `online` event. Chrona's offline model (CHX-230, WI-0033) replaces
  this with an explicit queue on Arca, so the cue vocabulary is kept and the
  mechanism is not.

## 6. The design as Forma tokens

The palette, type and radii are expressed as a Forma Brand Manifest,
[`brand/chrona.brand.json`](../../brand/chrona.brand.json), compiled by
Forma 0.4.1's own brand compiler (`tools/brand/compile.sh`) into
[`web/brand/chrona.css`](../../web/brand/chrona.css). The page applies it with
`data-ef-brand="chrona"`. Forma components then render in the legacy palette
with no forked CSS, and Forma's forced-colors projection still wins.

| Legacy token | Forma token (light / dark) |
|---|---|
| `--canvas` | `surface-primary` `#e8e4dc` / `#141b19` |
| `--paper` | `surface-secondary` `#f8f5ee` / `#1b2421` |
| `--paper-raised` | `surface-elevated` `#fffdf8` / `#222d29` |
| sidebar / tracker | `surface-inverse` `#23312e` / `#192824`; `surface-inverse-secondary` `#243a36` / `#213531` |
| `--ink` | `text-primary`, `text-heading`, `text-on-secondary-surface` `#1f2826` / `#f1eee6` |
| `--ink-soft` | `text-secondary`, `text-muted` `#53605c` / `#b5c0bb` |
| `--line` | `border-subtle` `#d2cdc2` / `#3b4944` |
| `--line-strong` | `border-functional` `#6e6a62` / `#8a9893` (darkened: see below) |
| `--mineral` | `accent-primary`, `control-active` `#215d57` / `#84bdb0`; `accent-hover` `#17443f` / `#b6ded5` |
| `--rust` | `accent-secondary` `#9a4d32` / `#d5896f` |
| `--gold` | `status-warning` `#775819` / `#d2ad60` (darkened: see below) |
| `--danger` | `status-danger` `#8c3529` / `#f1a18f` |
| `--focus` | `focus-ring` `#0b6fb8` / `#78c8ff` |
| serif / sans / mono stacks | `font-family-display` / `sans` / `mono` (unchanged) |
| `--radius-sm` / `--radius` | `radius-small` `0.55rem` / `radius-medium` `0.9rem` |

Two legacy values do not meet the standard Chrona is rebuilt to, so the
reconstruction corrects them rather than copying them:

- `--line-strong #aaa499` drew input borders at about 1.9:1 against the
  canvas, below WCAG 2.2 SC 1.4.11's 3:1 for component boundaries. The
  functional border is `#6e6a62` (light) and `#8a9893` (dark).
- `--gold #9b742e` as warning text is below 4.5:1 on the canvas. Warning
  text is `#775819`; the gold remains the hue.

`BrandTokenTests` holds the manifest to these values and checks the contrast
pairs Chrona relies on beyond what the compiler gates.

## 7. Gaps in Forma's public contract

Forma's components are deliberately neutral and square (`radius-none` on
buttons and inputs, no filled primary button). The legacy look needs
presentation the 0.4.1 manifest cannot express. Per Forma's branding rules
these are not solved by restyling `.ef-*` classes; Chrona composes its own
`chrona-*` classes that consume only `--ef-*` tokens (no literal colours), and
each gap is recorded for a deliberate Forma contract extension:

| Gap | Need | Chrona's interim composition |
|---|---|---|
| G1 | A dark, persistent application sidebar and a mobile bottom tab bar with marks. | `chrona-shell`, `chrona-sidebar`, `chrona-tabbar` |
| G2 | A large surface radius token (legacy 1.35rem). | `chrona-card` uses `radius-medium` |
| G3 | A display title scale for page titles (legacy up to 4.4rem). | `chrona-title` |
| G4 | An inverse "hero" surface for the running timer, with light-on-dark actions. | `chrona-tracker` |
| G5 | A time-column timeline (times, dot, rule) for ledgers. | `chrona-timeline` |
| G6 | A filled primary action and a control radius token for buttons and inputs. | `chrona-action--primary`, control radius from `radius-small` |

The gaps are filed as WI-0042 so they reach Forma instead of
staying local.
