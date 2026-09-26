// Chrona time.record v1 intake: provenance portion (CHR-PROV-001..010).
import test from "node:test";
import assert from "node:assert/strict";
import {
  receiveTimeObservation, candidateFromObservation, acceptCandidate, toInterchange, provenanceStatus,
  principalToActor, principalKindsFor, invokerKey, chronaKey, recordedDurationSeconds, CHRONA_ACTOR,
} from "../../lib/time-observation-intake.mjs";
import { classify, originator, preservationViolations, withRole } from "../../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const AGENT = { kind: "agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" };
const OTHER_AGENT = { kind: "agent", id: "anthropic/claude-code", provider: "anthropic", model: "unknown", runtime: "claude-code" };
const HUMAN = { kind: "human", id: "kevin" };
const CI = { kind: "automation", id: "github/github-actions", provider: "github", model: "unknown", runtime: "github-actions" };
const UNKNOWN = { kind: "unknown", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" };
const EXE1 = "EXE-20260926T080000000Z-a1a1a1a1";
const EXE2 = "EXE-20260926T100000000Z-a2a2a2a2";
const t = (hour, minute = 0) => `2026-09-26T${String(hour).padStart(2, "0")}:${String(minute).padStart(2, "0")}:00.000Z`;

const observation = (overrides = {}) => ({
  schemaVersion: "chrona.time-observation/1",
  observationId: "OBS-1",
  sourceSystem: "praxis",
  workItemId: "praxis:FEAT-ECHELON-PROVENANCE",
  recordedAt: t(9),
  performer: { actor: AGENT, execution: EXE1 },
  measurement: { kind: "interval", startedAt: t(8), endedAt: t(8, 45), source: "telemetry" },
  ...overrides,
});
const request = (overrides = {}) => ({ operationId: "op-1", receivedAt: t(9, 1), invoker: { actor: AGENT, execution: EXE1 }, ...overrides });
const receive = (obs = observation(), req = request()) => receiveTimeObservation(obs, req);
const deepFreeze = (value) => {
  if (value && typeof value === "object") { Object.values(value).forEach(deepFreeze); Object.freeze(value); }
  return value;
};

test("an observation from an agent execution keeps the EXE id and actor through candidate and entry", () => {
  const received = receive();
  assert.ok(received.ok, JSON.stringify(received.errors));
  const block = received.record.observation.provenance;
  assert.equal(classify(block).verdict, "supported");
  assert.deepEqual(block.contributions[EXE1].actor, AGENT);
  assert.deepEqual(block.contributions[EXE1].operations, ["created"]);
  assert.equal(received.record.observation.performer.execution, EXE1);

  const { candidate } = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 2) });
  assert.equal(candidate.execution, EXE1);
  assert.deepEqual(candidate.actor, AGENT);
  assert.equal(candidate.actorId, "openai/codex");
  assert.equal(candidate.workItemId, "praxis:FEAT-ECHELON-PROVENANCE");
  assert.equal(candidate.exactDurationSeconds, 45 * 60);
  assert.equal(originator(candidate.provenance).key, EXE1, "the agent execution stays the originator");
  assert.deepEqual(candidate.provenance.contributions[chronaKey("op-2")].actor, CHRONA_ACTOR);
  assert.deepEqual(candidate.provenance.derivedFrom, ["chrona:observation/OBS-1"]);
  assert.deepEqual(preservationViolations(block, candidate.provenance), []);

  const accepted = acceptCandidate(candidate, { key: "CTB-20260926-5f2e19aa", actor: HUMAN, at: t(10), entryId: "ACT-1" });
  assert.ok(accepted.ok, accepted.error);
  assert.equal(accepted.entry.execution, EXE1);
  assert.equal(originator(accepted.entry.provenance).actor.id, "openai/codex", "approval never transfers authorship");
  assert.deepEqual(withRole(accepted.entry.provenance, "approved").map((item) => item.actor.id), ["kevin"]);
  assert.deepEqual(preservationViolations(candidate.provenance, accepted.entry.provenance), []);
});

