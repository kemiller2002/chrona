// Chrona time.record v1 intake: the provenance portion of
// TimeObservation -> TimeCandidate -> accepted time entry.
//
// Requirements: docs/requirements/CHRONA-PROVENANCE.md (CHR-PROV-001..010),
// which reference Praxis RQ-ROS-2026-A001..A019 and DF-ROS-2026-A037.
// Identity is the Praxis actor and provenance is the Praxis
// praxis.provenance/1 block; the receiving and appending rules come from the
// vendored, unchanged Praxis reference library. Chrona adds no second
// identity model and has no runtime dependency on Praxis being installed.
//
// Every function is pure: inputs are never mutated; results are new plain
// objects that serialize as-is. Nothing here reads a clock, the environment,
// or the filesystem: times and identities are passed in explicitly.

import {
  SCHEMA_TAG, classify, appendContribution, addLineage, actorProblems, actorsAgree,
  credentialFindings, contributionProblems, emptyBlock, foreignExecutionKey,
} from "../vendor/praxis-provenance/lib/provenance-interchange.mjs";

export const OBSERVATION_SCHEMA = "chrona.time-observation/1";
const OBSERVATION_SCHEMA_PATTERN = /^chrona\.time-observation\/([1-9][0-9]*)$/;
const PERFORMER_EXECUTION = /^(EXE-[A-Za-z0-9._-]+|EXT-[a-z][a-z0-9-]*\.[A-Za-z0-9._-]+)$/;
const TIMESTAMP = /^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,9})?Z$/;
const MEASUREMENT_SOURCES = Object.freeze(["timer", "manual-entry", "telemetry", "import"]);
const UNKNOWN = "unknown";

