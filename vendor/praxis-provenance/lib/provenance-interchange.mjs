// Praxis provenance interchange (praxis.provenance/1) -- reference library.
//
// The canonical semantics live in Praxis (docs/agent-provenance.md,
// schemas/provenance-interchange.schema.json, RQ-ROS-2026-A013..A017,
// DF-ROS-2026-A037). This module is a dependency-free, side-effect-free
// implementation of the receiving and appending rules so other Echelon
// systems can adopt the contract without depending on the Praxis CLI.
// Its verdicts are pinned to tests/fixtures/provenance-interchange/cases.json,
// which the F# implementation is tested against too.
//
// Every function is pure: inputs are never mutated, and results are new
// frozen-free plain objects that can be serialized as-is.

/**
 * Every environment variable Praxis identity discovery reads (Ros.Domain.Telemetry.Identity).
 * A launcher, hub, or worker that starts a process on behalf of another actor must remove
 * all of them before setting that actor's explicit identity, or its own identity and run
 * keys leak into the other actor's execution (contract 1.1). Pinned by
 * tests/fixtures/provenance-interchange/identity-environment.json.
 */
export const IDENTITY_ENVIRONMENT_VARIABLES = Object.freeze([
  "ROS_ACTOR", "ROS_ACTOR_KIND", "ROS_EXECUTION_ID",
  "ROS_TELEMETRY_PROVIDER", "ROS_TELEMETRY_MODEL", "ROS_TELEMETRY_MODEL_VERSION",
  "ROS_TELEMETRY_RUNTIME", "ROS_TELEMETRY_RUNTIME_VERSION", "ROS_TELEMETRY_SESSION_ID",
  "ROS_TELEMETRY_CONVERSATION_ID", "ROS_TELEMETRY_RUN_ID",
  "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CODEX_THREAD_ID", "GEMINI_SESSION_ID",
  "COPILOT_SESSION_ID", "GITHUB_ACTIONS", "GITHUB_RUN_ID", "OLLAMA_HOST",
]);

/** A pure environment for a child acting as `declared`: identity variables removed, declared ones set. */
export const identityEnvironment = (inherited, declared) => ({
  ...Object.fromEntries(Object.entries(inherited).filter(([name]) => !IDENTITY_ENVIRONMENT_VARIABLES.includes(name))),
  ...Object.fromEntries(Object.entries(declared).filter(([, value]) => value !== undefined && value !== null && value !== "")),
});

export const SCHEMA_TAG = "praxis.provenance/1";

const SCHEMA_PATTERN = /^praxis\.provenance\/([1-9][0-9]*)$/;
const EXECUTION_KEY = /^EXE-[A-Za-z0-9._-]+$/;
const CONTRIBUTION_KEY = /^CTB-[A-Za-z0-9._-]+$/;
const FOREIGN_EXECUTION_KEY = /^EXT-([a-z][a-z0-9-]*)\.([A-Za-z0-9._-]+)$/;
const KIND_PATTERN = /^(agent|human|automation|unknown|x-[a-z0-9][a-z0-9-]*)$/;
const OPERATION_GRAMMAR = /^[a-z][a-z0-9-]*$/;
const EXTENSION = /^x-[a-z0-9][a-z0-9-]*$/;
const TIMESTAMP = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,9}))?Z$/;
const UNKNOWN = "unknown";

export const KNOWN_OPERATIONS = Object.freeze([
  "created", "modified", "reviewed", "approved", "superseded", "migrated",
  "discovered", "measured", "transformed", "remediated", "validated", "resolved",
]);

const MODIFYING_OPERATIONS = new Set([
  "modified", "superseded", "migrated", "transformed", "remediated", "resolved",
]);

const CREDENTIAL_PATTERNS = [
  /gh[pousr]_[A-Za-z0-9]{20,}/,
  /github_pat_[A-Za-z0-9_]{20,}/,
  /sk-[A-Za-z0-9_-]{20,}/,
  /AKIA[0-9A-Z]{16}/,
  /xox[abprs]-[A-Za-z0-9-]{10,}/,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/,
  /\bbearer\s+[A-Za-z0-9._~+/=-]{16,}/i,
  /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\./,
];

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isString = (value) => typeof value === "string";
const isNonEmptyString = (value) => isString(value) && value.trim().length > 0;
/**
 * Milliseconds since the epoch for a calendar-valid UTC timestamp (year 0001-9999, no
 * rollover such as Feb 30 or 24:00), or undefined. Ordering is compared at millisecond
 * precision: extra fractional digits are truncated, never rounded (contract 1.1).
 */
