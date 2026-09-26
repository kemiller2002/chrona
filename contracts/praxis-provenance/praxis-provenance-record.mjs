// Minimal, dependency-free reader/validator for the Praxis provenance
// interchange record (`praxis.provenance-record`, contract version 1.x).
//
// Contract owner: kemiller2002/praxis (RQ-ROS-2026-A001, A002, A013..A015;
// schemas/provenance-record.schema.json). This file is a local re-implementation
// of the contract's structural rules; it has no package or code reference to
// Praxis and never needs Praxis to be reachable. It interprets, but never
// rewrites: callers keep the raw JSON they received and carry it verbatim.
//
// Style: pure functions over frozen plain data; no input is mutated.

export const CONTRACT_NAME = "praxis.provenance-record";
export const SUPPORTED_MAJOR = 1;
export const MAX_SOURCE_DEPTH = 16;

const KINDS = new Set(["agent", "human", "automation", "unknown"]);
const OPERATIONS = new Set(["created", "modified", "reviewed", "approved", "superseded", "migrated"]);
const EXTENSION = /^x-[a-z0-9][a-z0-9-]*$/;
const VERSION = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$/;
const EXECUTION_KEY = /^EXE-[A-Za-z0-9._-]+$/;
const CONTRIBUTION_KEY = /^CTB-[A-Za-z0-9._-]+$/;
const FOREIGN_EXECUTION = /^EXE-[a-z][a-z0-9-]*\./;
const SYSTEM = /^[a-z][a-z0-9-]*$/;
const RUN = /^[A-Za-z0-9_-][A-Za-z0-9._-]*$/;
const REFERENCE = /^\S+$/;
const TIMESTAMP = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z$/;

// Same shapes Praxis refuses (Credentials.fs): a guard against accidental
// leaks, not a secret scanner.
const CREDENTIALS = [
  /\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{16,}/,
  /\bgh[pousr]_[A-Za-z0-9]{20,}/,
  /\bgithub_pat_[A-Za-z0-9_]{20,}/,
  /\bxox[abposr]-[A-Za-z0-9-]{10,}/,
  /\bAKIA[0-9A-Z]{16}\b/,
  /\bAIza[0-9A-Za-z_-]{30,}/,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
  /\bbearer\s+[A-Za-z0-9._~+/=-]{16,}/i,
  /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/,
  /\b(?:api[_-]?key|access[_-]?token|secret|password|passwd)\s*[=:]\s*\S{8,}/i,
];

const problem = (field, message) => Object.freeze({ field, message });
const at = (prefix, field) => (prefix === "" ? field : `${prefix}.${field}`);
const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isAbsent = (value) => value === undefined || value === null;
const stringOr = (value, fallback) => (typeof value === "string" ? value : fallback);

export const looksLikeCredential = (value) =>
  typeof value === "string" && CREDENTIALS.some((pattern) => pattern.test(value));

export const isKnownKind = (kind) => typeof kind === "string" && (KINDS.has(kind) || EXTENSION.test(kind));
export const isKnownOperation = (op) => typeof op === "string" && (OPERATIONS.has(op) || EXTENSION.test(op));
export const isExecutionKey = (key) => typeof key === "string" && EXECUTION_KEY.test(key);
export const isContributionKey = (key) => isExecutionKey(key) || (typeof key === "string" && CONTRIBUTION_KEY.test(key));
export const isForeignExecution = (key) => typeof key === "string" && FOREIGN_EXECUTION.test(key);

/** `EXE-<system>.<run>` for a system with no propagated Praxis execution (RQ-ROS-2026-A014). */
export const foreignExecutionKey = (system, run) =>
  !SYSTEM.test(system ?? "")
    ? { ok: false, error: `system '${system}' must match ^[a-z][a-z0-9-]*$` }
    : !RUN.test(run ?? "")
      ? { ok: false, error: `run '${run}' must match ^[A-Za-z0-9_-][A-Za-z0-9._-]*$` }
      : { ok: true, value: `EXE-${system}.${run}` };

