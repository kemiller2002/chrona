// Chrona time.record v1 intake: provenance portion (CHR-PROV-001..011),
// Praxis contract revision 1.1.
import test from "node:test";
import assert from "node:assert/strict";
import {
  receiveTimeObservation, candidateFromObservation, acceptCandidate, toInterchange, provenanceStatus,
  principalToActor, principalKindsFor, invokerKey, chronaKey, recordedDurationSeconds, CHRONA_ACTOR, UNKNOWN_ACTOR,
} from "../../lib/time-observation-intake.mjs";
import { classify, originator, preservationViolations, withRole } from "../../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const AGENT = { kind: "agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" };
const OTHER_AGENT = { kind: "agent", id: "anthropic/claude-code", provider: "anthropic", model: "unknown", runtime: "claude-code" };
const HUMAN = { kind: "human", id: "kevin" };
const CI = { kind: "automation", id: "github/github-actions", provider: "github", model: "unknown", runtime: "github-actions" };
const EXE1 = "EXE-20260926T080000000Z-a1a1a1a1";
const EXE2 = "EXE-20260926T100000000Z-a2a2a2a2";
const CTB = "CTB-20260926-5f2e19aa";
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
const candidateOf = (record, operationId = "op-2", at = t(9, 2)) => candidateFromObservation(record, { operationId, at });
const deepFreeze = (value) => {
  if (value && typeof value === "object") { Object.values(value).forEach(deepFreeze); Object.freeze(value); }
  return value;
};
const upstream = () => ({
  schema: "praxis.provenance/1",
  "x-upstream": { keep: true },
  contributions: {
    "EXE-20260926T070000000Z-99999999": { operations: ["created", "discovered"], at: t(7), actor: OTHER_AGENT, attestation: { sig: "abc" } },
    "EXT-vigila.op-1": { operations: ["transformed", "x-tagged"], at: t(7, 30), actor: { kind: "automation", id: "echelon/vigila", provider: "echelon", model: "unknown", runtime: "vigila" } },
  },
  derivedFrom: ["vigila:item/IT-4"],
});

test("time.record creates a new record: an agent execution is the creator and measurer, keyed by its EXE", () => {
  const received = receive();
  assert.ok(received.ok, JSON.stringify(received.errors));
  const { provenance } = received.record;
  assert.equal(classify(provenance).verdict, "supported");
  assert.deepEqual(Object.keys(provenance.contributions), [EXE1]);
  assert.deepEqual(provenance.contributions[EXE1].operations, ["created", "measured"]);
  assert.deepEqual(provenance.contributions[EXE1].actor, AGENT);
  assert.deepEqual(provenance.derivedFrom, ["praxis:observation/OBS-1"]);
  assert.equal(received.record.observation.performer.execution, EXE1);
  assert.equal(received.record.sourceProvenance, undefined);
});

test("the EXE and actor survive candidate and entry; approval never transfers authorship", () => {
  const { candidate } = candidateOf(receive().record);
  assert.equal(candidate.execution, EXE1);
  assert.deepEqual(candidate.actor, AGENT);
  assert.equal(candidate.exactDurationSeconds, 45 * 60);
  assert.equal(originator(candidate.provenance).key, EXE1);
  assert.deepEqual(candidate.provenance.contributions[chronaKey("op-2")].actor, CHRONA_ACTOR);
  const accepted = acceptCandidate(candidate, { key: CTB, actor: HUMAN, at: t(10), entryId: "ACT-1" });
  assert.ok(accepted.ok, JSON.stringify(accepted.errors));
  assert.equal(accepted.entry.execution, EXE1);
  assert.equal(originator(accepted.entry.provenance).actor.id, "openai/codex");
  assert.deepEqual(withRole(accepted.entry.provenance, "approved").map((item) => item.actor.id), ["kevin"]);
  assert.deepEqual(Object.keys(accepted.entry.provenance.contributions), [EXE1, "EXT-chrona.op-2", CTB], "agent, Chrona, and human contributions coexist");
  assert.deepEqual(preservationViolations(candidate.provenance, accepted.entry.provenance), []);
});

