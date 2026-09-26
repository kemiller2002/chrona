// Vendored Praxis provenance contract (RQ-ROS-2026-A018; CHR-PROV-009):
// the files under vendor/praxis-provenance/ are byte-identical to the pinned
// Praxis commit, every shared conformance case reaches the same verdict, and
// the end-to-end Echelon chain replays with every record keeping its own
// originator.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import {
  classify, appendContribution, addLineage, preservationViolations, originator, withRole, emptyBlock,
} from "../../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const vendorRoot = new URL("../../vendor/praxis-provenance/", import.meta.url);
const source = JSON.parse(readFileSync(new URL("SOURCE.json", vendorRoot), "utf8"));
const json = (path) => JSON.parse(readFileSync(new URL(path, vendorRoot), "utf8"));

test("SOURCE.json pins the Praxis contract commit", () => {
  assert.equal(source.repository, "kemiller2002/praxis");
  assert.equal(source.commit, "a42c44e8ae0e6e16fdd513141460b700e5fa6648");
  assert.deepEqual(Object.keys(source.files).sort(), Object.keys(source.origins).sort());
});

for (const [path, expected] of Object.entries(source.files)) {
  test(`vendored ${path} is unchanged (SHA-256)`, () => {
    const actual = createHash("sha256").update(readFileSync(new URL(path, vendorRoot))).digest("hex");
    assert.equal(actual, expected, `${path} differs from Praxis ${source.commit}; re-vendor instead of editing`);
  });
}

const cases = json("fixtures/cases.json").cases;
test("the shared conformance suite is present", () => assert.ok(cases.length >= 40));
for (const item of cases) {
  test(`conformance: ${item.name} is ${item.expect}`, () => {
    const result = classify(item.block);
    assert.equal(result.verdict, item.expect, JSON.stringify(result.problems));
    assert.equal(result.warnings.length, item.warnings);
  });
}

const chain = json("fixtures/echelon-chain.json");
const replay = () =>
  chain.steps.reduce((records, step) => {
    const current = records[step.record] ?? emptyBlock();
    if (step.lineage) return { ...records, [step.record]: addLineage(current, step.lineage) };
    const result = appendContribution(current, step.append.key, step.append.contribution);
    assert.ok(result.ok, `${step.record} ${step.append.key}: ${result.error}`);
    assert.deepEqual(preservationViolations(current, result.block), []);
    return { ...records, [step.record]: result.block };
  }, {});

test("echelon chain: every record keeps its own originator and roles", () => {
  const records = replay();
  for (const [record, expected] of Object.entries(chain.expect.originators)) {
    assert.equal(originator(records[record]).key, expected.key, record);
    assert.equal(originator(records[record]).actor.id, expected.actorId, record);
  }
  for (const [record, roles] of Object.entries(chain.expect.roles)) {
    for (const [role, keys] of Object.entries(roles)) {
      assert.deepEqual(withRole(records[record], role).map((item) => item.key), keys, `${record} ${role}`);
    }
  }
});

test("time-observation schema references only the vendored Praxis schemas", () => {
  const schema = JSON.parse(readFileSync(new URL("../../schemas/time-observation.schema.json", import.meta.url), "utf8"));
  const ids = new Set([json("schemas/provenance-actor.schema.json").$id, json("schemas/provenance-interchange.schema.json").$id]);
  const refs = [];
  const walk = (node) => {
    if (Array.isArray(node)) node.forEach(walk);
    else if (node && typeof node === "object") {
      if (typeof node.$ref === "string" && !node.$ref.startsWith("#")) refs.push(node.$ref);
      Object.values(node).forEach(walk);
    }
  };
  walk(schema);
  assert.ok(refs.length >= 2);
  for (const ref of refs) assert.ok(ids.has(ref), `${ref} is not a vendored Praxis schema`);
  const interchange = json("schemas/provenance-interchange.schema.json");
  const actorRef = new URL(interchange.$defs.contribution.properties.actor.$ref, interchange.$id).href;
  assert.ok(ids.has(actorRef));
});
