// Chrona time.record v1 intake: the provenance portion of
// TimeObservation -> TimeCandidate -> accepted time entry.
//
// Requirements: docs/requirements/CHRONA-PROVENANCE.md (CHR-PROV-001..011),
// which reference Praxis RQ-ROS-2026-A001..A019 and DF-ROS-2026-A037
// (contract revision 1.1). Identity is the Praxis actor and provenance is
// the Praxis praxis.provenance/1 block; the receiving and appending rules
// come from the vendored, unchanged Praxis reference library. Chrona adds no
// second identity model and has no runtime dependency on Praxis.
//
// time.record is a CREATE (Praxis docs/echelon-provenance-architecture.md,
// "Creating a record versus relaying one"): the new observation record gets
// its OWN block, in which the invoking actor is `created` (and `measured`
// when it is the performer that measured the time), keyed by its execution.
// The block received with the request describes the source; it is stored
// verbatim as `sourceProvenance` and is never appended to. Candidate and
// entry are later states of the same Chrona record, so they continue the
// record's own block (Chrona `transformed`, reviewer `approved`).
//
// Every write goes through the reference library's appendContribution and
// uses only its result; a refusal is surfaced as a structured error.
// Every function is pure: inputs are never mutated; nothing reads a clock,
// the environment, or the filesystem.

import {
  SCHEMA_TAG, classify, appendContribution, addLineage, actorProblems, actorsAgree,
  credentialFindings, emptyBlock, keyFromEnvelopeV1, parseTimestamp,
} from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

export const OBSERVATION_SCHEMA = "chrona.time-observation/1";
const OBSERVATION_SCHEMA_PATTERN = /^chrona\.time-observation\/([1-9][0-9]*)$/;
const EXECUTION = /^(EXE-[A-Za-z0-9._-]+|EXT-[a-z][a-z0-9-]*\.[A-Za-z0-9._-]+)$/;
const MEASUREMENT_SOURCES = Object.freeze(["timer", "manual-entry", "telemetry", "import"]);
const UNKNOWN = "unknown";

/** Chrona's own identity when it changes a record's representation (DF-ROS-2026-A037 receiver rule). */
export const CHRONA_ACTOR = Object.freeze({ kind: "automation", id: "echelon/chrona", provider: "echelon", model: UNKNOWN, runtime: "chrona" });

/** The invoking actor when none was declared: recorded, never guessed. */
export const UNKNOWN_ACTOR = Object.freeze({ kind: UNKNOWN, id: UNKNOWN, provider: UNKNOWN, model: UNKNOWN, runtime: UNKNOWN });

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const isInstant = (value) => parseTimestamp(value) !== undefined;
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
const error = (code, message) => ({ code, message });
const refused = (message) => ({ ok: false, errors: [error("provenance-refused", message)] });
const optionalExecution = (value) => value === undefined || (typeof value === "string" && EXECUTION.test(value));

// Injective run-id escaping, identical to the reference keyFromEnvelopeV1 (contract 1.1 rule 6).
const escapeRunId = (text) => String(text).replace(/[^A-Za-z0-9.-]/g, (char) =>
  [...new TextEncoder().encode(char)].map((byte) => `_${byte.toString(16).padStart(2, "0")}`).join(""));

// ---- principal model (CHR-PROV-003) -----------------------------------------

/**
 * Maps a Chrona principal (§3: Human, Agent, Service, Integration) onto the
 * Praxis actor. The Praxis actor is the identity Chrona records; the principal
 * kind is a Chrona classification, not a second identity model.
 * Non-human attributes that are not declared are the literal "unknown".
 */
export const principalToActor = (principal) => {
  if (!isObject(principal) || !isNonEmptyString(principal.id)) {
    return { ok: false, error: "a principal needs a kind and a non-empty id" };
  }
  const attributes = () => ({
    provider: isNonEmptyString(principal.provider) ? principal.provider : UNKNOWN,
    model: isNonEmptyString(principal.model) ? principal.model : UNKNOWN,
    runtime: isNonEmptyString(principal.runtime) ? principal.runtime : UNKNOWN,
  });
  switch (principal.kind) {
    case "Human": return { ok: true, actor: { kind: "human", id: principal.id } };
    case "Agent": return { ok: true, actor: { kind: "agent", id: principal.id, ...attributes() } };
    case "Service":
    case "Integration": return { ok: true, actor: { kind: "automation", id: principal.id, ...attributes() } };
    default: return { ok: false, error: `principal kind '${principal.kind}' is not Human, Agent, Service, or Integration` };
  }
};

/**
 * The Chrona principal kinds a Praxis actor can be. `automation` is either a
 * Service or an Integration; which one is Chrona's own principal record, not
 * something provenance can decide. An unknown actor is no principal.
 */
