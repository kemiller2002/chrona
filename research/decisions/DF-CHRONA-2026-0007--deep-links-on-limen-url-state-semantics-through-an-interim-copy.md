---
id: DF-CHRONA-2026-0007
title: Deep links on Limen's URL-state semantics, through an interim copy of Limen.Routing until Limen 0.9.0 ships
status: accepted
version: 1.1.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - chrona
review_cycle: on-trigger
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt
  - research/decisions/DF-CHRONA-2026-0003--product-ui-on-limen-in-memory-with-identity-and-store-ports.md
  - research/decisions/DF-CHRONA-2026-0004--sign-in-through-fides-client-over-limen-requests.md
  - vendor/limen-routing/SOURCE.md
tags: [routing, deep-links, limen, url-state, sign-in]
derived_from: [DF-CHRONA-2026-0003, DF-CHRONA-2026-0004]
provenance:
  contributions:
    EXE-20261008T194553879Z-750991d9:
      operations: [created, modified]
      at: 2026-10-08T19:57:49.374Z
      last: 2026-10-08T22:05:04.815Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Deep links on Limen's URL-state semantics through an interim copy (WI-0071)"
    EXE-20261008T214626408Z-33828acf:
      operations: [modified]
      at: 2026-10-08T21:51:25.838Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Interim copy replaced by Limen 0.9.0 (WI-0072)"
---

# DF-CHRONA-2026-0007 — Deep links on Limen's URL-state semantics, through an interim copy

## Context

On 2026-10-08 the owner made a portfolio requirement of navigable state in the URL: a copied link opens the same view (requirement section 46, `CHX-460`).

Chrona already had a fragment route for each screen (WI-0046), but:

- filters, More's parts and sign-in did not keep their place in the address;
- an address had no single canonical form;
- an address Chrona did not know showed Today rather than saying so.

Limen is defining the shared semantics for every portfolio application (Limen LCP-088..112, DF-LIMEN-2026-0006). Limen 0.9.0 will ship them with an F# library, `Limen.Routing`. That release is being prepared by another agent and is not out yet.

The work on Limen's branch `urlstate/wi-0168-semantics-fsharp` (commit `e935da7`) already holds the semantics, the F# library and 165 language-neutral conformance vectors. The owner asked that Chrona match that API now, so that moving to the release is mechanical.

## Decision

1. **Limen's semantics, not Chrona's own.** Chrona's places are a typed codec (`RouteCodec`) over a `RouteTable` that Limen's rules validate. The codec lives in `src/Chrona.Engine/App/Places.fs`.
   - Chrona writes only its table, its `Place` type, and the mapping between them.
   - Matching, typing, canonical form, guards, return targets, share links and the inventory are Limen's functions.
2. **An interim copy, verbatim.** Until 0.9.0 ships, `vendor/limen-routing/` holds Limen's own files at `e935da7`:
   - the library;
   - its conformance runner;
   - the vectors.
   `limen-routing.lock` records each file's SHA-256, and `PlacesTests` fails if any file differs. CI runs the conformance runner, so the copy is proven against Limen's vectors in Chrona's own build.
   - The copy keeps Limen's namespace (`Limen.Routing`).
   - Nothing in it is edited.
3. **Switching is mechanical.** When Limen 0.9.0 is released and installed through Conditor:
   1. the engine's `ProjectReference` to the copy becomes the package reference;
   2. `vendor/limen-routing/` is deleted, with its solution entries and CI step.
   No Chrona source changes unless the release's API differs from `e935da7`. If it does, the release's own vectors and `PlacesTests` will show where.
4. **Hash routing.** Places live in the fragment (`#/day/2026-10-08?project=…`), and links are relative. GitHub Pages and any other static host then serve every address with one page.
   - The site root forwards to the page with the fragment intact.
   - The skip link no longer uses a fragment.
5. **The inventory is rendered, never hand-written.** `.echelon/routes.json` (`echelon.routes/v1`, sorted keys, two-space indentation, final newline) is `Inventory.render` of the table.
   - A test fails when the file and the table differ.
   - `CHRONA_WRITE_ROUTES=1 dotnet test --filter PlacesTests` rewrites the file.
   - Its routes carry a `guards` array, as Limen's byte-exact inventory vector specifies. The owner's summary said `guard`; Limen's vector is the contract both libraries render to, so it governs.
6. **Return targets survive the GitHub round trip in this tab's session storage.**
   - GitHub's callback returns to the deployment's redirect URI, which carries no fragment, so the fragment cannot carry the target through.
   - Before leaving for GitHub, the engine keeps the canonical return target (`ReturnTo.capture`) in this tab's session storage. It is a relative address with declared parameters only, never a token.
   - On the callback, the engine reads the target, consumes it, and resumes it (`ReturnTo.resume`, which re-checks guards) with a replace. Back therefore never returns to the sign-in page.
7. **Links never carry the page's query.**
   - A shared link is the page's origin and path plus the canonical fragment (`Link.share` with an empty query).
   - The callback's `code` and `state` are in the page's query, and Fides removes them from the address before anything else. Even so, no link Chrona makes can copy them.
   - The table cannot declare a credential-like parameter (`RouteTable.define` refuses them).
8. **The organization is in the address where it is a choice.** A deployment can serve several organizations. There, the one a person works in is navigable state: a link copied in one must not open another's view. So:
   - every guarded route declares an `org` parameter, last;
   - addresses name the organization when someone is signed in to a deployment of several, and never otherwise;
   - opening a link into another of the deployment's organizations switches to it, as choosing it does;
   - one the deployment does not serve is not found;
   - the organization reaches sign-in inside the return target.
9. **Places that do not exist yet join with their screens.** Candidate review has no screen until observation inboxes are stored (WI-0038). Its addresses join the table, the inventory and the browser tests with that screen; WI-0038's description says so.

## Update (WI-0072, 2026-10-08)

Limen 0.9.0 was released and selected in echelon-current 1.11.0. Point 3 was carried out:

- Conditor installed `limen-fsharp` 0.9.0 (`vendor/nuget/limen-fsharp.lock`, each package proven against the Registry's SHA-256).
- The engine references `EchelonFoundry.Limen.Routing` 0.9.0.
- `vendor/limen-routing/`, its solution entries and its CI step were deleted.

The release's `Routing.fs` is byte-identical to the interim copy at `e935da7`, so no Chrona source changed.

The npm `@echelon-foundry/limen` moved to 0.9.0 with it. The tarball's SHA-256 matches the Registry's.

The inventory is now checked two ways:

- `PlacesTests` holds it byte-equal to the table's rendering.
- `tools/routes/inventory.test.mjs` validates it against `contract/routes.schema.json`, the schema Limen 0.9.0 publishes.

## Consequences

- Every address Chrona makes is canonical. A non-canonical one, such as an old link, a hand-typed one or one with an undeclared parameter, opens its place and the address is replaced with the canonical form, without a new history entry.
- WI-0046's old addresses (`#/today`, `#/today/<date>`, `#/activity/<id>`) are Limen legacy redirects to `#/`, `#/day/<date>` and `#/entries/<id>`.
- Guards decide only what the page shows. GitHub's permissions remain the authority: a page Chrona shows never grants access that the person's token lacks.
- The copy is a second place where Limen's code lives. The lock keeps it identical, and point 3 removes it. Until then, a fix to Limen's branch reaches Chrona only by re-copying the files and updating the lock.
