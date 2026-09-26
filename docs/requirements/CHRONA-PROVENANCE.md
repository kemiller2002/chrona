---
id: CHR-PROV
title: Chrona Agent Provenance and Execution Identity Requirements
status: required
version: 1.2.0
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
    EXE-20260926T085502873Z-f8c6e57e:
      operations: [modified]
      at: 2026-09-26T08:58:32.113Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Contract revision 1.1: time.record creates a record; received provenance is source provenance"
    EXE-20260926T094829454Z-8f0e8fc4:
      operations: [modified]
      at: 2026-09-26T20:45:15.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Contract revision 1.2: checked lineage, per-code-point key escaping, text classification, one identity source"
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
`b0037183389c8b9392919f58521b9487d1b4d5c6` of `kemiller2002/praxis` (contract revision 1.2):

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

### CHR-PROV-006 time.record creates a record; received provenance is source provenance

`time.record` is a create operation (Praxis `docs/echelon-provenance-architecture.md`,
"Creating a record versus relaying one"; `DF-ROS-2026-A037`). The new
observation record MUST get its own `praxis.provenance/1` block in which the
invoking actor is `created`, plus `measured` when it is the performer that
recorded the time, keyed by its execution (or `EXT-op.<operationId>`). The
invoking actor comes from an explicit declaration (execution envelope, CLI
flags, `ROS_ACTOR_KIND`/`ROS_ACTOR`/`ROS_TELEMETRY_*`, `ROS_EXECUTION_ID`;
`RQ-ROS-2026-A016`) and is recorded as `unknown` when none was made. The new
block's `derivedFrom` is the received block's `derivedFrom` plus the source
reference `<sourceSystem>:observation/<observationId>`. The received block is
stored verbatim as `sourceProvenance` and MUST NOT be appended to, so a
source's contributors never appear as authors of the Chrona record.
Candidate and entry are later states of the same Chrona record: Chrona adds
its own `transformed` contribution as automation `echelon/chrona` keyed
`EXT-chrona.<operationId>`, and accepting a candidate records the reviewer as
`approved`, which never transfers authorship (`RQ-ROS-2026-A014`). Every
write uses only the reference library's result; a refusal (credential,
back-dated contribution, re-attribution, unknown actor extending a known
entry) is returned as a structured error and nothing is stored.

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

### CHR-PROV-011 Contract revision 1.1 validation

Chrona's own validation MUST follow Praxis contract revision 1.1: timestamps
must be calendar-valid (year 0001-9999, no February 30, no `24:00`) and are
ordered at millisecond precision; a field present with JSON `null` is invalid,
never absent; keys derived from operation ids use the reference's injective
escaping (`op 1` -> `EXT-op.op_201`; revised by CHR-PROV-012).

### CHR-PROV-012 Contract revision 1.2

Chrona MUST follow Praxis contract revision 1.2 (`docs/agent-provenance.md`,
"Contract revision 1.2"):

1. **Lineage is checked.** Every lineage Chrona adds, including lineage derived
   from the request payload (the received block's `derivedFrom` and the
   `<sourceSystem>:observation/<observationId>` reference), goes through the
   vendored `addLineage`, which returns `{ ok, block }`. A refusal (credential,
   blank, unpaired surrogate, or a result that would not classify as
   supported) rejects the request with `lineage-refused`; nothing is stored and
   the lineage is never dropped silently.
2. **Key segments.** Operation-derived keys (`EXT-op.<id>`, `EXT-chrona.<id>`)
   use the vendored `escapeKeySegment`: per Unicode code point, everything but
   ASCII letters, digits and `-` becomes `_xx` per UTF-8 byte, so `.`, `_` and
   characters outside the BMP are escaped injectively. An id that is empty or
   holds an unpaired surrogate cannot form a key and rejects the request
   (`invalid-request`). Keys already stored are never rewritten.
3. **Text.** An observation received as JSON text (a file, stdin, or a request
   body) is read with `receiveTimeObservationText`, which classifies the text
   with the vendored `classifyText` before parsing: a repeated member name or an
   unpaired surrogate anywhere rejects it (`malformed-text`).
4. **ASCII whitespace.** "Blank" means empty after trimming tab, LF, VT, FF, CR
   and space only; U+0085, U+FEFF, U+001C, U+00A0 are content.
5. **Timestamps** read from a request use the vendored strict `parseTimestamp`
   (no offsets, space separators, or date-only forms).
6. **One identity source** (`RQ-ROS-2026-A016` 1.2.0). Chrona does not resolve
   identity itself; the caller passes the invoker resolved from one source,
   with Summa's `resolveRequester` as the reference behaviour: an explicit
   declaration replaces the environment wholly and does not inherit
   `ROS_EXECUTION_ID`, which is honoured only with an identity declared in the
   same environment.
7. **Null.** A stored `"provenance": null` is malformed, not absent: accepting
   such a candidate is refused, never started from an empty block.

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
| CHR-PROV-005 | `receiveTimeObservation` (`classify`, credential tripwire) | malformed received provenance rejects …; credential-like values …; unsupported provenance major is carried verbatim … |
| CHR-PROV-006 | `receiveTimeObservation` (new block, `sourceProvenance`), `candidateFromObservation`, `acceptCandidate` | time.record creates a new record …; a received block is source provenance …; regression: reviewer token / back-dated / re-attribution / unknown extension refused; replaying the same request is deterministic |
| CHR-PROV-007 | candidate/entry fields; `toInterchange` | agent execution keeps EXE and actor …; round trip: export classifies as supported … |
| CHR-PROV-008 | `.github/workflows/readiness-work.yml` | workflow review (declares `ROS_ACTOR_KIND=automation`, no agent actor) |
| CHR-PROV-009 | `vendor/praxis-provenance/` + `SOURCE.json` | `tests/provenance/vendored.test.mjs` (SHA-256, 70 conformance cases, text, envelope-key and lineage cases, echelon chain) |
| CHR-PROV-010 | no actor-based decision exists in the intake | legacy observation … unattributed; review of `lib/time-observation-intake.mjs` |
| CHR-PROV-011 | `parseTimestamp` (vendored), null checks, `invokerKey`/`chronaKey` escaping | contract 1.1: calendar-invalid timestamps and null values …; operation-derived keys are escaped injectively; 56 vendored cases |
| CHR-PROV-012 | `addLineage` result handling, `escapeKeySegment` (`invokerKey`, `chronaKey`), `receiveTimeObservationText` (`classifyText`), ASCII `isNonEmptyString`, `acceptCandidate` null handling | `time-observation.test.mjs`: contract 1.2 finding 6 / rule 1 / 2 / 3 / 4 / 6 / timestamps; `vendored.test.mjs`: text, envelope-key and lineage cases |

## Revision history

- **1.2.0** (2026-09-26, FEAT-ECHELON-PROVENANCE-R12): Praxis contract revision
  1.2 (`b0037183`). Added CHR-PROV-012 (checked lineage, including lineage from
  the payload; per-code-point key escaping replacing Chrona's local escaper,
  review finding 6; text classification; ASCII whitespace; strict request
  timestamps; one identity source; stored null is malformed). CHR-PROV-009
  now covers the 70 cases and the three new fixtures.
- **1.1.0** (2026-09-26, FEAT-ECHELON-PROVENANCE-R11): contract revision 1.1;
  CHR-PROV-006 revised, CHR-PROV-011 added.
- **1.0.0** (2026-09-26, FEAT-ECHELON-PROVENANCE): initial requirements.

Run: `npm test` (alias of `npm run test:provenance`).