/** Nanoseconds since the epoch as a BigInt, or null when not an ISO-8601 UTC timestamp. */
export const instant = (value) => {
  const match = typeof value === "string" ? TIMESTAMP.exec(value) : null;
  if (!match) return null;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  const millis = Date.UTC(year, month - 1, day, hour, minute, second);
  const date = new Date(millis);
  const roundTrips =
    date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day &&
    date.getUTCHours() === hour && date.getUTCMinutes() === minute && date.getUTCSeconds() === second;
  return roundTrips ? BigInt(millis) * 1_000_000n + BigInt((match[7] ?? "").padEnd(9, "0")) : null;
};

const compareInstants = (left, right) => {
  const a = instant(left);
  const b = instant(right);
  if (a === b) return 0;
  if (a === null) return 1; // invalid sorts last: it never masquerades as the origin
  if (b === null) return -1;
  return a < b ? -1 : 1;
};

/** Order-insensitive structural JSON equality. */
export const jsonEqual = (left, right) => {
  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left) && Array.isArray(right) && left.length === right.length &&
      left.every((item, index) => jsonEqual(item, right[index]));
  }
  if (isObject(left) || isObject(right)) {
    if (!isObject(left) || !isObject(right)) return false;
    const keys = Object.keys(left);
    return keys.length === Object.keys(right).length &&
      keys.every((key) => Object.hasOwn(right, key) && jsonEqual(left[key], right[key]));
  }
  return left === right;
};

// ---- reading ---------------------------------------------------------------

const stringArray = (field, value) =>
  isAbsent(value)
    ? { ok: true, value: [] }
    : Array.isArray(value) && value.every((item) => typeof item === "string")
      ? { ok: true, value: Object.freeze([...value]) }
      : { ok: false, problems: [problem(field, "must be an array of strings")] };

const readActor = (field, value) => {
  if (isAbsent(value)) return { ok: false, problems: [problem(field, "actor is required")] };
  if (!isObject(value)) return { ok: false, problems: [problem(field, "actor must be an object")] };
  if (typeof value.kind !== "string") return { ok: false, problems: [problem(field, "actor.kind is required")] };
  if (!isKnownKind(value.kind)) return { ok: false, problems: [problem(field, `unknown actor kind '${value.kind}'`)] };
  const optional = (name) => (typeof value[name] === "string" ? value[name] : undefined);
  return {
    ok: true,
    value: Object.freeze({
      kind: value.kind,
      id: stringOr(value.id, ""),
      provider: optional("provider"),
      model: optional("model"),
      runtime: optional("runtime"),
    }),
  };
};

const readContribution = (key, value) => {
  const prefix = `contributions.${key}`;
  if (!isObject(value)) return { ok: false, problems: [problem(prefix, "contribution must be an object")] };
  const operations = stringArray(`${prefix}.operations`, value.operations);
  const evidence = stringArray(`${prefix}.evidence`, value.evidence);
  const actor = readActor(`${prefix}.actor`, value.actor);
  const structural = [operations, evidence, actor].flatMap((result) => (result.ok ? [] : result.problems));
  if (structural.length > 0) return { ok: false, problems: structural };
  const vocabulary = [
    ...operations.value.filter((op) => !isKnownOperation(op)).map((op) => problem(`${prefix}.operations`, `unknown operation '${op}'`)),
    ...(new Set(operations.value).size !== operations.value.length ? [problem(`${prefix}.operations`, "operations must be unique")] : []),
  ];
  return vocabulary.length > 0
    ? { ok: false, problems: vocabulary }
    : {
        ok: true,
        value: Object.freeze({
          key,
          operations: operations.value,
          at: stringOr(value.at, ""),
          last: typeof value.last === "string" ? value.last : undefined,
          actor: actor.value,
          reason: typeof value.reason === "string" ? value.reason : undefined,
          evidence: evidence.value,
        }),
      };
};