export const principalKindsFor = (actor) =>
  actor?.kind === "human" ? ["Human"]
    : actor?.kind === "agent" ? ["Agent"]
      : actor?.kind === "automation" ? ["Service", "Integration"]
        : [];

/** An actor whose performer identity Chrona cannot attribute time to. */
export const isUnknownActor = (actor) => !isObject(actor) || actor.kind === UNKNOWN || actor.id === UNKNOWN;

// ---- contribution keys (CHR-PROV-002) ---------------------------------------

/**
 * The contribution key for the invoking actor: its execution (EXE-/EXT-) when
 * known, otherwise EXT-op.<operationId> (reference escaping). Never invented.
 */
export const invokerKey = ({ execution, operationId }) =>
  isNonEmptyString(execution) ? execution : keyFromEnvelopeV1({ operationId });

/** Chrona's own key for a representation change it made during an operation. */
export const chronaKey = (operationId) => `EXT-chrona.${escapeRunId(operationId)}`;

/** The reference to the source an observation was taken from. */
export const sourceReference = (observation) => `${observation.sourceSystem}:observation/${observation.observationId}`;

// ---- validation (CHR-PROV-005, CHR-PROV-011) --------------------------------

const measurementProblems = (measurement) => {
  if (measurement === undefined) return [];
  if (!isObject(measurement)) return ["measurement must be an object (null is not absence)"];
  const source = MEASUREMENT_SOURCES.includes(measurement.source) ? [] : [`measurement.source must be one of ${MEASUREMENT_SOURCES.join(", ")}`];
  if (measurement.kind === "interval") {
    const times = [
      ...(isInstant(measurement.startedAt) ? [] : ["measurement.startedAt must be a calendar-valid RFC 3339 UTC instant"]),
      ...(isInstant(measurement.endedAt) ? [] : ["measurement.endedAt must be a calendar-valid RFC 3339 UTC instant"]),
    ];
    const order = times.length === 0 && parseTimestamp(measurement.endedAt) < parseTimestamp(measurement.startedAt)
      ? ["measurement.endedAt precedes measurement.startedAt"] : [];
    return [...times, ...order, ...source];
  }
  if (measurement.kind === "duration") {
    const seconds = typeof measurement.seconds === "number" && Number.isFinite(measurement.seconds) && measurement.seconds >= 0
      ? [] : ["measurement.seconds must be a finite number >= 0"];
    return [...seconds, ...source];
  }
  return [`measurement.kind '${measurement.kind}' is not interval or duration`];
};

// JSON null never means absent (contract 1.1 rule 3): a present optional field must be a real value.
const optionalString = (value, field) => (value === undefined || isNonEmptyString(value) ? [] : [`${field} must be a non-empty string when present (null is not absence)`]);

const observationProblems = (observation) => [
  ...(isNonEmptyString(observation.observationId) ? [] : ["observationId must be a non-empty string"]),
  ...(isNonEmptyString(observation.sourceSystem) ? [] : ["sourceSystem must be a non-empty string"]),
  ...optionalString(observation.workItemId, "workItemId"),
  ...optionalString(observation.externalReference, "externalReference"),
  ...(isInstant(observation.recordedAt) ? [] : ["recordedAt must be a calendar-valid RFC 3339 UTC instant"]),
  ...(!isObject(observation.performer) ? ["performer must be an object with the Praxis actor who performed the work"]
    : [
      ...actorProblems(observation.performer.actor, "performer.actor"),
      ...(optionalExecution(observation.performer.execution) ? [] : ["performer.execution must be EXE-... or EXT-<system>.<run-id> when present"]),
    ]),
  ...measurementProblems(observation.measurement),
];

const requestProblems = (request) => {
  if (!isObject(request)) return ["request context is required"];
  return [
    ...(isNonEmptyString(request.operationId) ? [] : ["request.operationId must be a non-empty string"]),
    ...(isInstant(request.receivedAt) ? [] : ["request.receivedAt must be a calendar-valid RFC 3339 UTC instant"]),
    ...(request.invoker === undefined ? []
      : !isObject(request.invoker) ? ["request.invoker must be an object"]
        : [
          ...actorProblems(request.invoker.actor, "request.invoker.actor"),
          ...(optionalExecution(request.invoker.execution) ? [] : ["request.invoker.execution must be EXE-... or EXT-<system>.<run-id> when present"]),
        ]),
  ];
};

/** Provenance status of a block: attributed, unattributed (absent/empty), unsupported (carried verbatim), or malformed. */
export const provenanceStatus = (block) => {
  if (block === undefined) return "unattributed";
  const verdict = classify(block).verdict;
  if (verdict !== "supported") return verdict;
  return Object.keys(block.contributions).length === 0 ? "unattributed" : "attributed";
};