/** Chrona's own identity when it changes a record's representation (DF-ROS-2026-A037 receiver rule). */
export const CHRONA_ACTOR = Object.freeze({ kind: "automation", id: "echelon/chrona", provider: "echelon", model: UNKNOWN, runtime: "chrona" });

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const isInstant = (value) => typeof value === "string" && TIMESTAMP.test(value) && !Number.isNaN(Date.parse(value));
const clone = (value) => (value === undefined ? undefined : JSON.parse(JSON.stringify(value)));
const safeRunId = (text) => String(text).replace(/[^A-Za-z0-9._-]/g, "-");
const error = (code, message) => ({ code, message });

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
  const attributes = (fallbackRuntime) => ({
    provider: isNonEmptyString(principal.provider) ? principal.provider : UNKNOWN,
    model: isNonEmptyString(principal.model) ? principal.model : UNKNOWN,
    runtime: isNonEmptyString(principal.runtime) ? principal.runtime : fallbackRuntime,
  });
  switch (principal.kind) {
    case "Human": return { ok: true, actor: { kind: "human", id: principal.id } };
    case "Agent": return { ok: true, actor: { kind: "agent", id: principal.id, ...attributes(UNKNOWN) } };
    case "Service":
    case "Integration": return { ok: true, actor: { kind: "automation", id: principal.id, ...attributes(UNKNOWN) } };
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
 * The contribution key for an explicitly declared invoker: its execution
 * (EXE-/EXT-) when known, otherwise EXT-op.<operationId>. Never invented.
 */
export const invokerKey = ({ execution, operationId }) =>
  isNonEmptyString(execution) ? execution : foreignExecutionKey("op", safeRunId(operationId));

/** Chrona's own key for a representation change it made during an operation. */
export const chronaKey = (operationId) => foreignExecutionKey("chrona", safeRunId(operationId));

// ---- observation validation (CHR-PROV-005, CHR-PROV-006) --------------------

const measurementProblems = (measurement) => {
  if (measurement === undefined) return [];
  if (!isObject(measurement)) return ["measurement must be an object"];
  const source = MEASUREMENT_SOURCES.includes(measurement.source) ? [] : [`measurement.source must be one of ${MEASUREMENT_SOURCES.join(", ")}`];
  if (measurement.kind === "interval") {
    const times = [
      ...(isInstant(measurement.startedAt) ? [] : ["measurement.startedAt must be an RFC 3339 UTC instant"]),
      ...(isInstant(measurement.endedAt) ? [] : ["measurement.endedAt must be an RFC 3339 UTC instant"]),
    ];
    const order = times.length === 0 && Date.parse(measurement.endedAt) < Date.parse(measurement.startedAt)
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

const observationProblems = (observation) => [
  ...(isNonEmptyString(observation.observationId) ? [] : ["observationId must be a non-empty string"]),
  ...(isNonEmptyString(observation.sourceSystem) ? [] : ["sourceSystem must be a non-empty string"]),
  ...(observation.workItemId === undefined || isNonEmptyString(observation.workItemId) ? [] : ["workItemId must be a non-empty string when present"]),
  ...(isInstant(observation.recordedAt) ? [] : ["recordedAt must be an RFC 3339 UTC instant"]),
  ...(!isObject(observation.performer) ? ["performer must be an object with the Praxis actor who performed the work"]
    : [
      ...actorProblems(observation.performer.actor, "performer.actor"),
      ...(observation.performer.execution === undefined || (typeof observation.performer.execution === "string" && PERFORMER_EXECUTION.test(observation.performer.execution))
        ? [] : ["performer.execution must be EXE-... or EXT-<system>.<run-id> when present"]),
    ]),
  ...measurementProblems(observation.measurement),
];

const requestProblems = (request) => [
  ...(isObject(request) ? [] : ["request context is required"]),
  ...(isObject(request) && !isNonEmptyString(request.operationId) ? ["request.operationId must be a non-empty string"] : []),
  ...(isObject(request) && !isInstant(request.receivedAt) ? ["request.receivedAt must be an RFC 3339 UTC instant"] : []),
  ...(isObject(request) && request.invoker !== undefined
    ? (isObject(request.invoker) ? actorProblems(request.invoker.actor, "request.invoker.actor") : ["request.invoker must be an object"])
    : []),
];

/** Provenance status of a stored record: attributed, unattributed (absent/empty), or unsupported (carried verbatim). */
export const provenanceStatus = (block) => {
  if (block === undefined) return "unattributed";
  const verdict = classify(block).verdict;
  if (verdict === "unsupported") return "unsupported";
  if (verdict === "malformed") return "malformed";
  return Object.keys(block.contributions).length === 0 ? "unattributed" : "attributed";
};

/**
 * Receives a TimeObservation through time.record v1.
 *
 * request: { operationId, receivedAt, invoker?: { actor, execution? } }
 * - `invoker` is the CURRENT invoking actor, taken only from an explicit
 *   declaration (execution envelope, flags, ROS_ACTOR_KIND/ROS_ACTOR/
 *   ROS_TELEMETRY_*, ROS_EXECUTION_ID). Absent means not declared: nothing is
 *   appended and nothing is guessed.
 *
 * Returns { ok: false, errors } for an invalid observation or malformed
 * provenance (rejected at the boundary; never dropped or repaired), or
 * { ok: true, record, warnings } where record = { observation, ingestion }.
 */
export const receiveTimeObservation = (observation, request) => {
  if (!isObject(observation)) return { ok: false, errors: [error("invalid-observation", "a TimeObservation must be a JSON object")] };
  const secrets = credentialFindings(observation);
  if (secrets.length > 0) {
    return { ok: false, errors: secrets.map((path) => error("credential", `${path}: credential-like value; an observation must never carry authentication material`)) };
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

  const received = observation.provenance === undefined ? undefined : classify(observation.provenance);
  if (received?.verdict === "malformed") {
    return { ok: false, errors: received.problems.map((message) => error("malformed-provenance", `provenance: ${message}`)) };
  }
  const ingestion = (status, warnings) => ({ operationId: request.operationId, receivedAt: request.receivedAt, provenance: status, warnings });

  if (received?.verdict === "unsupported") {
    const warnings = [`provenance ${received.schema} is not a major version this receiver understands; carried verbatim`];
    return { ok: true, record: { observation: clone(observation), ingestion: ingestion("unsupported", warnings) }, warnings };
  }

  const tolerated = received?.warnings ?? [];
  if (request.invoker === undefined) {
    const record = { observation: clone(observation), ingestion: ingestion(provenanceStatus(observation.provenance), tolerated) };
    return { ok: true, record, warnings: tolerated };
  }

  const block = observation.provenance ?? emptyBlock();
  const key = invokerKey({ execution: request.invoker.execution, operationId: request.operationId });
  if (Object.hasOwn(block.contributions, key)) {
    if (!actorsAgree(block.contributions[key].actor, request.invoker.actor)) {
      const existing = block.contributions[key].actor;
      return { ok: false, errors: [error("provenance-conflict", `contribution '${key}' is already attributed to ${existing.kind}:${existing.id}; refusing to re-attribute it`)] };
    }
    // Replay of an operation this execution already recorded: idempotent.
    const record = { observation: clone(observation), ingestion: ingestion(provenanceStatus(block), tolerated) };
    return { ok: true, record, warnings: tolerated };
  }
  const contribution = {
    operations: [Object.keys(block.contributions).length === 0 ? "created" : "measured"],
    at: request.receivedAt,
    actor: clone(request.invoker.actor),
    reason: "time.record: TimeObservation received",
  };
  const keyProblems = contributionProblems(key, contribution);
  if (keyProblems.length > 0) return { ok: false, errors: keyProblems.map((message) => error("invalid-request", message)) };
  const appended = appendContribution(block, key, contribution);
  if (!appended.ok) return { ok: false, errors: [error("provenance-conflict", appended.error)] };
  const next = { ...clone(observation), provenance: appended.block };
  return { ok: true, record: { observation: next, ingestion: ingestion(provenanceStatus(appended.block), tolerated) }, warnings: tolerated };
};

// ---- observation -> candidate -> entry (CHR-PROV-004, CHR-PROV-007) ---------

/** Exact recorded duration in seconds, or undefined when there is no duration evidence. Never estimated. */
export const recordedDurationSeconds = (measurement) => {
  if (!isObject(measurement)) return undefined;
  if (measurement.kind === "interval") return (Date.parse(measurement.endedAt) - Date.parse(measurement.startedAt)) / 1000;
  if (measurement.kind === "duration") return measurement.seconds;
  return undefined;
};

/** Carries a supported block forward with lineage and Chrona's own `transformed` contribution; others unchanged. */
const transformProvenance = (block, lineage, operationId, at, reason) => {
  if (block === undefined) return { ok: true, block: undefined };
  if (classify(block).verdict !== "supported") return { ok: true, block: clone(block) };
  return appendContribution(addLineage(block, lineage), chronaKey(operationId), { operations: ["transformed"], at, actor: CHRONA_ACTOR, reason });
};

/**
 * Produces a TimeCandidate from a received observation.
 * Without recorded duration evidence, or with an unknown performer, no
 * candidate is produced: the result is disposition NeedsAttention with the
 * reasons, and the observation stays preserved. Time is never synthesized
 * from provenance, execution existence, or any other indirect signal.
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
  const carried = transformProvenance(
    observation.provenance, [`chrona:observation/${observation.observationId}`], context.operationId, context.at,
    "TimeObservation -> TimeCandidate");
  if (!carried.ok) return { ok: false, disposition: "NeedsAttention", reasons: [carried.error], observationId: observation.observationId };
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
    ...(carried.block !== undefined ? { provenance: carried.block } : {}),
  };
  return { ok: true, candidate };
};

/**
 * Accepts a Pending candidate as a time entry. The reviewer is recorded as an
 * `approved` contribution (not authorship, not modification) under the key
 * the caller established for them (EXE-/EXT-/CTB-); the performer and
 * execution are carried unchanged. Recording the reviewer is provenance, not
 * an authorization decision (RQ-ROS-2026-A019).
 *
 * decision: { key, actor, at, entryId }
 */
export const acceptCandidate = (candidate, decision) => {
  if (candidate.disposition !== "Pending") return { ok: false, error: `a ${candidate.disposition} candidate cannot be accepted` };
  if (typeof candidate.exactDurationSeconds !== "number") return { ok: false, error: "a candidate without a recorded duration cannot become a time entry" };
  if (!isNonEmptyString(decision?.entryId) || !isInstant(decision?.at)) return { ok: false, error: "an acceptance needs an entryId and an RFC 3339 UTC instant" };
  const appendable = ["attributed", "unattributed"].includes(provenanceStatus(candidate.provenance));
  const approved = appendable && decision.actor !== undefined
    ? appendContribution(
      addLineage(candidate.provenance ?? emptyBlock(), [`chrona:candidate/${candidate.observationId}`]),
      decision.key,
      { operations: ["approved"], at: decision.at, actor: clone(decision.actor), reason: "TimeCandidate accepted as a time entry" })
    : { ok: true, block: clone(candidate.provenance) };
  if (!approved.ok) return { ok: false, error: approved.error };
  const provenance = approved.block;
  const { disposition, ...rest } = clone(candidate);
  return { ok: true, entry: { entryId: decision.entryId, ...rest, disposition: "Accepted", ...(provenance !== undefined ? { provenance } : {}) } };
};

/**
 * The Praxis-compatible provenance of a Chrona record (observation, candidate,
 * or entry) for export: the block itself (supported or unsupported, verbatim),
 * or undefined when the record is unattributed. Never synthesized.
 */
export const toInterchange = (record) => {
  const block = record?.observation?.provenance ?? record?.provenance;
  if (block === undefined) return undefined;
  return block.schema === undefined && classify(block).verdict === "supported" ? { schema: SCHEMA_TAG, ...clone(block) } : clone(block);
};