export const parseTimestamp = (value) => {
  const match = isString(value) ? TIMESTAMP.exec(value) : null;
  if (!match) return undefined;
  const [year, month, day, hour, minute, second] = match.slice(1, 7).map(Number);
  const millisecond = Number((match[7] ?? "").padEnd(3, "0").slice(0, 3));
  if (year < 1 || hour > 23 || minute > 59 || second > 59) return undefined;
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, second, millisecond);
  const valid = date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
  return valid ? date.getTime() : undefined;
};
const isTimestamp = (value) => parseTimestamp(value) !== undefined;
const instant = (value) => parseTimestamp(value) ?? Number.POSITIVE_INFINITY;
const isKnown = (value) => isString(value) && value.trim().length > 0 && value.trim() !== UNKNOWN;

export const isCredentialLike = (value) => CREDENTIAL_PATTERNS.some((pattern) => pattern.test(value));

/** Dotted paths of every key or string value that looks like a credential. */
export const credentialFindings = (node, path = "") => {
  if (isString(node)) return isCredentialLike(node) ? [path] : [];
  if (Array.isArray(node)) return node.flatMap((item, index) => credentialFindings(item, `${path}[${index}]`));
  if (isObject(node)) {
    return Object.entries(node).flatMap(([key, value]) => {
      const child = path ? `${path}.${key}` : key;
      return [...(isCredentialLike(key) ? [child] : []), ...credentialFindings(value, child)];
    });
  }
  return [];
};

export const keyKind = (key) =>
  EXECUTION_KEY.test(key) ? "execution"
    : FOREIGN_EXECUTION_KEY.test(key) ? "foreign-execution"
      : CONTRIBUTION_KEY.test(key) ? "contribution"
        : "invalid";

/** `EXT-dokimos.run-7` -> `dokimos`; undefined for any other key. */
export const foreignSystem = (key) => FOREIGN_EXECUTION_KEY.exec(key)?.[1];

/** Builds an `EXT-<system>.<run-id>` key, rejecting ids it cannot carry. */
export const foreignExecutionKey = (system, runId) => {
  const key = `EXT-${system}.${runId}`;
  if (!FOREIGN_EXECUTION_KEY.test(key) || foreignSystem(key) !== system) {
    throw new Error(`cannot form a foreign execution key from system '${system}' and run '${runId}'`);
  }
  return key;
};

export const actorProblems = (actor, prefix = "actor") => {
  if (!isObject(actor)) return [`${prefix} must be an object`];
  const problems = [];
  if (!isString(actor.kind) || !KIND_PATTERN.test(actor.kind)) problems.push(`${prefix}.kind '${actor.kind}' is not agent, human, automation, unknown, or x-...`);
  if (!isNonEmptyString(actor.id)) problems.push(`${prefix}.id must not be empty; use 'unknown' when it is not known`);
  for (const field of ["provider", "model", "runtime"]) {
    if (actor[field] !== undefined && !isNonEmptyString(actor[field])) problems.push(`${prefix}.${field} must be a non-empty string`);
    if (actor.kind === "agent" && actor[field] === undefined) problems.push(`${prefix}.${field} is required for an agent ('unknown' when not known)`);
  }
  return problems;
};

/** Same actor: kind, stable id, and every applicable known attribute agree; `unknown` never contradicts. */
export const actorsAgree = (left, right) => {
  const compatible = (a, b) => !(isKnown(a) && isKnown(b)) || a === b;
  return left.kind === right.kind
    && (left.id === right.id || !isKnown(left.id) || !isKnown(right.id))
    && compatible(left.provider, right.provider)
    && compatible(left.model, right.model)
    && compatible(left.runtime, right.runtime);
};

const stringListProblems = (value, field) =>
  value === undefined ? []
    : Array.isArray(value) && value.every(isNonEmptyString) ? []
      : [`${field} must be an array of non-empty strings`];