const orderContributions = (contributions) =>
  Object.freeze([...contributions].sort((left, right) => compareInstants(left.at, right.at) || (left.key < right.key ? -1 : left.key > right.key ? 1 : 0)));

const collect = (results) => {
  const problems = results.flatMap((result) => (result.ok ? [] : result.problems));
  return problems.length > 0 ? { ok: false, problems } : { ok: true, value: results.map((result) => result.value) };
};

const prefixProblems = (prefix, problems) => problems.map((item) => problem(at(prefix, item.field), item.message));

const parseVersion = (text) => {
  const match = typeof text === "string" ? VERSION.exec(text) : null;
  return match ? Object.freeze({ major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]), text }) : null;
};

const readBody = (raw, version, depth) => {
  const contributions = !isObject(raw.contributions)
    ? { ok: false, problems: [problem("contributions", isAbsent(raw.contributions) ? "contributions is required" : "contributions must be an object keyed by EXE-/CTB- ID")] }
    : collect(Object.entries(raw.contributions).map(([key, value]) => readContribution(key, value)));
  const derivedFrom = stringArray("derivedFrom", raw.derivedFrom);
  const subject = isAbsent(raw.subject)
    ? { ok: true, value: undefined }
    : typeof raw.subject === "string"
      ? { ok: true, value: raw.subject }
      : { ok: false, problems: [problem("subject", "subject must be a string")] };
  const sources = isAbsent(raw.sources)
    ? { ok: true, value: [] }
    : !isObject(raw.sources)
      ? { ok: false, problems: [problem("sources", "sources must be an object keyed by lineage reference")] }
      : depth >= MAX_SOURCE_DEPTH && Object.keys(raw.sources).length > 0
        ? { ok: false, problems: [problem("sources", `lineage snapshots nest deeper than ${MAX_SOURCE_DEPTH} levels`)] }
        : collect(
            Object.entries(raw.sources).map(([reference, snapshot]) => {
              const reading = readAt(snapshot, depth + 1);
              return reading.status === "invalid"
                ? { ok: false, problems: prefixProblems(`sources.${reference}`, reading.problems) }
                : {
                    ok: true,
                    value: Object.freeze(
                      reading.status === "unsupported"
                        ? { reference, opaque: true, version: reading.version }
                        : { reference, opaque: false, record: reading.record },
                    ),
                  };
            }),
          );
  const problems = [contributions, derivedFrom, subject, sources].flatMap((result) => (result.ok ? [] : result.problems));
  return problems.length > 0
    ? { ok: false, problems }
    : {
        ok: true,
        value: Object.freeze({
          version,
          subject: subject.value,
          contributions: orderContributions(contributions.value),
          derivedFrom: derivedFrom.value,
          sources: Object.freeze(sources.value),
        }),
      };
};

const readAt = (raw, depth) => {
  if (!isObject(raw)) return { status: "invalid", problems: [problem("", "a provenance record must be a JSON object")] };
  if (isAbsent(raw.contract) && isAbsent(raw.version)) {
    const body = readBody(raw, parseVersion("1.0.0"), depth);
    return body.ok ? { status: "unversioned", record: body.value, raw } : { status: "invalid", problems: body.problems };
  }
  if (raw.contract !== CONTRACT_NAME) return { status: "invalid", problems: [problem("contract", `contract must be '${CONTRACT_NAME}'`)] };
  const version = parseVersion(raw.version);
  if (!version) return { status: "invalid", problems: [problem("version", "version must be a semantic version (MAJOR.MINOR.PATCH)")] };
  if (version.major !== SUPPORTED_MAJOR) return { status: "unsupported", version: version.text, raw };
  const body = readBody(raw, version, depth);
  return body.ok ? { status: "current", record: body.value, raw } : { status: "invalid", problems: body.problems };
};

