// The evidence builder's decisions (GH-112): lack of evidence is unknown,
// never pass; a cited test counts only when it ran and passed; the document
// validates against the pinned schema.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { aggregate, actionEvidence, buildEvidence, validate, SCHEMA_PATH } from "./evidence.mjs";

const schema = JSON.parse(readFileSync(SCHEMA_PATH, "utf8"));
const report = (fields) => ({ kind: "action", ruleId: "ULI-A02", reason: "", ...fields });

test("no observation is unknown; one failure fails; unknown outranks pass", () => {
  assert.equal(aggregate([]), "unknown");
  assert.equal(aggregate(["pass", "fail", "unknown"]), "fail");
  assert.equal(aggregate(["pass", "unknown"]), "unknown");
  assert.equal(aggregate(["pass", "pass"]), "pass");
});

test("a conformance action passes only on a PASS report from a passing test", () => {
  const declared = { id: "x", verify: "conformance" };
  const run = (passed, status) => [{ file: "f", title: "t", passed, reports: [report({ id: "x", status })] }];
  assert.equal(actionEvidence(declared, run(true, "PASS")).status, "pass");
  assert.equal(actionEvidence(declared, run(false, "PASS")).status, "unknown");
  assert.equal(actionEvidence(declared, run(true, "FAIL")).status, "fail");
  assert.equal(actionEvidence(declared, []).status, "unknown");
});

test("a cited test is evidence only when it ran in this run", () => {
  const declared = { id: "x", verify: { spec: "tests/browser/a.spec.js", title: "does it" } };
  assert.equal(actionEvidence(declared, []).status, "unknown");
  assert.equal(actionEvidence(declared, [{ file: "tests/browser/a.spec.js", title: "does it", passed: true, reports: [] }]).status, "pass");
  assert.equal(actionEvidence(declared, [{ file: "tests/browser/a.spec.js", title: "does it", passed: false, reports: [] }]).status, "fail");
  assert.equal(actionEvidence({ id: "x", verify: null }, []).status, "unknown");
});

test("an empty run still yields a valid document in which nothing passes", () => {
  const document = buildEvidence({ run: [], revision: "0123456789abcdef" });
  assert.deepEqual(validate(document, schema), []);
  assert.ok(document.surfaces.every((surface) => surface.status === "unknown"));
  assert.ok(document.surfaces.flatMap((surface) => surface.actions).every((item) => item.status === "unknown"));
});

test("every existing test the contract cites is still there, under that title", async () => {
  const { ALL_SURFACES } = await import("../../tests/conformance/chrona.contract.mjs");
  const cited = ALL_SURFACES.flatMap((surface) => surface.actions).filter((item) => item.verify && item.verify.spec);
  const missing = cited.filter((item) => !readFileSync(item.verify.spec, "utf8").includes(JSON.stringify(item.verify.title).slice(1, -1)));
  assert.deepEqual(missing.map((item) => `${item.id}: ${item.verify.spec} › ${item.verify.title}`), []);
});