// ---- time.record: create (CHR-PROV-002, CHR-PROV-005, CHR-PROV-006) ---------

/**
 * Receives a TimeObservation through time.record v1 and CREATES the Chrona
 * observation record.
 *
 * request: { operationId, receivedAt, invoker?: { actor, execution? } }
 * - `invoker` is the CURRENT invoking actor, from an explicit declaration only
 *   (execution envelope, flags, ROS_ACTOR_KIND/ROS_ACTOR/ROS_TELEMETRY_*,
 *   ROS_EXECUTION_ID). When absent it is recorded as unknown, never guessed.
 *
 * Returns { ok: false, errors } for an invalid observation, malformed received
 * provenance, or a refused write, or { ok: true, record, warnings } where
 * record = { observation, provenance, sourceProvenance?, ingestion }:
 * - `provenance`: the new record's own block (invoker `created`, plus
 *   `measured` when the invoker is the performer that recorded the time);
 *   `derivedFrom` = the received block's lineage + the source reference;
 * - `sourceProvenance`: the received block, verbatim (supported or unsupported).
 */
export const receiveTimeObservation = (observation, request) => {
  if (!isObject(observation)) return { ok: false, errors: [error("invalid-observation", "a TimeObservation must be a JSON object")] };
  const secrets = credentialFindings({ observation, request: request ?? null });
  if (secrets.length > 0) {
    return { ok: false, errors: secrets.map((path) => error("credential", `${path}: credential-like value; provenance must never carry authentication material`)) };
  }
  if (observation.schemaVersion !== OBSERVATION_SCHEMA) {
    const other = typeof observation.schemaVersion === "string" && OBSERVATION_SCHEMA_PATTERN.test(observation.schemaVersion);
    return {
      ok: false,
      errors: [other
        ? error("unsupported-observation-version", `schemaVersion '${observation.schemaVersion}' is not supported by this receiver (expects ${OBSERVATION_SCHEMA}); use its side-by-side contract version`)
        : error("invalid-observation", `schemaVersion must be ${OBSERVATION_SCHEMA}`)],
    };
  }
  const contextProblems = requestProblems(request);
  if (contextProblems.length > 0) return { ok: false, errors: contextProblems.map((message) => error("invalid-request", message)) };
  const problems = observationProblems(observation);
  if (problems.length > 0) return { ok: false, errors: problems.map((message) => error("invalid-observation", message)) };

  const received = observation.provenance !== undefined ? classify(observation.provenance) : undefined;
  if (received?.verdict === "malformed") {
    return { ok: false, errors: received.problems.map((message) => error("malformed-provenance", `provenance: ${message}`)) };
  }
  const warnings = [
    ...(received?.warnings ?? []),
    ...(received?.verdict === "unsupported" ? [`received provenance ${received.schema} is carried verbatim as source provenance; it is not interpreted`] : []),
  ];

  const invoker = request.invoker ?? { actor: { ...UNKNOWN_ACTOR } };
  const measuredByInvoker = observation.measurement !== undefined
    && !isUnknownActor(invoker.actor)
    && actorsAgree(invoker.actor, observation.performer.actor)
    && (observation.performer.execution === undefined || invoker.execution === undefined || observation.performer.execution === invoker.execution);
  const created = appendContribution(emptyBlock(), invokerKey({ execution: invoker.execution, operationId: request.operationId }), {
    operations: measuredByInvoker ? ["created", "measured"] : ["created"],
    at: request.receivedAt,
    actor: clone(invoker.actor),
    reason: "time.record: TimeObservation recorded",
  });
  if (!created.ok) return refused(created.error);
  const upstreamLineage = received?.verdict === "supported" && Array.isArray(observation.provenance.derivedFrom) ? observation.provenance.derivedFrom : [];
  const provenance = addLineage(created.block, [...upstreamLineage, sourceReference(observation)]);
  const verdict = classify(provenance);
  if (verdict.verdict !== "supported") return refused(verdict.problems.join("; "));

  const { provenance: sourceProvenance, ...fields } = clone(observation);
  const record = {
    observation: fields,
    provenance,
    ...(sourceProvenance !== undefined ? { sourceProvenance } : {}),
    ingestion: {
      operationId: request.operationId,
      receivedAt: request.receivedAt,
      invokerDeclared: request.invoker !== undefined,
      sourceProvenance: received === undefined ? "absent" : received.verdict,
      warnings,
    },
  };
  return { ok: true, record, warnings };
};

// ---- observation -> candidate -> entry (CHR-PROV-004, CHR-PROV-007) ---------