/**
 * Structural read. `status` is "current" | "unversioned" (legacy bare
 * `{contributions}`) | "unsupported" (other major: carry verbatim) | "invalid".
 */
export const read = (raw) => readAt(raw, 0);

// ---- rules -----------------------------------------------------------------

const actorProblems = (actor) => [
  ...(actor.id.trim().length === 0 ? [problem("id", "actor id must not be empty; use 'unknown' when it is not known")] : []),
  ...(actor.kind === "agent"
    ? ["provider", "model", "runtime"].flatMap((name) =>
        actor[name] === undefined
          ? [problem(name, `agent actor must record ${name} (use 'unknown' when it is not known)`)]
          : actor[name].trim().length === 0
            ? [problem(name, `agent actor ${name} must not be empty`)]
            : [])
    : []),
  ...["id", "provider", "model", "runtime"]
    .filter((name) => looksLikeCredential(actor[name]))
    .map((name) => problem(name, "value looks like a credential; identity must never carry secrets")),
];

/** Reads an actor object and applies the Praxis actor rules (RQ-ROS-2026-A001). */
export const validateActor = (value) => {
  const actor = readActor("", value);
  return actor.ok ? actorProblems(actor.value) : actor.problems;
};

const contributionProblems = (contribution) => {
  const field = (name) => `contributions.${contribution.key}.${name}`;
  const first = instant(contribution.at);
  const last = contribution.last === undefined ? undefined : instant(contribution.last);
  return [
    ...(!isContributionKey(contribution.key)
      ? [problem(field("key"), `contribution key '${contribution.key}' must be an execution ID (EXE-...) or a contribution ID (CTB-...)`)]
      : []),
    ...(contribution.operations.length === 0 ? [problem(field("operations"), "contribution must record at least one operation")] : []),
    ...(first === null ? [problem(field("at"), `'${contribution.at}' is not an ISO-8601 UTC timestamp`)] : []),
    ...(contribution.last !== undefined && last === null ? [problem(field("last"), `'${contribution.last}' is not an ISO-8601 UTC timestamp`)] : []),
    ...(last !== undefined && last !== null && first !== null && last < first ? [problem(field("last"), "last must not precede at")] : []),
    ...actorProblems(contribution.actor).map((item) => problem(field(`actor.${item.field}`), item.message)),
    ...(contribution.actor.kind === "agent" && !isExecutionKey(contribution.key)
      ? [problem(field("key"), "an agent contribution must be keyed by the execution (EXE-...) that produced it")]
      : []),
    ...(contribution.evidence.some((item) => item.trim().length === 0) ? [problem(field("evidence"), "evidence references must not be empty")] : []),
    ...(looksLikeCredential(contribution.reason) ? [problem(field("reason"), "reason looks like it contains a credential")] : []),
    ...(contribution.evidence.some(looksLikeCredential) ? [problem(field("evidence"), "an evidence reference looks like a credential")] : []),
  ];
};

const isCreation = (contribution) => contribution.operations.includes("created");

/** The contribution that claims `created`, if any. */
export const originator = (record) => record.contributions.find(isCreation);