export const contributionProblems = (key, entry) => {
  const prefix = `contributions.${key}`;
  if (!isObject(entry)) return [`${prefix} must be an object`];
  const operations = Array.isArray(entry.operations) ? entry.operations : undefined;
  const kind = keyKind(key);
  return [
    ...(kind === "invalid" ? [`${prefix}: key must be EXE-..., EXT-<system>.<run-id>, or CTB-...`] : []),
    ...(operations === undefined ? [`${prefix}.operations must be an array`]
      : operations.length === 0 ? [`${prefix}.operations must record at least one operation`]
        : [
          ...operations.filter((op) => !isString(op) || !OPERATION_GRAMMAR.test(op)).map((op) => `${prefix}.operations: '${op}' is not a valid operation code`),
          ...(new Set(operations).size !== operations.length ? [`${prefix}.operations must not repeat an operation`] : []),
        ]),
    ...(!isTimestamp(entry.at) ? [`${prefix}.at must be a calendar-valid ISO-8601 UTC timestamp`] : []),
    ...(entry.last === undefined ? []
      : !isTimestamp(entry.last) ? [`${prefix}.last must be a calendar-valid ISO-8601 UTC timestamp`]
        : instant(entry.last) < instant(entry.at) ? [`${prefix}.last must not precede at`] : []),
    ...(entry.actor === undefined ? [`${prefix}.actor is required`] : actorProblems(entry.actor, `${prefix}.actor`)),
    ...(isObject(entry.actor) && entry.actor.kind === "agent" && kind !== "execution" && kind !== "foreign-execution"
      ? [`${prefix}: an agent contribution must be keyed by the execution (EXE-... or EXT-...) that produced it`] : []),
    ...(entry.reason !== undefined && !isString(entry.reason) ? [`${prefix}.reason must be a string`] : []),
    ...stringListProblems(entry.evidence, `${prefix}.evidence`),
  ];
};

const creators = (contributions) =>
  Object.entries(contributions).filter(([, entry]) => entry.operations.includes("created"));

const historyProblems = (contributions) => {
  const created = creators(contributions);
  if (created.length > 1) return [`more than one contribution claims 'created': ${created.map(([key]) => key).join(", ")}`];
  if (created.length === 0) return [];
  const [creationKey, creation] = created[0];
  return Object.entries(contributions)
    .filter(([key, entry]) => key !== creationKey && instant(entry.at) < instant(creation.at))
    .map(([key]) => `contributions.${key} precedes the recorded creation (${creationKey})`);
};

/**
 * Classifies a received block:
 * - `supported`: understood; `warnings` name tolerated forward-compatible content.
 * - `unsupported`: another major version; carry verbatim, never interpret or merge.
 * - `malformed`: reject at the boundary; never drop or repair silently.
 */
export const classify = (block) => {
  if (!isObject(block)) return { verdict: "malformed", problems: ["provenance must be a JSON object"], warnings: [] };
  const secrets = credentialFindings(block);
  if (secrets.length > 0) {
    return { verdict: "malformed", problems: secrets.map((path) => `${path}: credential-like value; provenance must never carry authentication material`), warnings: [] };
  }
  if (block.schema !== undefined) {
    if (!isString(block.schema)) return { verdict: "malformed", problems: ["schema must be a string"], warnings: [] };
    if (block.schema !== SCHEMA_TAG) {
      if (SCHEMA_PATTERN.test(block.schema)) return { verdict: "unsupported", schema: block.schema, problems: [], warnings: [] };
      return { verdict: "malformed", problems: [`schema '${block.schema}' is not a valid praxis.provenance/<major> tag`], warnings: [] };
    }
  }
  if (!isObject(block.contributions)) {
    return { verdict: "malformed", problems: [block.contributions === undefined ? "contributions is required" : "contributions must be an object keyed by EXE-, EXT-, or CTB- keys"], warnings: [] };
  }
  const entryProblems = Object.entries(block.contributions).flatMap(([key, entry]) => contributionProblems(key, entry));
  const lineageProblems = stringListProblems(block.derivedFrom, "derivedFrom");
  const problems = [...entryProblems, ...lineageProblems];
  if (problems.length > 0) return { verdict: "malformed", problems, warnings: [] };
  const invariant = historyProblems(block.contributions);
  if (invariant.length > 0) return { verdict: "malformed", problems: invariant, warnings: [] };
  const warnings = Object.entries(block.contributions).flatMap(([key, entry]) =>
    entry.operations
      .filter((op) => !KNOWN_OPERATIONS.includes(op) && !EXTENSION.test(op))
      .map((op) => `contributions.${key}.operations: '${op}' is not an operation this version knows; preserved verbatim`));
  return { verdict: "supported", problems: [], warnings };
};

const clone = (value) => JSON.parse(JSON.stringify(value));

/** A new, empty `praxis.provenance/1` block. */
export const emptyBlock = () => ({ schema: SCHEMA_TAG, contributions: {} });

