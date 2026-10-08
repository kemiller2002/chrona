---
id: CHX-DATALOC
title: Chrona data location, application-owned namespace and permission separation
status: accepted
version: 1.0.0
created: 2026-10-08
updated: 2026-10-08
owners:
  - chrona
related_documents:
  - docs/requirements/implementation-gap-analysis.md
tags: [requirements, storage, data-location, arca]
provenance:
  contributions:
    EXE-20261008T074345758Z-6c5a1aa8:
      operations: [created]
      at: 2026-10-08T07:44:42.077Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record user decisions of 2026-10-08 and the per-application data-location requirement"
---

# CHX-DATALOC — data location, namespace and permission separation

These requirements come from user decisions of 2026-10-08. They apply to
Chrona and to every Echelon application that stores data through Arca
(`kemiller2002/arca`, requirements `ARCA-LOC-001..010`, decision
`DF-ARCA-2026-0002`).

**CHX-DATALOC-001 Configurable data location.** The data repository (owner,
repository, branch, base path) MUST be configured per deployment. Chrona
MUST NOT hard-code a repository, owner, branch or root path.

**CHX-DATALOC-002 Application-owned namespace.** Chrona MUST create and own its
own folder structure (namespace) in the configured repository and keep every
read and write inside it.

**CHX-DATALOC-003 Shared repositories.** Chrona MUST NOT assume it is the only
application using the repository, or that it owns the repository root.

**CHX-DATALOC-004 Separable permissions.** Chrona's data MUST be separable from
other applications' data under different permissions (Chrona and Summa, for example,
have different permissions). GitHub permissions apply per repository, not
per folder, so separation is achieved by pointing Chrona at **its own
repository** through CHX-DATALOC-001. A shared repository is allowed only when the
applications in it may share permissions.

**CHX-DATALOC-005 No at-rest encryption for now.** Chrona MUST NOT add
application-level at-rest encryption. Per-application encryption is a
deferred, possible future Arca item, not a requirement.

Related Chrona requirements: CHX-024 (2.4 multi-application repositories), CHX-025 (2.5 organization isolation, "different organizations to use different repositories"), CHX-027 (2.7 production repository safety).
