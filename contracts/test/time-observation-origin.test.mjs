// CHR-PROV-001..010: executable checks for the receiver-owned origin contract.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { CONTRACT_NAME, lineageProblems, originProblems, schema } from "../time-observation-origin.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "fixtures", "time-observation-origin", "v1");
const load = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));
const manifest = load("manifest.json");

test("schema and fixtures describe the same contract", () => {
  assert.equal(schema.properties.contract.const, CONTRACT_NAME);
  assert.equal(manifest.contract, CONTRACT_NAME);
  assert.ok(manifest.cases.some((item) => item.expect === "valid"));
  assert.ok(manifest.cases.some((item) => item.expect === "invalid"));
});

for (const item of manifest.cases) {
  test(`origin ${item.file} is ${item.expect} (${item.why})`, () => {
    const problems = originProblems(load(item.file));
    assert.equal(problems.length === 0 ? "valid" : "invalid", item.expect, JSON.stringify(problems));
  });
}

for (const item of load(manifest.lineage).cases) {
  test(`lineage ${item.operation} ${item.name} is ${item.expect} (${item.why})`, () => {
    const problems = lineageProblems(item.sources, item.result);
    assert.equal(problems.length === 0 ? "preserved" : "destructive", item.expect, JSON.stringify(problems));
  });
}

test("validation is pure: the origin, including unknown fields and provenance, is untouched", () => {
  const origin = load("valid/minor-version-unknown-fields.json");
  const before = JSON.stringify(origin);
  assert.deepEqual(originProblems(Object.freeze(origin)), []);
  assert.equal(JSON.stringify(origin), before);
  const carried = load("valid/unsupported-provenance-carried.json");
  assert.deepEqual(originProblems(carried), []);
  assert.deepEqual(carried.provenance, load("valid/unsupported-provenance-carried.json").provenance);
});

test("validation needs nothing from Praxis at run time (no network, no Praxis package)", () => {
  const pkg = JSON.parse(readFileSync(new URL("../../package.json", import.meta.url), "utf8"));
  const deps = { ...pkg.dependencies, ...pkg.devDependencies };
  assert.ok(!Object.keys(deps).some((name) => /praxis|ros-/i.test(name)));
});

test("CHR-PROV-003: Chrona's own run key is namespaced, never Praxis-shaped", async () => {
  const { foreignExecutionKey, isForeignExecution } = await import("../praxis-provenance/praxis-provenance-record.mjs");
  const key = foreignExecutionKey("chrona", "import-42");
  assert.deepEqual(key, { ok: true, value: "EXE-chrona.import-42" });
  assert.ok(isForeignExecution(key.value));
  assert.ok(!isForeignExecution("EXE-20260926T100000000Z-c3c3c3c3"));
  assert.equal(foreignExecutionKey("Chrona", "x").ok, false);
});