test("two executions of one agent stay distinct records and contributions", () => {
  const one = receive(observation({ observationId: "OBS-1" }), request({ operationId: "op-1" }));
  const two = receive(
    observation({ observationId: "OBS-2", performer: { actor: AGENT, execution: EXE2 } }),
    request({ operationId: "op-2", invoker: { actor: AGENT, execution: EXE2 } }));
  assert.deepEqual(Object.keys(one.record.observation.provenance.contributions), [EXE1]);
  assert.deepEqual(Object.keys(two.record.observation.provenance.contributions), [EXE2]);
  const a = candidateFromObservation(one.record, { operationId: "op-3", at: t(11) }).candidate;
  const b = candidateFromObservation(two.record, { operationId: "op-4", at: t(11) }).candidate;
  assert.equal(a.actorId, b.actorId);
  assert.notEqual(a.execution, b.execution);
});

test("human-entered time: a human actor, manual-entry evidence, no execution", () => {
  const received = receive(
    observation({ sourceSystem: "chrona", performer: { actor: HUMAN }, measurement: { kind: "duration", seconds: 5400, source: "manual-entry" } }),
    request({ invoker: { actor: HUMAN }, operationId: "manual-7" }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  const key = invokerKey({ operationId: "manual-7" });
  assert.equal(key, "EXT-op.manual-7");
  assert.deepEqual(received.record.observation.provenance.contributions[key].actor, HUMAN);
  assert.equal(received.record.observation.provenance.contributions[key].actor.provider, undefined, "humans carry no provider/model/runtime");
  const { candidate } = candidateFromObservation(received.record, { operationId: "op-9", at: t(12) });
  assert.deepEqual(candidate.principalKinds, ["Human"]);
  assert.equal(candidate.execution, undefined, "no execution is invented for a human");
  assert.equal(candidate.exactDurationSeconds, 5400);
});

test("automation (CI) performer maps to Service/Integration and keeps its foreign execution", () => {
  const received = receive(
    observation({ performer: { actor: CI, execution: "EXT-github-actions.run-777-1" } }),
    request({ invoker: { actor: CI, execution: "EXT-github-actions.run-777-1" } }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  const { candidate } = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  assert.deepEqual(candidate.principalKinds, ["Service", "Integration"]);
  assert.equal(candidate.execution, "EXT-github-actions.run-777-1");
  assert.equal(candidate.actor.kind, "automation");
});

test("unknown performer: observation preserved, never attributed as a time entry", () => {
  const received = receive(observation({ performer: { actor: UNKNOWN } }), request({ invoker: undefined }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  assert.equal(received.record.observation.provenance, undefined, "nothing is appended for an undeclared invoker");
  assert.equal(received.record.ingestion.provenance, "unattributed");
  const result = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  assert.equal(result.ok, false);
  assert.equal(result.disposition, "NeedsAttention");
  assert.match(result.reasons.join(" "), /performer is unknown/);
});

test("no time entry is fabricated when duration evidence is missing", () => {
  const { measurement, ...withoutMeasurement } = observation();
  const received = receive(withoutMeasurement);
  assert.ok(received.ok, "the observation itself is preserved");
  assert.ok(received.record.observation.provenance.contributions[EXE1], "the agent execution is still recorded");
  const result = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  assert.equal(result.ok, false);
  assert.equal(result.disposition, "NeedsAttention");
  assert.match(result.reasons[0], /no recorded duration evidence/);
  assert.equal(result.candidate, undefined);
  assert.equal(recordedDurationSeconds(undefined), undefined);
  const refused = acceptCandidate({ disposition: "Pending", observationId: "OBS-1" }, { key: "CTB-1", actor: HUMAN, at: t(10), entryId: "ACT-9" });
  assert.equal(refused.ok, false);
});

test("guessed or unsourced durations are rejected", () => {
  for (const measurement of [
    { kind: "duration", seconds: 60, source: "estimated" },
    { kind: "duration", seconds: -1, source: "timer" },
    { kind: "interval", startedAt: t(9), endedAt: t(8), source: "timer" },
    { kind: "guess", seconds: 60, source: "timer" },
  ]) {
    const result = receive(observation({ measurement }));
    assert.equal(result.ok, false, JSON.stringify(measurement));
    assert.equal(result.errors[0].code, "invalid-observation");
  }
  assert.equal(receive(observation({ measurement: { kind: "duration", seconds: 0, source: "timer" } })).ok, true, "an observed zero is real");
});

test("malformed provenance rejects the observation at the boundary with a clear error", () => {
  const malformed = { schema: "praxis.provenance/1", contributions: { "EXE-1": { operations: [], at: t(8), actor: AGENT } } };
  const result = receive(observation({ provenance: malformed }));
  assert.equal(result.ok, false);
  assert.equal(result.errors[0].code, "malformed-provenance");
  assert.match(result.errors[0].message, /^provenance: /);

  const agentWithoutExecution = { contributions: { "CTB-x": { operations: ["created"], at: t(8), actor: AGENT } } };
  assert.equal(receive(observation({ provenance: agentWithoutExecution })).errors[0].code, "malformed-provenance");
});

test("credential-like values anywhere are refused, never stored", () => {
  const leaked = observation({ externalReference: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" });
  const result = receive(leaked);
  assert.equal(result.ok, false);
  assert.equal(result.errors[0].code, "credential");
});

test("invalid observations are distinguishable from valid ones and name the problem", () => {
  const cases = [
    [{ ...observation(), observationId: "" }, /observationId/],
    [{ ...observation(), performer: { actor: { kind: "agent", id: "x" } } }, /performer\.actor\.provider/],
    [{ ...observation(), performer: { actor: AGENT, execution: "run-1" } }, /performer\.execution/],
    [{ ...observation(), recordedAt: "yesterday" }, /recordedAt/],
  ];
  for (const [obs, pattern] of cases) {
    const result = receive(obs);
    assert.equal(result.ok, false);
    assert.equal(result.errors[0].code, "invalid-observation");
    assert.match(result.errors.map((item) => item.message).join("; "), pattern);
  }
  assert.equal(receive(observation({ schemaVersion: "chrona.time-observation/2" })).errors[0].code, "unsupported-observation-version");
  assert.equal(receive(observation(), { receivedAt: t(9) }).errors[0].code, "invalid-request");
});

test("an unsupported provenance major is carried verbatim through candidate and entry", () => {
  const future = { schema: "praxis.provenance/2", history: [{ who: "someone", what: "new-shape" }] };
  const received = receive(observation({ provenance: future }));
  assert.ok(received.ok);
  assert.equal(received.record.ingestion.provenance, "unsupported");
  assert.deepEqual(received.record.observation.provenance, future);
  const { candidate } = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  assert.deepEqual(candidate.provenance, future);
  const { entry } = acceptCandidate(candidate, { key: "CTB-1", actor: HUMAN, at: t(10), entryId: "ACT-2" });
  assert.deepEqual(entry.provenance, future, "nothing is merged into an unsupported block");
  assert.deepEqual(toInterchange(entry), future);
});

test("an upstream block is preserved: unknown fields, other contributors, lineage; invoker measures", () => {
  const upstream = {
    schema: "praxis.provenance/1",
    "x-upstream": { keep: true },
    contributions: {
      [EXE1]: { operations: ["created"], at: t(8), actor: AGENT, attestation: { sig: "abc" } },
      "EXT-vigila.op-1": { operations: ["transformed", "x-tagged"], at: t(8, 30), actor: { kind: "automation", id: "echelon/vigila", provider: "echelon", model: "unknown", runtime: "vigila" } },
    },
    derivedFrom: ["praxis:FEAT-ECHELON-PROVENANCE"],
  };
  const received = receive(observation({ provenance: upstream }), request({ invoker: { actor: OTHER_AGENT, execution: EXE2 } }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  const block = received.record.observation.provenance;
  assert.deepEqual(preservationViolations(upstream, block), []);
  assert.deepEqual(block.contributions[EXE2].operations, ["measured"], "the invoker measured; it did not author the record");
  assert.equal(originator(block).actor.id, "openai/codex");
  assert.equal(block["x-upstream"].keep, true);
});

test("a transporting actor can never re-attribute an execution", () => {
  const upstream = { schema: "praxis.provenance/1", contributions: { [EXE1]: { operations: ["created"], at: t(8), actor: AGENT } } };
  const forged = receive(observation({ provenance: upstream }), request({ invoker: { actor: OTHER_AGENT, execution: EXE1 } }));
  assert.equal(forged.ok, false);
  assert.equal(forged.errors[0].code, "provenance-conflict");
  assert.match(forged.errors[0].message, /refusing to re-attribute/);
  const transported = receive(observation({ provenance: upstream }), request({ invoker: { actor: CI, execution: "EXT-github-actions.run-5" } }));
  assert.ok(transported.ok);
  assert.deepEqual(transported.record.observation.provenance.contributions[EXE1].actor, AGENT, "the original actor is kept");
  assert.deepEqual(transported.record.observation.performer.actor, AGENT, "the performer is not the transporter");
});

test("replaying the same operation is idempotent", () => {
  const first = receive();
  const again = receiveTimeObservation(first.record.observation, request());
  assert.deepEqual(again.record.observation, first.record.observation);
});

test("a legacy observation without provenance stays valid and reads as unattributed", () => {
  const received = receive(observation(), request({ invoker: undefined }));
  assert.ok(received.ok);
  assert.equal(received.record.ingestion.provenance, "unattributed");
  assert.equal(provenanceStatus(undefined), "unattributed");
  const { candidate } = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  assert.equal(candidate.provenance, undefined, "Chrona does not invent a history");
  assert.equal(toInterchange(candidate), undefined);
});

test("round trip: export classifies as supported and re-receiving preserves the history", () => {
  const received = receive();
  const { candidate } = candidateFromObservation(received.record, { operationId: "op-2", at: t(9, 5) });
  const wire = JSON.parse(JSON.stringify(candidate));
  const exported = toInterchange(wire);
  assert.equal(classify(exported).verdict, "supported");
  assert.deepEqual(exported, candidate.provenance);
  const reobserved = receive(observation({ provenance: exported }), request({ operationId: "op-3", invoker: { actor: CI, execution: "EXT-github-actions.run-9" } }));
  assert.ok(reobserved.ok);
  assert.deepEqual(preservationViolations(exported, reobserved.record.observation.provenance), []);
});

test("human correction of agent time: both contributions coexist without re-attribution", () => {
  const received = receive();
  const human = { operationId: "fix-1", receivedAt: t(12), invoker: { actor: HUMAN, execution: undefined } };
  const corrected = receiveTimeObservation({ ...received.record.observation, measurement: { kind: "duration", seconds: 1800, source: "manual-entry" } }, human);
  assert.ok(corrected.ok);
  const block = corrected.record.observation.provenance;
  assert.deepEqual(Object.keys(block.contributions), [EXE1, "EXT-op.fix-1"]);
  assert.equal(originator(block).actor.kind, "agent");
});

test("principal model maps onto the Praxis actor without a second identity model", () => {
  assert.deepEqual(principalToActor({ kind: "Human", id: "kevin" }).actor, HUMAN);
  assert.deepEqual(principalToActor({ kind: "Agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" }).actor, AGENT);
  assert.deepEqual(principalToActor({ kind: "Agent", id: "a" }).actor, { kind: "agent", id: "a", provider: "unknown", model: "unknown", runtime: "unknown" });
  assert.equal(principalToActor({ kind: "Service", id: "echelon/summa" }).actor.kind, "automation");
  assert.equal(principalToActor({ kind: "Integration", id: "jira-sync" }).actor.kind, "automation");
  assert.equal(principalToActor({ kind: "Robot", id: "r" }).ok, false);
  assert.deepEqual(principalKindsFor(UNKNOWN), []);
});

test("intake functions never mutate their inputs", () => {
  const obs = deepFreeze(observation());
  const req = deepFreeze(request());
  const received = receiveTimeObservation(obs, req);
  const record = deepFreeze(received.record);
  const { candidate } = candidateFromObservation(record, { operationId: "op-2", at: t(9, 5) });
  deepFreeze(candidate);
  assert.ok(acceptCandidate(candidate, { key: "CTB-1", actor: HUMAN, at: t(10), entryId: "ACT-3" }).ok);
});
