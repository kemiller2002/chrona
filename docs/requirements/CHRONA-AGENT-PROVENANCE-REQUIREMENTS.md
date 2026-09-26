# Chrona Agent Execution Provenance Requirements

Status: **Required**
Application: **Chrona**
Work item: `WI-0007`
Date: 2026-09-26
Refines: `CHRONA-REQUIREMENTS-EXPANSION.txt` §3 (principals), §5 (source/provenance),
§13 (split/merge lineage), §17 (Summa publication), §18 (receiver-owned contract),
§19 (observation processing), §25 (audit), §45 (architectural boundary)

## 1. Purpose and scope

When information about time or work originates from agent activity, Chrona must
keep the identity of the agent execution that produced it, so the chain
*agent execution → work item → recorded time → cost/elapsed-time analysis* can
be followed later without anyone's memory.

The identity and provenance shapes are owned by Praxis, not by Chrona. This
document does not restate them; it states what Chrona does with them:

- Praxis actor: `RQ-ROS-2026-A001` (`schemas/provenance-actor.schema.json`).
- Praxis execution identity: `RQ-ROS-2026-A002` (`EXE-<timestamp>-<random>`).
- Praxis provenance interchange record: `RQ-ROS-2026-A013`
  (`praxis.provenance-record`, semantic version, major 1).
- Execution propagation to other systems: `RQ-ROS-2026-A014`
  (`ROS_EXECUTION_ID`; otherwise `EXE-<system>.<run>`).
- No silent stripping of provenance: `RQ-ROS-2026-A015`.

Requirement IDs in this document use the prefix `CHR-PROV-`. The keywords MUST,
MUST NOT, SHOULD, and MAY are normative.

## 2. Requirements

### CHR-PROV-001 Origin fields on agent-derived observations

A `TimeObservation` derived from a Praxis (or other Echelon) execution MUST be
able to carry an **origin**:

- `originActor`: the Praxis actor, exactly as supplied (key order `kind`, `id`,
  `provider`, `model`, `runtime`; unknown fields preserved);
- `originExecution`: the execution that produced the observation: a Praxis
  `EXE-<timestamp>-<random>`, a foreign `EXE-<system>.<run>`, or, for a
  non-agent actor acting outside any run, a `CTB-...` key;
- `provenance` (optional): a Praxis provenance interchange record, carried
  verbatim;
- `sourceSystem`: the namespaced system that supplied the observation.

An origin whose actor kind is `agent` MUST have an `originExecution` keyed
`EXE-...`. The wire form is the receiver-owned contract
`contracts/time-observation-origin.v1.schema.json` (CHR-PROV-008).

### CHR-PROV-002 Shape validation only; no dependency on Praxis

Chrona MUST validate the origin's **shape** at the boundary (structure, actor
rules, key syntax, and, when a provenance record is present, that it is a
well-formed record in which `originExecution` is a contribution attributed to
`originActor`). Chrona MUST NOT call, resolve, or require the availability of
Praxis to accept, store, review, publish, or report time. Chrona MUST NOT
reference Praxis code or packages; the contract is re-implemented locally.

### CHR-PROV-003 Never invent origin values

Chrona MUST NOT fabricate, infer, or complete an origin: not from Git
authorship, commit text, work-item assignees, session state, or the identity of
the reviewer. Absent attributes stay absent or the literal `unknown` exactly as
supplied. Chrona MUST NOT mint a Praxis-shaped `EXE-<timestamp>-<random>` key.
When Chrona itself runs an import job that needs a run key, it MUST use
`EXE-chrona.<run>`. Chrona MUST NOT fabricate time records to represent agent
activity: an origin describes an observation; it does not create one.

### CHR-PROV-004 Malformed origins are rejected, not dropped

An observation whose origin (or carried provenance record) is malformed MUST be
classified as an **invalid payload** under §19, distinguishable from a valid
rejected observation, with a stable diagnostic. Chrona MUST NOT silently strip
the origin and continue. A provenance record in an unsupported major version is
not malformed: it MUST be stored and forwarded verbatim, never interpreted,
modified, or extended (`RQ-ROS-2026-A013`).

### CHR-PROV-005 Identity is not authorization; review still applies

