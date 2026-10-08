---
id: DF-CHRONA-2026-0003
title: The product UI is a pure Limen engine with one effectful module, in memory behind identity and store ports until Fides and Arca exist
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
  - research/decisions/DF-CHRONA-2026-0002--reconstruct-not-migrate-legacy-reconciliation-and-data-status.md
  - docs/legacy/look-and-feel-inventory.md
tags: [ui, limen, forma, architecture, ports, arca, fides]
provenance:
  contributions:
    EXE-20261008T090916106Z-9b107edc:
      operations: [created]
      at: 2026-10-08T09:35:19.595Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the product UI architecture: pure engine, effectful runtime, identity and store ports"
derived_from: [DF-CHRONA-2026-0001]
---

# DF-CHRONA-2026-0003 — Product UI architecture

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0046 (and WI-0047 on the same engine)

## Context

The user asked for the UI to be rebuilt on Limen 0.7.1 and Forma 0.4.1
before storage (Arca) and sign-in (Fides) exist, using in-memory state, in
functional style, following the HelixNote Limen pattern, and keeping the
legacy look and feel. The later slices must plug in without reworking the
screens.

## Decision

1. **Pure engine, one effectful module.** `Chrona.Engine.App` holds the
   application: `Model` (plain data), `Update.update : Ctx -> Msg -> Model ->
   Model * Effect list` and `Project.project : Model -> View`, all pure. The
   current instant and fresh ids arrive in `Ctx`. Every business rule is
   `Chrona.Domain`'s; the engine only routes intent to it. As in HelixNote,
   one module has effects: `Chrona.Application.Runtime` holds the page state
   and supplies the clock, ids and ports. `Chrona.Application.App` translates
   between Limen and the engine under the Aegis boundary and is
   deterministic for a given environment, so it is tested as JSON in, JSON
   out.
2. **Effects are data.** The engine asks for `Navigate`, `Wake` (the running
   timer's once-a-second refresh, through Limen's `limen.schedule` pack),
   `DescribeEnvironment` (the browser's time zone, through
   `limen.environment`) and `Store`. The display is always computed from
   timestamps, never counted in the browser (CHX-101).
3. **Identity port.** `Session` says who is working. Until Fides exists the
   implementation is a local session ("LS", one person, this tab). Fides
   (WI-0029) supplies a signed-in session; nothing else changes.
4. **Store port.** Every accepted command produces a `StoreRequest` holding
   the whole records it created or changed; the store answers `Committed`,
   `Conflict`, `Failed` or `OutcomeUnknown`, and the page shows the answer
   (CHX-210, CHX-230 vocabulary). Until Arca exists the in-memory store
   acknowledges at once and keeps nothing beyond the tab, and the sidebar
   says so ("Kept in this tab only"). Arca (WI-0032) answers the same
   requests asynchronously from GitHub.
5. **Routes are fragments** (`#/today`, `#/today/2026-10-08`, `#/track`,
   `#/more`) pushed through Limen's Navigation effect, so every screen has an
   address that survives Back, Forward and deep links on a static host.
6. **Look and feel.** Forma components with the Chrona brand (the legacy
   palette, type and radii as Forma tokens). What Forma 0.4.1 cannot express
   (gaps G1-G6, WI-0042) is composed in `web/chrona.css` from `--ef-*`
   tokens only; a test refuses literal colours there. Forma's skip link
   needs its marketing spacing tokens, so Chrona keeps the legacy skip link.
7. **Native validation does not gate the engine.** Limen calls
   `reportValidity()` before a form submits, so fields carry
   `aria-required="true"` rather than `required`: the domain reports every
   problem at once, in words, with its stable code.
8. **The kernel verification slice stays** as `web/kernel-slice.html` with
   its tests; the product page is `web/index.html`.

## Consequences

- Refreshing the page loses this session's records until Arca exists. The
  page states it; the user asked for in-memory state for now.
- The Fides and Arca slices replace two values in `Runtime` (the session and
  the store) and add the asynchronous store interpreter; screens, engine and
  tests stay.
- WI-0047 adds the lifecycle and review screens on the same engine.

## Revisit when

Arca's record format (ARCA-REC) or token-provider port (ARCA-AUTH) does not
fit the store or identity port as defined here.