/**
 * Appends one contribution without disturbing any other (RQ-ROS-2026-A004 rules):
 * the same key merges operations/evidence and advances `last` only when the actor agrees;
 * a second or late `created` is refused; unknown fields everywhere are preserved.
 * Returns `{ ok: true, block, changed }` or `{ ok: false, error }`; the input is never mutated.
 */
export const appendContribution = (block, key, contribution) => {
  const received = classify(block);
  if (received.verdict !== "supported") {
    return { ok: false, error: `refusing to append to a ${received.verdict} provenance block${received.schema ? ` (${received.schema})` : ""}` };
  }
  const secrets = credentialFindings({ [key]: contribution });
  if (secrets.length > 0) {
    return { ok: false, error: `${secrets.join(", ")}: credential-like value; provenance must never carry authentication material` };
  }
  const problems = contributionProblems(key, contribution);
  if (problems.length > 0) return { ok: false, error: problems.join("; ") };

  const refuse = (error) => ({ ok: false, error });
  const finish = (next, changed) => {
    // Contract 1.1: whatever an append returns must itself classify as supported.
    const result = classify(next);
    return result.verdict === "supported"
      ? { ok: true, block: next, changed }
      : refuse(`the resulting history would be ${result.verdict}: ${result.problems.join("; ")}`);
  };

  const next = clone(block);
  const existing = next.contributions[key];
  if (existing === undefined) {
    if (contribution.operations.includes("created")) {
      if (creators(next.contributions).length > 0) return refuse("the record already has an originator; record 'modified' instead of 'created'");
      if (Object.values(next.contributions).some((entry) => instant(entry.at) < instant(contribution.at))) {
        return refuse("a 'created' contribution cannot follow existing contributions");
      }
    }
    next.contributions[key] = clone(contribution);
    return finish(next, true);
  }
  if (!actorsAgree(existing.actor, contribution.actor)) {
    return refuse(`contribution '${key}' is already attributed to ${existing.actor.kind}:${existing.actor.id}; refusing to re-attribute it`);
  }
  // An actor whose identity is unknown cannot add work to an entry a known actor holds:
  // "unknown" never contradicts, but it never proves the same run either.
  if ((isKnown(existing.actor.id) && !isKnown(contribution.actor.id)) || (existing.actor.kind !== UNKNOWN && contribution.actor.kind === UNKNOWN)) {
    return refuse(`contribution '${key}' belongs to ${existing.actor.kind}:${existing.actor.id}; an actor with unknown identity cannot extend it`);
  }
  if (contribution.operations.includes("created") && !existing.operations.includes("created")) {
    const earlier = Object.entries(next.contributions).some(([other, entry]) => other !== key && instant(entry.at) < instant(existing.at));
    if (creators(next.contributions).length > 0 || earlier) {
      return refuse("the record's originator is already recorded or precedes this contribution; record 'modified' instead of 'created'");
    }
  }
  const operations = [...existing.operations, ...contribution.operations.filter((op) => !existing.operations.includes(op))];
  const evidence = [...(existing.evidence ?? []), ...(contribution.evidence ?? []).filter((item) => !(existing.evidence ?? []).includes(item))];
  const latest = [existing.last ?? existing.at, contribution.last ?? contribution.at]
    .reduce((a, b) => (instant(b) > instant(a) ? b : a));
  const merged = {
    // Unknown fields from the incoming contribution are kept; the existing entry wins on conflict.
    ...clone(contribution),
    ...existing,
    operations,
    ...(evidence.length > 0 ? { evidence } : {}),
    ...(instant(latest) > instant(existing.at) ? { last: latest } : {}),
    ...(existing.reason === undefined && contribution.reason !== undefined ? { reason: contribution.reason } : {}),
  };
  const changed = JSON.stringify(merged) !== JSON.stringify(existing);
  next.contributions[key] = merged;
  return finish(next, changed);
};

/** Adds lineage references (never authorship), preserving existing order. */
export const addLineage = (block, references) => {
  const current = Array.isArray(block.derivedFrom) ? block.derivedFrom : [];
  const additions = references.filter((reference) => !current.includes(reference));
  return additions.length === 0 ? clone(block) : { ...clone(block), derivedFrom: [...current, ...additions] };
};

/**
 * Checks that `after` preserved everything `before` held: no contribution removed,
 * no actor or `created` replaced, no operation or evidence dropped, no unknown field lost,
 * no lineage removed. Returns a list of violations (empty when `after` is a faithful extension).
 */