An origin is self-reported provenance. It MUST NOT be treated as
authentication, authorization, evidence of the work, or a reason to trust the
duration. Agent-originated observations MUST follow the §19 default: they enter
as `Pending` and require human review or an explicit source policy before they
become authoritative recorded time. A policy MUST NOT auto-accept an observation
merely because it carries an origin. Chrona owns the decision that a fact
becomes authoritative recorded time (§45); the reviewer who accepts it is
recorded as the reviewer, never as the origin, and the origin actor is never
recorded as the approver.

### CHR-PROV-006 Origin survives acceptance, split, and merge (lineage)

An accepted activity MUST retain the origin of each observation it came from as
an `originLineage` entry `{ "from": <ObservationId or source ActivityId>,
"origin": <origin verbatim> }`, in addition to the §19 SourceSystem /
ObservationId / WorkItemId retention. On split (§13), every child MUST carry all
of the parent's lineage entries verbatim. On merge (§13), the merged activity
MUST carry the union of its sources' lineage entries verbatim, each still tied
to its own source. Lineage MUST NOT invent, overwrite, collapse, or re-attribute
an entry: a merged activity has several origins, not a new merged author.
Amending an activity MUST NOT change its lineage entries.

### CHR-PROV-007 Origin appears in audit

Every §25 audit entry for a material transition of an activity that has lineage
MUST reference that activity's `originLineage` (verbatim or by stable reference
to the revision that holds it), separately from the audit `actor`, which is the
principal (§3: Human, Agent, Service, Integration) performing the transition.
Import accept/reject audit entries MUST record the observation's origin even
when the observation is rejected.

### CHR-PROV-008 Receiver-owned, versioned origin contract

The origin wire form is a receiver-owned contract (§18):
`chrona.time-observation-origin`, semantic version, schema
`contracts/time-observation-origin.v1.schema.json`. Chrona accepts any minor of
a supported major, preserves fields it does not model, and treats an
unsupported major as an invalid payload for that observation (the payload
itself is preserved for diagnosis). The future `Chrona.Integration` assembly
MUST serialize to this schema and pass the same fixtures.

### CHR-PROV-009 Publication to Summa keeps origin

When approved billable time is published to Summa (§17), the publication MUST
include the activity's `originLineage` entries verbatim, or state explicitly
that no origin was recorded. Chrona MUST NOT substitute the approver, the
publisher, or the Chrona service identity for the origin actor.

### CHR-PROV-010 No secrets

Origins, lineage, and audit entries MUST NOT carry credentials. An origin
containing a value shaped like a credential is an invalid payload
(CHR-PROV-004).

## 3. Traceability

| Requirement | Implementation (contract tier) | Verification |
|---|---|---|
| CHR-PROV-001, 008 | `contracts/time-observation-origin.v1.schema.json` | `contracts/test/time-observation-origin.test.mjs` (valid fixtures) |
| CHR-PROV-002, 004, 010 | `contracts/time-observation-origin.mjs` (`originProblems`), `contracts/praxis-provenance/praxis-provenance-record.mjs` | invalid fixtures; `contracts/test/praxis-provenance-conformance.test.mjs` |
| CHR-PROV-003 | `originProblems` refuses agent origins without an `EXE-` execution | `invalid/agent-without-execution.json`, `valid/unknown-attributes.json` |
| CHR-PROV-005 | Requirement only; review workflow not yet implemented | Pending: §43 scenarios 25–27 must assert `Pending` for agent origins |
| CHR-PROV-006 | `lineageProblems` (split/merge preservation check) | `contracts/fixtures/time-observation-origin/v1/lineage/*` |
| CHR-PROV-007, 009 | Requirement only; audit and publication not yet implemented | Pending with §25 and §17 implementation |

The JavaScript in `contracts/` is a contract-verification harness, not product
code: Chrona's domain and integration logic remains F# (§0.4). When
`Chrona.Integration` exists it MUST pass the same fixtures.

The vendored Praxis conformance fixtures live in
`contracts/fixtures/praxis-provenance-record/`; `SOURCE.json` pins the Praxis
commit and a SHA-256 per file. Run `npm run test:contracts`.