const recordProblems = (record, prefix, depth) => {
  const creations = record.contributions.filter(isCreation);
  const creationProblems =
    creations.length > 1
      ? [problem("contributions", `more than one contribution claims 'created': ${creations.map((item) => item.key).join(", ")}`)]
      : creations.length === 1
        ? record.contributions
            .filter((item) => compareInstants(item.at, creations[0].at) < 0)
            .map((item) => problem(`contributions.${item.key}.at`, `contribution precedes the recorded creation (${creations[0].key})`))
        : [];
  const references = [...(record.subject === undefined ? [] : [["subject", record.subject]]), ...record.derivedFrom.map((value) => ["derivedFrom", value])];
  const own = [
    ...record.contributions.flatMap(contributionProblems),
    ...creationProblems,
    ...references.filter(([, value]) => !REFERENCE.test(value)).map(([name, value]) => problem(name, `reference '${value}' must be a non-empty token without whitespace`)),
    ...references.filter(([, value]) => looksLikeCredential(value)).map(([name]) => problem(name, "value looks like a credential")),
    ...(record.subject !== undefined && record.derivedFrom.includes(record.subject) ? [problem("derivedFrom", `'${record.subject}' cannot be derived from itself`)] : []),
    ...(new Set(record.derivedFrom).size !== record.derivedFrom.length ? [problem("derivedFrom", "lineage references must be unique")] : []),
    ...record.sources
      .filter((source) => !record.derivedFrom.includes(source.reference))
      .map((source) => problem(`sources.${source.reference}`, "a lineage snapshot must name a reference listed in derivedFrom")),
    ...record.sources
      .filter((source) => !source.opaque && source.record.subject !== undefined && source.record.subject !== source.reference)
      .map((source) => problem(`sources.${source.reference}.subject`, `the snapshot describes '${source.record.subject}', not '${source.reference}'`)),
  ];
  const nested = record.sources.flatMap((source) =>
    source.opaque
      ? []
      : depth >= MAX_SOURCE_DEPTH
        ? [problem(`sources.${source.reference}`, `lineage snapshots nest deeper than ${MAX_SOURCE_DEPTH} levels`)]
        : recordProblems(source.record, `sources.${source.reference}`, depth + 1),
  );
  return [...prefixProblems(prefix, own), ...nested];
};

/**
 * Structural read plus every rule of the contract. Returns the reading, or
 * `{status: "invalid", problems}`. An unsupported major is not an error:
 * carry it verbatim.
 */
export const validate = (raw) => {
  const reading = read(raw);
  if (reading.status !== "current" && reading.status !== "unversioned") return reading;
  const problems = recordProblems(reading.record, "", 0);
  return problems.length > 0 ? { status: "invalid", problems } : reading;
};

/** Manifest vocabulary: "valid" | "valid-unversioned" | "unsupported-version" | "invalid". */
export const classify = (raw) =>
  ({ current: "valid", unversioned: "valid-unversioned", unsupported: "unsupported-version", invalid: "invalid" })[validate(raw).status];

// ---- lineage chain -----------------------------------------------------------

/** Every contribution in the record and its lineage snapshots, own first; each link keeps its subject. */
export const chain = (record) => {
  const walk = (current, depth) => [
    ...current.contributions.map((contribution) => Object.freeze({ subject: current.subject, depth, contribution })),
    ...(depth >= MAX_SOURCE_DEPTH
      ? []
      : current.sources.flatMap((source) =>
          source.opaque ? [] : walk({ ...source.record, subject: source.record.subject ?? source.reference }, depth + 1))),
  ];
  return walk(record, 0);
};

// ---- non-destructive successor (RQ-ROS-2026-A015) ---------------------------

const sameActor = (left, right) =>
  left.kind === right.kind && left.id === right.id && left.provider === right.provider &&
  left.model === right.model && left.runtime === right.runtime;

const describe = (actor) => `${actor.kind}:${actor.id}`;

const lowered = (before, after) =>
  after.minor < before.minor || (after.minor === before.minor && after.patch < before.patch);

