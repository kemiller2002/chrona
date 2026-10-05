import test from "node:test";
import assert from "node:assert/strict";
import { decodeLabel, eventToCommand, initialModel, transition } from "../dist/domain.js";
import { project } from "../dist/projection.js";
import { REQUIREMENTS, step } from "../dist/transport.js";
import { CORE_CONTRACT_IDENTITY, PROTOCOL_MINOR, PROTOCOL_VERSION } from "@echelon-foundry/limen/protocol";

const draft = (model, value) => transition(model, { kind: "DraftChanged", value });

test("a blank label is rejected by the guard", () => {
  const r = decodeLabel("   ");
  assert.equal(typeof r, "object");
  assert.match(r.reason, /required/);
});

test("an over-long label is rejected by the guard", () => {
  assert.match(decodeLabel("x".repeat(25)).reason, /at most 24/);
});

test("a valid label is trimmed", () => {
  assert.equal(decodeLabel("  hello  "), "hello");
});

test("draft change moves Empty -> Editing, and invalid input -> Invalid", () => {
  const ok = draft(initialModel(), "note");
  assert.equal(ok.accepted, true);
  assert.equal(ok.model.phase.kind, "Editing");

  const bad = draft(initialModel(), "");
  assert.equal(bad.model.phase.kind, "Invalid");
});

test("Save is illegal from Empty and rejected from Invalid with the reason", () => {
  const fromEmpty = transition(initialModel(), { kind: "Save", correlationId: "c1" });
  assert.equal(fromEmpty.accepted, false);
  assert.equal(fromEmpty.error.kind, "IllegalFromCurrentPhase");

  const invalid = draft(initialModel(), "").model;
  const fromInvalid = transition(invalid, { kind: "Save", correlationId: "c1" });
  assert.equal(fromInvalid.accepted, false);
  assert.equal(fromInvalid.error.kind, "InvalidLabel");
});

test("Save requests exactly one Storage set effect and does not perform it", () => {
  const editing = draft(initialModel(), "note").model;
  const saving = transition(editing, { kind: "Save", correlationId: "c1" });
  assert.equal(saving.accepted, true);
  assert.deepEqual(saving.effects, [
    { kind: "Storage", correlationId: "c1", operation: "set", key: "chrona.kernel-slice.label", value: "note" },
  ]);
  assert.equal(saving.model.phase.kind, "Saving");
});

test("a stale effect result is rejected rather than applied", () => {
  const saving = transition(draft(initialModel(), "note").model, { kind: "Save", correlationId: "c1" }).model;
  const stale = transition(saving, {
    kind: "RecordStorage",
    correlationId: "c-other",
    outcome: { kind: "Success", value: null },
  });
  assert.equal(stale.accepted, false);
  assert.equal(stale.error.kind, "StaleEffectResult");
  assert.equal(stale.model.phase.kind, "Saving", "phase must be unchanged");
});

test("a storage failure lands in StorageFailed, not silently ignored", () => {
  const saving = transition(draft(initialModel(), "note").model, { kind: "Save", correlationId: "c1" }).model;
  const failed = transition(saving, {
    kind: "RecordStorage",
    correlationId: "c1",
    outcome: { kind: "Failure", reason: "quota-exceeded" },
  });
  assert.equal(failed.model.phase.kind, "StorageFailed");
  assert.equal(failed.model.phase.reason, "quota-exceeded");
});

test("load of an absent key yields Absent, not Loaded(null)", () => {
  const loading = transition(initialModel(), { kind: "Load", correlationId: "c9" }).model;
  const done = transition(loading, {
    kind: "RecordStorage",
    correlationId: "c9",
    outcome: { kind: "Success", value: null },
  });
  assert.equal(done.model.phase.kind, "Absent");
});

test("projection is total over every phase", () => {
  const phases = [
    { kind: "Empty" },
    { kind: "Editing", draft: "a" },
    { kind: "Invalid", draft: "", reason: "r" },
    { kind: "Saving", label: "a", correlationId: "c" },
    { kind: "Saved", label: "a" },
    { kind: "Loading", correlationId: "c" },
    { kind: "Loaded", label: "a" },
    { kind: "Absent" },
    { kind: "StorageFailed", reason: "unavailable" },
  ];
  for (const phase of phases) {
    const view = project({ phase, log: [], sequence: 0 });
    assert.equal(typeof view.statusText, "string");
    assert.notEqual(view.statusText, "", `${phase.kind} must project a status`);
    assert.equal(Array.isArray(view.log), true);
  }
});

test("ViewState log items are flat records of primitives, as ViewItem requires", () => {
  const saved = transition(draft(initialModel(), "note").model, { kind: "Save", correlationId: "c1" }).model;
  for (const item of project(saved).log) {
    for (const value of Object.values(item)) {
      assert.ok(["string", "number", "boolean"].includes(typeof value));
    }
  }
});

test("unknown event names are rejected rather than silently ignored", () => {
  assert.throws(() => eventToCommand({ kind: "Event", name: "nope" }, "c1"), /Unknown event name/);
});