/** Exact recorded duration in seconds, or undefined when there is no duration evidence. Never estimated. */
export const recordedDurationSeconds = (measurement) => {
  if (!isObject(measurement)) return undefined;
  if (measurement.kind === "interval") return (parseTimestamp(measurement.endedAt) - parseTimestamp(measurement.startedAt)) / 1000;
  if (measurement.kind === "duration") return measurement.seconds;
  return undefined;
};

/**
 * Produces a TimeCandidate from a received observation record. Without
 * recorded duration evidence, or with an unknown performer, no candidate is
 * produced: disposition NeedsAttention with the reasons; the observation stays
 * preserved. Time is never synthesized from provenance or any indirect signal.
 * The candidate continues the record's own block with Chrona's `transformed`
 * contribution; the source provenance is carried verbatim, never appended to.
 *
 * context: { operationId, at }
 */
export const candidateFromObservation = (record, context) => {
  const { observation } = record;
  const seconds = recordedDurationSeconds(observation.measurement);
  const reasons = [
    ...(seconds === undefined ? ["no recorded duration evidence (measurement absent); Chrona does not estimate elapsed time"] : []),
    ...(isUnknownActor(observation.performer.actor) ? ["performer is unknown; time is never attributed to a guessed actor"] : []),
  ];
  if (reasons.length > 0) return { ok: false, disposition: "NeedsAttention", reasons, observationId: observation.observationId };
  if (!isInstant(context?.at) || !isNonEmptyString(context?.operationId)) {
    return { ok: false, errors: [error("invalid-request", "a candidate needs an operationId and a calendar-valid RFC 3339 UTC instant")] };
  }
  const transformed = appendContribution(record.provenance, chronaKey(context.operationId), {
    operations: ["transformed"], at: context.at, actor: { ...CHRONA_ACTOR }, reason: "TimeObservation -> TimeCandidate",
  });
  if (!transformed.ok) return refused(transformed.error);
  const candidate = {
    observationId: observation.observationId,
    sourceSystem: observation.sourceSystem,
    ...(observation.workItemId !== undefined ? { workItemId: observation.workItemId } : {}),
    ...(observation.externalReference !== undefined ? { externalReference: observation.externalReference } : {}),
    actorId: observation.performer.actor.id,
    actor: clone(observation.performer.actor),
    principalKinds: principalKindsFor(observation.performer.actor),
    ...(observation.performer.execution !== undefined ? { execution: observation.performer.execution } : {}),
    measurement: clone(observation.measurement),
    exactDurationSeconds: seconds,
    ingestedAt: record.ingestion.receivedAt,
    disposition: "Pending",
    provenance: transformed.block,
    ...(record.sourceProvenance !== undefined ? { sourceProvenance: clone(record.sourceProvenance) } : {}),
  };
  return { ok: true, candidate };
};

/**
 * Accepts a Pending candidate as a time entry. The reviewer is recorded as an
 * `approved` contribution (never authorship) under the key the caller
 * established for them (EXE-/EXT-/CTB-). Only the reference library's result
 * is stored; a refusal (credential, back-dated, re-attribution, ...) is a
 * structured error. Recording the reviewer is provenance, not authorization.
 *
 * decision: { key, actor, at, entryId }
 */
export const acceptCandidate = (candidate, decision) => {
  if (candidate.disposition !== "Pending") return { ok: false, errors: [error("invalid-transition", `a ${candidate.disposition} candidate cannot be accepted`)] };
  if (typeof candidate.exactDurationSeconds !== "number") return { ok: false, errors: [error("no-duration-evidence", "a candidate without a recorded duration cannot become a time entry")] };
  if (!isNonEmptyString(decision?.entryId) || !isInstant(decision?.at) || !isObject(decision?.actor)) {
    return { ok: false, errors: [error("invalid-request", "an acceptance needs an entryId, the reviewer actor, and a calendar-valid RFC 3339 UTC instant")] };
  }
  const approved = appendContribution(candidate.provenance ?? emptyBlock(), decision.key, {
    operations: ["approved"], at: decision.at, actor: clone(decision.actor), reason: "TimeCandidate accepted as a time entry",
  });
  if (!approved.ok) return refused(approved.error);
  const { disposition, provenance, ...rest } = clone(candidate);
  return { ok: true, entry: { entryId: decision.entryId, ...rest, disposition: "Accepted", provenance: approved.block } };
};

/**
 * The Praxis-compatible provenance of a Chrona record (observation record,
 * candidate, or entry) for export: the record's own block, tagged. Source
 * provenance travels separately and verbatim (`sourceProvenance`).
 */
export const toInterchange = (record) => {
  const block = record?.provenance;
  if (block === undefined) return undefined;
  return block.schema === undefined ? { schema: SCHEMA_TAG, ...clone(block) } : clone(block);
};
