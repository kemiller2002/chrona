---
id: CHR-PROV
title: Chrona Agent Provenance and Execution Identity Requirements
status: required
version: 1.0.0
owners:
  - chrona
created: 2026-09-26
updated: 2026-09-26
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt
  - docs/requirements/ECHELON-SHARED-APPLICATION-FOUNDATIONS.md
  - schemas/time-observation.schema.json
  - lib/time-observation-intake.mjs
  - vendor/praxis-provenance/SOURCE.json
tags: [provenance, identity, time-observation, echelon]
provenance:
  contributions:
    EXE-20260926T081042649Z-0330d2e5:
      operations: [created]
      at: 2026-09-26T08:16:51.682Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Chrona provenance requirements for the Echelon provenance upgrade"
---

# Chrona Agent Provenance and Execution Identity Requirements

Status: **Required**. These requirements refine §3 (principals), §5
(authoritative activity record: ActorId, source/provenance), §17 (Summa
publication), §19 (TimeObservation -> TimeCandidate -> ProcessingReceipt), and
§25 (audit and provenance) of
[`CHRONA-REQUIREMENTS-EXPANSION.txt`](CHRONA-REQUIREMENTS-EXPANSION.txt) for
time and work information that originates from agent activity.

## Authority

Praxis is authoritative for identity and provenance. Chrona adopts, and does
not restate or redefine, the Praxis contract at commit
`a42c44e8ae0e6e16fdd513141460b700e5fa6648` of `kemiller2002/praxis`:

- `docs/agent-provenance.md` (actor, execution, contribution, lineage, unknown, legacy);
- `DF-ROS-2026-A036` (agent identity and provenance) and `DF-ROS-2026-A037`
  (Echelon provenance interchange, `praxis.provenance/1`, execution envelope v2);
- `RQ-ROS-2026-A001` through `RQ-ROS-2026-A019`.

Where this document and the Praxis contract appear to differ, the Praxis
contract wins and this document is corrected.

## Requirements

### CHR-PROV-001 Praxis identity, not a second identity model

Chrona MUST record who performed time-bearing work as a Praxis actor
(`kind`, `id`, and, for non-humans, `provider`/`model`/`runtime`, literal
`"unknown"` when not known) (`RQ-ROS-2026-A001`). Chrona MUST NOT introduce a
separate identity schema for agents, services, or integrations.

### CHR-PROV-002 Execution identity is preserved

When a TimeObservation, TimeCandidate, or accepted time entry originates from
agent or automated activity, Chrona MUST preserve the performing execution: a
Praxis execution id (`EXE-...`) or another Echelon system's execution
(`EXT-<system>.<run-id>`) (`RQ-ROS-2026-A002`, `RQ-ROS-2026-A013`), on the
record (`performer.execution`) and in its embedded `praxis.provenance/1` block
(`provenance`) whose contributions are keyed by execution. Two executions of
the same agent MUST remain distinct. An unknown execution is recorded as
absent and MUST NOT be invented; when only the operation is known the
contribution key is `EXT-op.<operationId>` (`DF-ROS-2026-A037`).

### CHR-PROV-003 Principal model maps explicitly to the Praxis actor

Chrona's §3 principal kinds map to Praxis actor kinds as follows, and only as
follows: Human -> `human`; Agent -> `agent`; Service -> `automation`;
Integration -> `automation`, each with the principal's stable id as the actor
`id`. An `automation` actor is a Service or an Integration according to
Chrona's own principal record; provenance does not decide which. An `unknown`
actor is no principal.

### CHR-PROV-004 Time is never fabricated

Chrona MUST NOT synthesize a time entry from provenance, from the existence of
an execution, from commit timestamps, or from any other indirect signal.
Elapsed time MUST come from recorded duration evidence: a timer, a manual
entry, execution telemetry, or an import (`measurement.source`). An
observation without duration evidence, or whose performer is unknown, is
preserved and yields disposition `NeedsAttention`; it produces no candidate or
time entry. An observed zero is a real value; an estimate is not accepted.

### CHR-PROV-005 Receiving provenance

At the time.record v1 boundary Chrona MUST classify an observation's
`provenance` block per `RQ-ROS-2026-A015`: `supported` blocks are preserved
(including unknown fields and tolerated operation codes); `unsupported` majors
are carried verbatim through candidate and entry and never merged into;
`malformed` blocks, and credential-like values anywhere in the observation
(`RQ-ROS-2026-A017`), reject the observation with a structured error that is
distinguishable from a valid rejected observation (§19).