export const preservationViolations = (before, after) => {
  const b = classify(before);
  if (b.verdict === "unsupported") return JSON.stringify(before) === JSON.stringify(after) ? [] : ["unsupported provenance was altered instead of carried verbatim"];
  if (!isObject(after) || !isObject(after.contributions)) return ["provenance was removed"];
  const violations = [];
  for (const [key, entry] of Object.entries(before.contributions ?? {})) {
    const later = after.contributions[key];
    if (later === undefined) { violations.push(`contribution ${key} was removed`); continue; }
    if (JSON.stringify(later.actor) !== JSON.stringify(entry.actor)) violations.push(`contribution ${key} actor was overwritten`);
    if (later.at !== entry.at) violations.push(`contribution ${key} time was rewritten`);
    for (const op of entry.operations) if (!later.operations.includes(op)) violations.push(`contribution ${key} lost operation ${op}`);
    for (const item of entry.evidence ?? []) if (!(later.evidence ?? []).includes(item)) violations.push(`contribution ${key} lost evidence ${item}`);
    for (const field of Object.keys(entry)) if (!(field in later)) violations.push(`contribution ${key} lost field ${field}`);
  }
  for (const field of Object.keys(before)) if (!(field in after)) violations.push(`block lost field ${field}`);
  for (const reference of before.derivedFrom ?? []) if (!(after.derivedFrom ?? []).includes(reference)) violations.push(`lineage ${reference} was removed`);
  const creatorsBefore = creators(before.contributions ?? {}).map(([key]) => key);
  const creatorsAfter = creators(after.contributions).map(([key]) => key);
  if (creatorsBefore.length === 1 && JSON.stringify(creatorsBefore) !== JSON.stringify(creatorsAfter)) violations.push("the originator was replaced");
  return violations;
};

/** The originating contribution (`created`), or undefined when origin is not recorded. */
export const originator = (block) => {
  const found = creators(block.contributions ?? {});
  return found.length === 1 ? { key: found[0][0], ...found[0][1] } : undefined;
};

/** Distinct actors whose operations changed the record after its creation. */
export const modifiers = (block) =>
  Object.entries(block.contributions ?? {})
    .filter(([, entry]) => !entry.operations.includes("created") && entry.operations.some((op) => MODIFYING_OPERATIONS.has(op) || (!KNOWN_OPERATIONS.includes(op))))
    .map(([key, entry]) => ({ key, actor: entry.actor }));

/** Contributions that played a role (for example 'validated'), in time order. */
export const withRole = (block, operation) =>
  Object.entries(block.contributions ?? {})
    .filter(([, entry]) => entry.operations.includes(operation))
    .sort(([, a], [, b]) => instant(a.at) - instant(b.at))
    .map(([key, entry]) => ({ key, actor: entry.actor, at: entry.at }));

/**
 * Lossless projection of an `echelon.execution-envelope/v1` actor onto a Praxis actor.
 * v1 `system` becomes `automation` (a deterministic non-agent process); `knownValue`
 * states map to the literal "unknown" (unknown) or omission (not-applicable, humans only).
 * Nothing is invented: model and runtime, which v1 cannot express, are "unknown".
 */
export const actorFromEnvelopeV1 = (envelopeActor) => {
  const value = (known) => (isObject(known) && known.state === "known" && isNonEmptyString(known.value) ? known.value : UNKNOWN);
  const kind = envelopeActor.kind === "system" ? "automation" : envelopeActor.kind;
  if (kind === "human") return { kind, id: value(envelopeActor.identity) };
  return { kind, id: value(envelopeActor.identity), provider: value(envelopeActor.provider), model: UNKNOWN, runtime: UNKNOWN };
};

/**
 * The contribution key for work carried by a v1 envelope, which names no executing system:
 * `EXT-run.<runId>` when a run id is known, otherwise `EXT-op.<operationId>`.
 * Characters a key cannot carry are escaped injectively as _xx (UTF-8 hex).
 */
export const keyFromEnvelopeV1 = (envelope) => {
  // Injective escaping: '_' and every character a key cannot carry become _xx (hex),
  // so two different ids can never map to the same key (contract 1.1).
  const safe = (text) => String(text).replace(/[^A-Za-z0-9.-]/g, (char) =>
    [...new TextEncoder().encode(char)].map((byte) => `_${byte.toString(16).padStart(2, "0")}`).join(""));
  const run = envelope.actor?.runId;
  return isObject(run) && run.state === "known" && isNonEmptyString(run.value)
    ? `EXT-run.${safe(run.value)}`
    : `EXT-op.${safe(envelope.operationId)}`;
};
