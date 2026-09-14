import test from "node:test";
import assert from "node:assert/strict";
import { decodeLabel, eventToCommand, initialModel, transition } from "../dist/domain.js";
import { project } from "../dist/projection.js";
import { step } from "../dist/transport.js";

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

test("step rejects a mismatched protocol version", () => {
  assert.throws(
    () => step(initialModel(), { kind: "Initialize", protocolVersion: 99, capabilities: ["Http", "Storage"] }, "c1"),
    /Unsupported protocol version/,
  );
});

test("Initialize projects the initial view without requesting effects", () => {
  const { response } = step(initialModel(), { kind: "Initialize", protocolVersion: 1, capabilities: ["Http", "Storage"] }, "c1");
  assert.deepEqual(response.effects, []);
  assert.equal(response.view.phaseKind, "Empty");
});