// Protocol 1.4 messages, shaped as the Limen 0.7.0 kernel sends them.
const location = { origin: "http://127.0.0.1:4173", path: "/verification/kernel-slice/index.html", query: "", hash: "" };
const offer = {
  protocol: { major: PROTOCOL_VERSION, minor: PROTOCOL_MINOR },
  contract: { ...CORE_CONTRACT_IDENTITY },
  capabilities: [],
};
const initialize = (overrides = {}) => ({
  kind: "Initialize",
  protocolVersion: PROTOCOL_VERSION,
  capabilities: ["Http", "Storage", "Clipboard", "Navigation"],
  location,
  handshake: offer,
  ...overrides,
});
const savingModel = () => transition(draft(initialModel(), "note").model, { kind: "Save", correlationId: "c1" }).model;

test("step rejects a mismatched protocol version", () => {
  assert.throws(() => step(initialModel(), initialize({ protocolVersion: 99 }), "c1"), /Unsupported protocol version/);
});

test("step refuses a kernel that does not offer the Storage effect", () => {
  assert.throws(() => step(initialModel(), initialize({ capabilities: ["Http"] }), "c1"), /does not offer the Storage effect/);
});

test("Initialize projects the initial view without requesting effects", () => {
  const { response } = step(initialModel(), initialize(), "c1");
  assert.deepEqual(response.effects, []);
  assert.deepEqual(response.cancellations, []);
  assert.equal(response.view.phaseKind, "Empty");
});

test("Initialize answers the host's handshake: Accepted at protocol 1.4, core contract, no capability packs", () => {
  const { response } = step(initialModel(), initialize(), "c1");
  assert.deepEqual(response.handshake, {
    kind: "Accepted",
    protocol: { major: 1, minor: 4 },
    contract: { ...CORE_CONTRACT_IDENTITY },
    capabilities: [],
  });
  assert.deepEqual(REQUIREMENTS.required, []);
  assert.deepEqual(REQUIREMENTS.optional, []);
});

test("Initialize without a handshake offer (a pre-1.1 kernel) is answered HandshakeMissing", () => {
  const { handshake, ...legacy } = initialize();
  assert.ok(handshake);
  const { response } = step(initialModel(), legacy, "c1");
  assert.deepEqual(response.handshake, { kind: "Rejected", reason: { kind: "HandshakeMissing" } });
});

test("Initialize from a host on a different core contract is answered ContractMismatch", () => {
  const foreign = { ...offer, contract: { ...offer.contract, fingerprint: "sha256:00" } };
  const { response } = step(initialModel(), initialize({ handshake: foreign }), "c1");
  assert.equal(response.handshake.kind, "Rejected");
  assert.equal(response.handshake.reason.kind, "ContractMismatch");
});

test("only Initialize carries a handshake", () => {
  const { response } = step(initialModel(), { kind: "Event", event: { kind: "Event", name: "draftChanged", value: "a" } }, "c1");
  assert.equal("handshake" in response, false);
});

test("LocationChanged re-projects the model unchanged and requests nothing", () => {
  const model = savingModel();
  const { model: after, response } = step(model, { kind: "LocationChanged", location: { ...location, hash: "#x" } }, "c2");
  assert.equal(after, model, "the model must be the same value");
  assert.deepEqual(response.effects, []);
  assert.deepEqual(response.view, project(model));
});

test("a CapabilityFact is a contract violation: no capability was negotiated", () => {
  assert.throws(
    () => step(initialModel(), { kind: "CapabilityFact", capability: "limen.focus", version: 1, fact: {} }, "c1"),
    /Unexpected CapabilityFact from limen\.focus/,
  );
});

test("a StorageResult is routed to the domain as RecordStorage", () => {
  const { model, response } = step(savingModel(), {
    kind: "EffectResult",
    result: { kind: "StorageResult", correlationId: "c1", outcome: { kind: "Success", value: null } },
  }, "c2");
  assert.equal(model.phase.kind, "Saved");
  assert.equal(response.view.statusText, 'Saved "note".');
});

for (const result of [
  { kind: "HttpResult", correlationId: "c1", outcome: { kind: "Cancelled" } },
  { kind: "ClipboardResult", correlationId: "c1", outcome: { kind: "Success" } },
  { kind: "NavigationResult", correlationId: "c1", outcome: { kind: "Dispatched" } },
  { kind: "CapabilityResult", correlationId: "c1", capability: "limen.focus", version: 1, outcome: { kind: "Unsupported", reason: "not-negotiated" } },
]) {
  test(`a ${result.kind} has no request behind it and is refused, not applied`, () => {
    assert.throws(
      () => step(savingModel(), { kind: "EffectResult", result }, "c2"),
      new RegExp(`Unexpected ${result.kind} \\(c1\\): this slice only requests Storage effects`),
    );
  });
}

test("an unknown message kind fails loudly rather than being ignored", () => {
  assert.throws(() => step(initialModel(), { kind: "Bogus" }, "c1"), /Unhandled variant/);
});
