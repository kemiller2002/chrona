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
    EXE-20261008T125405928Z-c10c8445:
      operations: [modified]
      at: 2026-10-08T13:16:03.773Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Document the organization section and storage behaviour (WI-0032)"
    EXE-20261008T132359552Z-11bd758b:
      operations: [modified]
      at: 2026-10-08T13:35:19.911Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Organizations as a list, and people (WI-0031)"
    EXE-20261008T134532885Z-23ecf533:
      operations: [modified]
      at: 2026-10-08T13:50:50.909Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Bootstrap administrators and the dedicated-repository recommendation (WI-0053)"
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
  "identity": {
    "exchange": "https://fides.acme.example",
    "application": "chrona-production",
    "provider": "github",
    "clientId": "Iv23li...",
    "redirectUri": "https://chrona.acme.example/"
  },
  "organizations": [
    { "id": "org_acme", "displayName": "Acme Consulting", "slug": "acme", "timeZone": "America/New_York",
      "administrators": [ "583231" ] },
    { "id": "org_eu", "displayName": "Acme Europe", "slug": "acme-eu", "timeZone": "Europe/Berlin",
      "administrators": [ "583231", "1001" ],
      "location": { "owner": "acme-eu", "repository": "chrona-eu", "branch": "main", "basePath": "" } }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `environment` | yes | `local`, `test`, `staging` or `production`. Production data is never initialized into a public repository without a recorded decision (CHX-027). |
| `environmentName` | yes | A display name for the environment. |
| `location` | no | Where Chrona's data lives (Arca): Chrona owns `<basePath>/chrona` in that repository and nothing else. Use a repository of its own where Chrona's data needs its own permissions (CHX-DATALOC-004). |
| `identity` | no | Sign-in through Fides. Without it the deployment is a local session: one person, in one browser tab. |
| `identity.exchange` | yes | The Fides exchange's origin: https, no path. |
| `identity.application` | yes | The application id registered with the exchange. |
| `identity.provider` | yes | `github`. |
| `identity.clientId` | yes | The GitHub App's public client id. |
| `identity.redirectUri` | yes | The page GitHub returns to, exactly as registered (the Chrona page's address). |
| `organizations` | with `location` | The organizations the deployment serves, the default first. Each has an `id` (immutable; it names the organization's folder), `displayName`, `slug` (lower case), `timeZone` (IANA), its bootstrap `administrators` (GitHub numeric account ids; see People) and, optionally, a `location` of its own, for example to give its data its own permissions (CHX-DATALOC-004). |

A `location` needs `identity` (someone signed in must write the data) and at
least one organization; organization ids are unique.

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

## Storage

With a `location`, the records open after sign-in. Chrona reads the
repository with the person's GitHub credential and refuses to start: when
it cannot read or write the repository, when the data branch is missing or
does not accept direct changes, and, in production, when the repository is
public (CHX-027). On first use it sets up `<basePath>/chrona` and the
organization's folder `<basePath>/chrona/datasets/<id>`, with the
organization's manifest. Every change is one commit, made only if the
repository is still as it was read; when it moved, Chrona reloads and
decides again, keeping others' independent changes and refusing a change
that no longer fits (CHX-210).

**Use a repository of Chrona's own.** That is the recommended setup. "Moved"
means any commit to the repository, not only Chrona's: in a repository
shared with other applications or other content, every one of their commits
makes Chrona's next save reload and decide again. That is safe, but slower
and uses more of the GitHub rate limit. A dedicated repository also gives
Chrona's data its own permissions (CHX-DATALOC-004). Records edited outside Chrona are held and
listed under More (CHX-410). The person's GitHub account needs write access
to the repository; who may do what inside Chrona is its own roster
(capabilities), separate from GitHub's permissions.

## People

The configuration is the root of trust. Each organization lists its
bootstrap `administrators` by GitHub numeric account id (a person can find
theirs at `https://api.github.com/users/<login>`, or on Chrona's page when
they are not yet a member). Only a listed account can set an organization
up, and it becomes the first administrator; opening an organization first
grants nothing. With no administrators listed, an organization is not set
up; production says so plainly. Only an explicitly `local` environment that
lists no one keeps the first person to open an organization as its
administrator.

An organization whose roster has no administrator who is also listed (for
example one set up before this rule) is held: nothing in it can be done,
and nothing is granted, until a listed account signs in and confirms itself
as administrator.

Anyone else who signs in is told their GitHub account number and asked to
pass it to an administrator, who adds them under More, People, with the
access they need (keeping their own time, reviewing the organization's
time, or administering it). Rosters are stored in the organization's
folder, so the same people can work from any device. With several
organizations, a person chooses which to work in under More.

To make it work for real, a deployment needs a Fides exchange (see Fides'
`docs/hosting/AWS.md`) with this application registered (its origin and
redirect URI), and a GitHub App whose client id is `identity.clientId`.