const interpretedSuccessorProblems = (before, after) => {
  const afterByKey = new Map(after.contributions.map((item) => [item.key, item]));
  const contributions = before.contributions.flatMap((previous) => {
    const field = (name) => `contributions.${previous.key}${name}`;
    const current = afterByKey.get(previous.key);
    if (!current) return [problem(field(""), "contribution was removed; provenance history is append-only")];
    return [
      ...(!sameActor(previous.actor, current.actor) ? [problem(field(".actor"), `actor changed from ${describe(previous.actor)} to ${describe(current.actor)}`)] : []),
      ...(previous.at !== current.at ? [problem(field(".at"), "the time of the first recorded operation changed")] : []),
      ...previous.operations.filter((op) => !current.operations.includes(op)).map((op) => problem(field(".operations"), `operation '${op}' was removed`)),
      ...previous.evidence.filter((item) => !current.evidence.includes(item)).map((item) => problem(field(".evidence"), `evidence '${item}' was removed`)),
      ...(previous.reason !== undefined && previous.reason !== current.reason ? [problem(field(".reason"), "reason was rewritten")] : []),
    ];
  });
  const origin = originator(before);
  const successorOrigin = originator(after);
  return [
    ...contributions,
    ...(origin && successorOrigin && origin.key !== successorOrigin.key ? [problem("contributions", `originator changed from ${origin.key} to ${successorOrigin.key}`)] : []),
    ...before.derivedFrom.filter((ref) => !after.derivedFrom.includes(ref)).map((ref) => problem("derivedFrom", `lineage reference '${ref}' was removed`)),
    ...before.sources.filter((source) => !after.sources.some((item) => item.reference === source.reference)).map((source) => problem(`sources.${source.reference}`, "lineage snapshot was removed")),
    ...(before.subject !== undefined && after.subject !== before.subject ? [problem("subject", "subject changed; a different subject needs its own derived record")] : []),
    ...(after.version.major !== before.version.major
      ? [problem("version", "major version changed in place")]
      : lowered(before.version, after.version)
        ? [problem("version", `version was lowered from ${before.version.text} to ${after.version.text}`)]
        : []),
  ];
};

const EXTENDABLE = new Set(["operations", "evidence", "last", "reason"]);
const ENVELOPE = new Set(["contributions", "derivedFrom", "sources", "version", "contract"]);

const preservationProblems = (before, after) => [
  ...Object.entries(before)
    .filter(([key]) => !ENVELOPE.has(key))
    .filter(([key, value]) => !Object.hasOwn(after, key) || !jsonEqual(value, after[key]))
    .map(([key]) => problem(key, "field was removed or changed; fields a consumer does not model must be preserved")),
  ...(isObject(before.contributions) && isObject(after.contributions)
    ? Object.entries(before.contributions).flatMap(([key, entry]) => {
        const successor = after.contributions[key];
        return isObject(entry) && isObject(successor)
          ? Object.entries(entry)
              .filter(([name]) => !EXTENDABLE.has(name))
              .filter(([name, value]) => !Object.hasOwn(successor, name) || !jsonEqual(value, successor[name]))
              .map(([name]) => problem(`contributions.${key}.${name}`, "field was removed or changed; another contributor's entry must be preserved verbatim"))
          : [];
      })
    : []),
  ...(isObject(before.sources) && isObject(after.sources)
    ? Object.entries(before.sources)
        .filter(([key, value]) => Object.hasOwn(after.sources, key) && !jsonEqual(value, after.sources[key]))
        .map(([key]) => problem(`sources.${key}`, "lineage snapshot must be carried verbatim"))
    : []),
];

/**
 * Problems that make `after` a destructive successor of `before`: removed or
 * re-attributed contributions, lost execution identity, dropped lineage or
 * unknown fields, rewritten snapshots, a lowered version, or a modified
 * unsupported major. Empty means `after` preserves `before`.
 */
export const successorProblems = (before, after) => {
  const previous = read(before);
  const current = read(after);
  if (previous.status === "unsupported") {
    return jsonEqual(before, after) ? [] : [problem("", "a record in an unsupported major version must be carried verbatim")];
  }
  if (previous.status === "invalid") return [problem("", "the previous record is malformed; refusing to judge a successor of it")];
  if (current.status === "invalid") return current.problems;
  if (current.status === "unsupported") return [problem("version", "a supported record was replaced by an unsupported major version")];
  return [...interpretedSuccessorProblems(previous.record, current.record), ...preservationProblems(before, after)];
};