test("a received block is source provenance: stored verbatim, never appended to; lineage carried", () => {
  const source = upstream();
  const received = receive(observation({ provenance: source }), request({ invoker: { actor: CI, execution: "EXT-github-actions.run-5" } }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  assert.deepEqual(received.record.sourceProvenance, upstream(), "carried verbatim");
  assert.equal(received.record.ingestion.sourceProvenance, "supported");
  const own = received.record.provenance;
  assert.deepEqual(Object.keys(own.contributions), ["EXT-github-actions.run-5"], "the source's discoverer is not an author of the new record");
  assert.deepEqual(own.contributions["EXT-github-actions.run-5"].operations, ["created"], "the invoker did not measure the agent's time");
  assert.deepEqual(own.derivedFrom, ["vigila:item/IT-4", "praxis:observation/OBS-1"]);
  assert.deepEqual(received.record.observation.performer.actor, AGENT, "the performer is not the transporter");
  const { candidate } = candidateOf(received.record);
  assert.deepEqual(candidate.sourceProvenance, upstream());
  const { entry } = acceptCandidate(candidate, { key: CTB, actor: HUMAN, at: t(10), entryId: "ACT-9" });
  assert.deepEqual(entry.sourceProvenance, upstream());
});

test("two executions of one agent stay distinct records and contributions", () => {
  const one = receive(observation({ observationId: "OBS-1" }), request({ operationId: "op-1" }));
  const two = receive(
    observation({ observationId: "OBS-2", performer: { actor: AGENT, execution: EXE2 } }),
    request({ operationId: "op-2", invoker: { actor: AGENT, execution: EXE2 } }));
  assert.deepEqual(Object.keys(one.record.provenance.contributions), [EXE1]);
  assert.deepEqual(Object.keys(two.record.provenance.contributions), [EXE2]);
  const a = candidateOf(one.record, "op-3").candidate;
  const b = candidateOf(two.record, "op-4").candidate;
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
  assert.deepEqual(received.record.provenance.contributions[key].operations, ["created", "measured"]);
  assert.equal(received.record.provenance.contributions[key].actor.provider, undefined, "humans carry no provider/model/runtime");
  const { candidate } = candidateOf(received.record, "op-9", t(12));
  assert.deepEqual(candidate.principalKinds, ["Human"]);
  assert.equal(candidate.execution, undefined, "no execution is invented for a human");
  assert.equal(candidate.exactDurationSeconds, 5400);
});

test("automation (CI) performer maps to Service/Integration and keeps its foreign execution", () => {
  const received = receive(
    observation({ performer: { actor: CI, execution: "EXT-github-actions.run-777-1" } }),
    request({ invoker: { actor: CI, execution: "EXT-github-actions.run-777-1" } }));
  const { candidate } = candidateOf(received.record);
  assert.deepEqual(candidate.principalKinds, ["Service", "Integration"]);
  assert.equal(candidate.execution, "EXT-github-actions.run-777-1");
});

test("an undeclared invoker is recorded as unknown, never guessed; unknown performer yields no entry", () => {
  const received = receive(observation({ performer: { actor: UNKNOWN_ACTOR } }), request({ invoker: undefined }));
  assert.ok(received.ok, JSON.stringify(received.errors));
  assert.equal(received.record.ingestion.invokerDeclared, false);
  assert.deepEqual(received.record.provenance.contributions["EXT-op.op-1"].actor, UNKNOWN_ACTOR);
  const result = candidateOf(received.record);
  assert.equal(result.ok, false);
  assert.equal(result.disposition, "NeedsAttention");
  assert.match(result.reasons.join(" "), /performer is unknown/);
});

test("no time entry is fabricated when duration evidence is missing", () => {
  const { measurement, ...withoutMeasurement } = observation();
  const received = receive(withoutMeasurement);
  assert.ok(received.ok, "the observation itself is preserved");
  assert.deepEqual(received.record.provenance.contributions[EXE1].operations, ["created"], "nothing was measured");
  const result = candidateOf(received.record);
  assert.equal(result.ok, false);
  assert.equal(result.disposition, "NeedsAttention");
  assert.match(result.reasons[0], /no recorded duration evidence/);
  assert.equal(result.candidate, undefined);
  assert.equal(recordedDurationSeconds(undefined), undefined);
  assert.equal(acceptCandidate({ disposition: "Pending", observationId: "OBS-1" }, { key: CTB, actor: HUMAN, at: t(10), entryId: "ACT-9" }).errors[0].code, "no-duration-evidence");
});

test("guessed or unsourced durations are rejected; an observed zero is real", () => {
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
  assert.equal(receive(observation({ measurement: { kind: "duration", seconds: 0, source: "timer" } })).ok, true);
});

test("malformed received provenance rejects the observation at the boundary", () => {
  const malformed = { schema: "praxis.provenance/1", contributions: { "EXE-1": { operations: [], at: t(8), actor: AGENT } } };
  const result = receive(observation({ provenance: malformed }));
  assert.equal(result.ok, false);
  assert.equal(result.errors[0].code, "malformed-provenance");
  assert.match(result.errors[0].message, /^provenance: /);
  const agentWithoutExecution = { contributions: { "CTB-x": { operations: ["created"], at: t(8), actor: AGENT } } };
  assert.equal(receive(observation({ provenance: agentWithoutExecution })).errors[0].code, "malformed-provenance");
});

test("credential-like values in the observation or the request are refused, never stored", () => {
  assert.equal(receive(observation({ externalReference: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" })).errors[0].code, "credential");
  const leakyInvoker = request({ invoker: { actor: { kind: "human", id: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } } });
  assert.equal(receive(observation(), leakyInvoker).errors[0].code, "credential");
});

test("regression: a reviewer id carrying a token is refused by the library, and nothing is stored", () => {
  const { candidate } = candidateOf(receive().record);
  const before = JSON.stringify(candidate);
  const result = acceptCandidate(candidate, { key: CTB, actor: { kind: "human", id: "ghp_abcdefghijklmnopqrstuvwxyz0123456789" }, at: t(10), entryId: "ACT-1" });
  assert.equal(result.ok, false);
  assert.equal(result.entry, undefined);
  assert.equal(result.errors[0].code, "provenance-refused");
  assert.match(result.errors[0].message, /credential/);
  assert.equal(JSON.stringify(candidate), before);
  const bearer = acceptCandidate(candidate, { key: CTB, actor: { kind: "human", id: "kevin", note: "Bearer abcdefghijklmnopqrstuvwxyz0123" }, at: t(10), entryId: "ACT-1" });
  assert.equal(bearer.ok, false);
});

test("regression: a back-dated approval, a re-attribution, and an unknown actor extending a known entry are refused", () => {
  const { candidate } = candidateOf(receive().record);
  const backdated = acceptCandidate(candidate, { key: CTB, actor: HUMAN, at: t(7), entryId: "ACT-1" });
  assert.equal(backdated.ok, false);
  assert.equal(backdated.errors[0].code, "provenance-refused");
  const forged = acceptCandidate(candidate, { key: EXE1, actor: HUMAN, at: t(10), entryId: "ACT-1" });
  assert.equal(forged.ok, false);
  assert.match(forged.errors[0].message, /refusing to re-attribute/);
  const unknownExtends = acceptCandidate(candidate, { key: EXE1, actor: { kind: "agent", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" }, at: t(10), entryId: "ACT-1" });
  assert.equal(unknownExtends.ok, false);
  assert.match(unknownExtends.errors[0].message, /unknown identity cannot extend/);
});

test("contract 1.1: calendar-invalid timestamps and null values are rejected, never treated as absent", () => {
  for (const [obs, req, pattern] of [
    [observation({ recordedAt: "2026-02-30T00:00:00.000Z" }), request(), /recordedAt/],
    [observation(), request({ receivedAt: "2026-09-26T24:00:00.000Z" }), /receivedAt/],
    [observation({ measurement: { kind: "interval", startedAt: "2026-13-01T00:00:00.000Z", endedAt: t(9), source: "timer" } }), request(), /startedAt/],
    [observation({ workItemId: null }), request(), /workItemId/],
    [observation({ externalReference: null }), request(), /externalReference/],
    [observation({ measurement: null }), request(), /measurement/],
    [observation({ performer: { actor: AGENT, execution: null } }), request(), /performer\.execution/],
    [observation(), request({ invoker: { actor: AGENT, execution: null } }), /invoker\.execution/],
  ]) {
    const result = receive(obs, req);
    assert.equal(result.ok, false, pattern.source);
    assert.match(result.errors.map((item) => item.message).join("; "), pattern);
  }
  const nullProvenance = receive(observation({ provenance: null }));
  assert.equal(nullProvenance.errors[0].code, "malformed-provenance");
  const nullLast = { contributions: { [EXE1]: { operations: ["created"], at: t(8), last: null, actor: AGENT } } };
  assert.equal(receive(observation({ provenance: nullLast })).errors[0].code, "malformed-provenance");
  assert.equal(receive(observation({ provenance: { schema: "praxis.provenance/1\n", contributions: {} } })).errors[0].code, "malformed-provenance");
  assert.equal(receive(observation({ recordedAt: "9999-12-31T23:59:59.999999999Z" })).ok, true);
});

test("contract 1.1: operation-derived keys are escaped injectively", () => {
  assert.equal(invokerKey({ operationId: "op 1" }), "EXT-op.op_201");
  assert.equal(invokerKey({ operationId: "op_1" }), "EXT-op.op_5f1");
  assert.equal(chronaKey("gh/99"), "EXT-chrona.gh_2f99");
  const received = receive(observation(), request({ operationId: "op 1", invoker: undefined }));
  assert.deepEqual(Object.keys(received.record.provenance.contributions), ["EXT-op.op_201"]);
});

test("invalid observations are distinguishable from valid ones and name the problem", () => {
  for (const [obs, pattern] of [
    [{ ...observation(), observationId: "" }, /observationId/],
    [{ ...observation(), performer: { actor: { kind: "agent", id: "x" } } }, /performer\.actor\.provider/],
    [{ ...observation(), performer: { actor: AGENT, execution: "run-1" } }, /performer\.execution/],
  ]) {
    const result = receive(obs);
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
  assert.equal(received.record.ingestion.sourceProvenance, "unsupported");
  assert.deepEqual(received.record.sourceProvenance, future);
  assert.deepEqual(received.record.provenance.derivedFrom, ["praxis:observation/OBS-1"]);
  const { candidate } = candidateOf(received.record);
  const { entry } = acceptCandidate(candidate, { key: CTB, actor: HUMAN, at: t(10), entryId: "ACT-2" });
  assert.deepEqual(entry.sourceProvenance, future, "nothing is merged into an unsupported block");
});

test("replaying the same request is deterministic (idempotent)", () => {
  assert.deepEqual(receive(), receive());
  const source = upstream();
  const once = receive(observation({ provenance: source }));
  const twice = receive(observation({ provenance: once.record.sourceProvenance }));
  assert.deepEqual(twice.record, once.record);
});

test("a legacy observation without provenance stays valid; its source reads as unattributed", () => {
  const received = receive(observation(), request({ invoker: undefined }));
  assert.ok(received.ok);
  assert.equal(received.record.ingestion.sourceProvenance, "absent");
  assert.equal(received.record.sourceProvenance, undefined);
  assert.equal(provenanceStatus(undefined), "unattributed");
});

test("round trip: export classifies as supported and survives JSON and re-intake as source provenance", () => {
  const { candidate } = candidateOf(receive().record);
  const exported = toInterchange(JSON.parse(JSON.stringify(candidate)));
  assert.equal(classify(exported).verdict, "supported");
  assert.deepEqual(exported, candidate.provenance);
  const reobserved = receive(observation({ provenance: exported }), request({ operationId: "op-3", invoker: { actor: CI, execution: "EXT-github-actions.run-9" } }));
  assert.deepEqual(reobserved.record.sourceProvenance, exported);
  assert.deepEqual(preservationViolations(exported, reobserved.record.sourceProvenance), []);
});

test("principal model maps onto the Praxis actor without a second identity model", () => {
  assert.deepEqual(principalToActor({ kind: "Human", id: "kevin" }).actor, HUMAN);
  assert.deepEqual(principalToActor({ kind: "Agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" }).actor, AGENT);
  assert.deepEqual(principalToActor({ kind: "Agent", id: "a" }).actor, { kind: "agent", id: "a", provider: "unknown", model: "unknown", runtime: "unknown" });
  assert.equal(principalToActor({ kind: "Service", id: "echelon/summa" }).actor.kind, "automation");
  assert.equal(principalToActor({ kind: "Integration", id: "jira-sync" }).actor.kind, "automation");
  assert.equal(principalToActor({ kind: "Robot", id: "r" }).ok, false);
  assert.deepEqual(principalKindsFor(UNKNOWN_ACTOR), []);
});

test("intake functions never mutate their inputs", () => {
  const obs = deepFreeze(observation({ provenance: upstream() }));
  const received = receiveTimeObservation(obs, deepFreeze(request()));
  const { candidate } = candidateFromObservation(deepFreeze(received.record), { operationId: "op-2", at: t(9, 5) });
  assert.ok(acceptCandidate(deepFreeze(candidate), { key: CTB, actor: HUMAN, at: t(10), entryId: "ACT-3" }).ok);
});
