---
id: CHRONA-DEPLOYMENT-CONFIGURATION
title: Configuring a Chrona deployment
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - chrona
related_documents:
  - docs/requirements/CHRONA-DATA-LOCATION.md
  - research/decisions/DF-CHRONA-2026-0004--sign-in-through-fides-client-over-limen-requests.md
tags: [deployment, configuration, identity, fides, storage, arca]
provenance:
  contributions:
    EXE-20261008T121136332Z-c6255241:
      operations: [created]
      at: 2026-10-08T12:33:12.572Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Sign-in through Fides and the deployment's configuration document (WI-0029)"
---

# Configuring a Chrona deployment

A deployment configures Chrona with one document, `chrona.deployment.json`,
served beside the page (`web/chrona.deployment.json` in this repository).
The page reads it when it starts. Chrona hard-codes no repository, owner,
branch, base path, sign-in host or client id (CHX-DATALOC-001, CHX-022).
The repository's copy is a local deployment; a deployment replaces it.

```json
{
  "environment": "production",
  "environmentName": "production",
  "location": { "owner": "acme", "repository": "chrona-data", "branch": "main", "basePath": "deployments/prod" },
  "organizations": { "org_eu": { "owner": "acme-eu", "repository": "chrona-eu", "branch": "main", "basePath": "" } },
  "identity": {
    "exchange": "https://fides.acme.example",
    "application": "chrona-production",
    "provider": "github",
    "clientId": "Iv23li...",
    "redirectUri": "https://chrona.acme.example/"
  }
}
```

| Field | Required | Meaning |
|---|---|---|
| `environment` | yes | `local`, `test`, `staging` or `production`. Production data is never initialized into a public repository without a recorded decision (CHX-027). |
| `environmentName` | yes | A display name for the environment. |
| `location` | no | Where Chrona's data lives (Arca): Chrona owns `<basePath>/chrona` in that repository and nothing else. Use a repository of its own where Chrona's data needs its own permissions (CHX-DATALOC-004). |
| `organizations` | no | Organizations whose data lives in another repository, by OrganizationId. Needs `location`. |
| `identity` | no | Sign-in through Fides. Without it the deployment is a local session: one person, in one browser tab. |
| `identity.exchange` | yes | The Fides exchange's origin: https, no path. |
| `identity.application` | yes | The application id registered with the exchange. |
| `identity.provider` | yes | `github`. |
| `identity.clientId` | yes | The GitHub App's public client id. |
| `identity.redirectUri` | yes | The page GitHub returns to, exactly as registered (the Chrona page's address). |

The document is closed: a field Chrona does not define is refused. Every
address must be https; plain http is accepted only for `localhost`,
`127.0.0.1` and `[::1]`, for development. Nothing in it is secret, and there
is no place for a secret: the GitHub App's client secret stays with the
exchange. A document that cannot be read or used stops the page with the
reason; nothing runs for anyone.

## Sign-in

With an `identity` section the page asks the person to sign in with GitHub.
They choose where their token is kept: in the page only (the default; a
reload signs them out) or until the tab closes. Their identity is GitHub's
(`github:<numeric id>`, shown by login); nothing is typed. Signing out
clears the token from the tab and revokes it at GitHub (CHX-023).

To make it work for real, a deployment needs a Fides exchange (see Fides'
`docs/hosting/AWS.md`) with this application registered (its origin and
redirect URI), and a GitHub App whose client id is `identity.clientId`.