### CHR-PROV-006 Appending, never re-attributing

When the invoking actor is explicitly declared (execution envelope, CLI flags,
`ROS_ACTOR_KIND`/`ROS_ACTOR`/`ROS_TELEMETRY_*`, `ROS_EXECUTION_ID`;
`RQ-ROS-2026-A016`), Chrona appends that actor's contribution (`created` on an
empty history, otherwise `measured`) under its execution key, following the
Praxis append rules (`RQ-ROS-2026-A004`). A transporting actor MUST NOT
overwrite the original actor or re-attribute an execution. Replaying the same
operation is idempotent. When Chrona changes representation
(observation -> candidate) it MAY add its own `transformed` contribution as
automation `echelon/chrona` keyed `EXT-chrona.<operationId>` with lineage
`chrona:observation/<id>`; accepting a candidate records the reviewer as
`approved`, which never transfers authorship (`RQ-ROS-2026-A014`).

### CHR-PROV-007 Traceability for later analysis

Accepted time entries MUST retain SourceSystem, ObservationId, optional
WorkItemId, performer actor, and performer execution so that agent execution
-> work item -> elapsed time/cost can be analysed later
(`RQ-ROS-2026-A011`). Publication to Summa (§17) carries the entry's
provenance block unchanged.

### CHR-PROV-008 Automation is not an agent

Repository automation (GitHub Actions) MUST NOT declare itself as an agent.
It is recorded as `automation` (`RQ-ROS-2026-A001`, `RQ-ROS-2026-A006`;
agents never impersonate another actor, `RQ-ROS-2026-A012`). Historical
records that declared otherwise are left unchanged: history is never
rewritten or backfilled (`RQ-ROS-2026-A003`, `RQ-ROS-2026-A007`).

### CHR-PROV-009 Independence and conformance

Chrona MUST work without Praxis installed (`RQ-ROS-2026-A018`). The Praxis
reference library, schemas, and conformance fixtures are vendored unchanged
with their SHA-256 recorded; tests verify the hashes, every conformance case,
and the Echelon end-to-end chain.

### CHR-PROV-010 Provenance is not authority

A recorded actor MUST NOT grant, deny, or weight approval, publication, or
billing (`RQ-ROS-2026-A019`). Capability checks (§3) remain separate.

Legacy observations and activities without provenance remain valid and read as
`unattributed`; nothing is inferred or backfilled (`RQ-ROS-2026-A003`,
`RQ-ROS-2026-A007`).

## Traceability

| Requirement | Implementation | Tests |
|---|---|---|
| CHR-PROV-001 | `schemas/time-observation.schema.json` (`performer.actor` refs the vendored Praxis actor schema); `receiveTimeObservation` | `tests/provenance/time-observation.test.mjs`: invalid observations …; `vendored.test.mjs`: schema references only the vendored Praxis schemas |
| CHR-PROV-002 | `performer.execution`; `invokerKey`; `candidateFromObservation`; `acceptCandidate` | agent execution keeps EXE and actor …; two executions of one agent stay distinct |
| CHR-PROV-003 | `principalToActor`, `principalKindsFor` | principal model maps onto the Praxis actor …; automation (CI) performer …; human-entered time … |
| CHR-PROV-004 | `recordedDurationSeconds`, `candidateFromObservation` (NeedsAttention) | no time entry is fabricated …; guessed or unsourced durations are rejected; unknown performer … |
| CHR-PROV-005 | `receiveTimeObservation` (`classify`, credential tripwire) | malformed provenance rejects …; credential-like values …; unsupported provenance major is carried verbatim … |
| CHR-PROV-006 | `receiveTimeObservation`, `candidateFromObservation`, `acceptCandidate` | upstream block is preserved …; transporting actor can never re-attribute …; replaying the same operation is idempotent; human correction of agent time … |
| CHR-PROV-007 | candidate/entry fields; `toInterchange` | agent execution keeps EXE and actor …; round trip: export classifies as supported … |
| CHR-PROV-008 | `.github/workflows/readiness-work.yml` | workflow review (declares `ROS_ACTOR_KIND=automation`, no agent actor) |
| CHR-PROV-009 | `vendor/praxis-provenance/` + `SOURCE.json` | `tests/provenance/vendored.test.mjs` (SHA-256, 40 conformance cases, echelon chain) |
| CHR-PROV-010 | no actor-based decision exists in the intake | legacy observation … unattributed; review of `lib/time-observation-intake.mjs` |

Run: `npm test` (alias of `npm run test:provenance`).
