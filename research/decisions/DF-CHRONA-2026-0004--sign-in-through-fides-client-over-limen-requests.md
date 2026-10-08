---
id: DF-CHRONA-2026-0004
title: Sign-in runs Fides' own client inside the Limen request/reply loop, configured only by the deployment's configuration document
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
  - research/decisions/DF-CHRONA-2026-0003--product-ui-on-limen-in-memory-with-identity-and-store-ports.md
  - docs/requirements/CHRONA-DATA-LOCATION.md
  - docs/deployment-configuration.md
tags: [identity, fides, limen, architecture, configuration, security]
derived_from: [DF-CHRONA-2026-0003]
provenance:
  contributions:
    EXE-20261008T121136332Z-c6255241:
      operations: [created]
      at: 2026-10-08T12:33:12.158Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Sign-in through Fides and the deployment's configuration document (WI-0029)"
---

# DF-CHRONA-2026-0004 — Sign-in through Fides

- **Date:** 2026-10-08
- **Status:** accepted
- **Work item:** WI-0029

## Context

Sign-in (CHX-022, CHX-023) comes from Fides 0.2.0, installed by Conditor
from its attested release assets like Arca. `EchelonFoundry.Fides.Client`
is an asynchronous F# client that asks its host for browser services
(`ClientPorts`: a POST to the exchange, tab and device storage, navigation,
address replacement, a broadcast channel, the clock and random bytes).
Chrona's engine is pure and its only route to the browser is Limen's
request/reply protocol (DF-CHRONA-2026-0003). Fides is not deployed yet:
there is no exchange, domain or GitHub App, so nothing about it may be
hard-coded, and the real end-to-end check waits on the deployment.

## Decision

1. **Fides' own client, unmodified.** Chrona does not reimplement PKCE, the
   callback, retention, refresh or revocation. `Chrona.Application.Identity`
   creates the client and implements its ports.
2. **Every port is a Limen request.** A port call parks its continuation
   under a fresh correlation id; the request goes out with the engine's next
   reply; the kernel's answer resumes it. The exchange is Limen's `Http`
   effect, device storage its `Storage` effect, and what Limen's core lacks
   (this tab's session storage, leaving for the provider over https,
   replacing the address within this origin, a token-free broadcast between
   tabs) is Chrona's `chrona.host` capability pack (`web-kernel/host.js`).
   The browser runtime has one thread, so the whole exchange of messages
   runs synchronously inside one `App.step`; a finished operation becomes an
   engine message (`IdentityChanged`).
3. **The engine decides, the edge carries.** The engine holds the sign-in
   state (configuring, local only, misconfigured, sign-in required, signed
   in), the retention choice (this page, the default; or this tab) and the
   notice codes; it gates every work event until someone may work, and
   signing out rebuilds the model so nothing of the person stays in memory.
4. **Identity is the provider's.** The actor is `github:<subject>` (GitHub's
   stable numeric id); the login is only a display name. Nothing is typed.
5. **Tokens stay in Fides.** Chrona never reads a token. Arca receives the
   client's token provider through `Fides.Arca.TokenBridge`, never a token.
6. **Configuration is a document beside the page.** `chrona.deployment.json`
   (`Chrona.Domain.Deployment`) names the environment, the data location
   and, optionally, the exchange origin, the registered application, the
   provider, the public client id and the redirect URI. It is closed, every
   address must be https (http only on this machine), and there is no place
   for a secret. Without an `identity` section the deployment is a local
   session; a document that cannot be read or used stops the page with a
   visible reason. The repository's copy is the local one; a deployment
   replaces it.

## Consequences

- Tests run the real Fides client against fakes: a fake exchange speaking
  Fides' wire protocol in .NET (`SignInTests`), and Playwright routes for the
  configuration, the exchange and GitHub's authorize page in a real browser
  with the WASM engine (`app-sign-in.spec.js`). No Fides test fake is a
  published package, so the fakes are Chrona's.
- A Fides exchange, a GitHub App and the deployment's document are needed for
  real sign-in. The end-to-end check against them is its own work item.
- Organization membership is not decided here: a signed-in session works in
  the deployment's single organization until WI-0030.

## Revisit when

Fides publishes a Limen-native client or a test fake; Limen's core gains
session storage, external navigation or broadcast effects; or a deployment
host needs configuration other than a static document.
