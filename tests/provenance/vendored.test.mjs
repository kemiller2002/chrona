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
  classify, classifyText, appendContribution, addLineage, preservationViolations, originator, withRole, emptyBlock,
  keyFromEnvelopeV1, IDENTITY_ENVIRONMENT_VARIABLES,
} from "../../vendor/praxis-provenance/lib/provenance-interchange.mjs";

const vendorRoot = new URL("../../vendor/praxis-provenance/", import.meta.url);
const source = JSON.parse(readFileSync(new URL("SOURCE.json", vendorRoot), "utf8"));
const json = (path) => JSON.parse(readFileSync(new URL(path, vendorRoot), "utf8"));

test("SOURCE.json pins the Praxis contract commit", () => {
  assert.equal(source.repository, "kemiller2002/praxis");
  assert.equal(source.commit, "b0037183389c8b9392919f58521b9487d1b4d5c6");
  assert.deepEqual(Object.keys(source.files).sort(), Object.keys(source.origins).sort());
});

for (const [path, expected] of Object.entries(source.files)) {
  test(`vendored ${path} is unchanged (SHA-256)`, () => {
    const actual = createHash("sha256").update(readFileSync(new URL(path, vendorRoot))).digest("hex");
    assert.equal(actual, expected, `${path} differs from Praxis ${source.commit}; re-vendor instead of editing`);
  });
}

const cases = json("fixtures/cases.json").cases;
test("the shared conformance suite is present (revision 1.2: 70 cases)", () => {
  assert.equal(json("fixtures/cases.json").contractRevision, "1.2");
  assert.equal(cases.length, 70);
});
for (const item of cases) {
  test(`conformance: ${item.name} is ${item.expect}`, () => {
    const result = classify(item.block);
    assert.equal(result.verdict, item.expect, JSON.stringify(result.problems));
    assert.equal(result.warnings.length, item.warnings);
  });
}

// Contract 1.2 rule 1: text is classified as text (duplicate member names, unpaired surrogates).
const textCases = json("fixtures/text-cases.json");
test("the text conformance suite is present (revision 1.2)", () => {
  assert.equal(textCases.contractRevision, "1.2");
  assert.ok(textCases.cases.length >= 14);
});
for (const item of textCases.cases) {
  test(`text conformance: ${item.name} is ${item.expect}`, () => {
    assert.equal(classifyText(item.text).verdict, item.expect);
  });
}

// Contract 1.2 rule 4: injective per-code-point key segments.
const envelopeKeyCases = json("fixtures/envelope-key-cases.json").cases
  .map((item) => ({ ...item, envelope: item.envelopeText !== undefined ? JSON.parse(item.envelopeText) : item.envelope }));
for (const item of envelopeKeyCases) {
  test(`envelope key: ${item.name}`, () => {
    if (item.error) assert.throws(() => keyFromEnvelopeV1(item.envelope));
    else assert.equal(keyFromEnvelopeV1(item.envelope), item.key);
  });
}

// Contract 1.2 rule 3: lineage is checked like contributions.
for (const item of json("fixtures/lineage-cases.json").cases) {
  test(`lineage: ${item.name} is ${item.ok ? "added" : "refused"}`, () => {
    const result = addLineage(item.block, item.references);
    assert.equal(result.ok, item.ok, result.error);
    if (item.ok) assert.deepEqual(result.block.derivedFrom, item.derivedFrom);
  });
}

const chain = json("fixtures/echelon-chain.json");
const replay = () =>
  chain.steps.reduce((records, step) => {
    const current = records[step.record] ?? emptyBlock();
    if (step.lineage) {
      // Contract 1.2: addLineage returns { ok, block } and refuses what it cannot store.
      const added = addLineage(current, step.lineage);
      assert.ok(added.ok, `${step.record} lineage: ${added.error}`);
      return { ...records, [step.record]: added.block };
    }
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

test("the identity-environment list matches the vendored fixture", () => {
  const fixture = json("fixtures/identity-environment.json");
  assert.equal(fixture.contractRevision, "1.1");
  assert.deepEqual([...IDENTITY_ENVIRONMENT_VARIABLES].sort(), [...fixture.variables].sort());
});
