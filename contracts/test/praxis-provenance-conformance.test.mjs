// Runs the local Praxis provenance codec against the vendored Praxis
// conformance fixtures (RQ-ROS-2026-A013, RQ-ROS-2026-A015). The fixtures
// are a pinned copy: SOURCE.json records the Praxis commit and a SHA-256 per
// file, and the first test proves the copy has not drifted.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { chain, classify, originator, read, successorProblems } from "../praxis-provenance/praxis-provenance-record.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..", "fixtures", "praxis-provenance-record");
const load = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));
const manifest = load("manifest.json");
const source = load("SOURCE.json");

const walk = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [relative(root, path).split(sep).join("/")];
  });

test("vendored fixtures match SOURCE.json digests exactly", () => {
  assert.equal(source.repository, "kemiller2002/praxis");
  assert.equal(source.path, "schemas/conformance/provenance-record");
  const present = walk(root).filter((path) => path !== "SOURCE.json").sort();
  assert.deepEqual(present, Object.keys(source.files).sort(), "no fixture added or removed");
  const digests = Object.fromEntries(present.map((path) => [path, createHash("sha256").update(readFileSync(join(root, path))).digest("hex")]));
  assert.deepEqual(digests, source.files);
});

test("manifest is the supported contract", () => {
  assert.equal(manifest.contract, "praxis.provenance-record");
  assert.equal(manifest.version.split(".")[0], "1");
});

for (const item of manifest.cases) {
  test(`case ${item.file} is ${item.expect} (${item.why})`, () => {
    assert.equal(classify(load(item.file)), item.expect);
  });
}

for (const item of manifest.successors) {
  test(`successor ${item.before} -> ${item.after} is ${item.expect} (${item.why})`, () => {
    const problems = successorProblems(load(item.before), load(item.after));
    assert.equal(problems.length === 0 ? "preserved" : "destructive", item.expect, JSON.stringify(problems));
  });
}

test("e2e: every step is valid and every successor step preserves its predecessor", () => {
  for (const step of manifest.e2e.steps) {
    assert.equal(classify(load(step.file)), "valid", step.file);
    if (step.successorOf) assert.deepEqual(successorProblems(load(step.successorOf), load(step.file)), [], step.file);
  }
});

test("e2e: the final record's chain and originator are reconstructed without re-attribution", () => {
  const { expected } = manifest.e2e;
  const reading = read(load(expected.final));
  const origin = originator(reading.record);
  assert.deepEqual({ key: origin.key, actor: origin.actor.id }, expected.originatorOfFinal);
  const links = chain(reading.record).map((link) => ({
    subject: link.subject,
    key: link.contribution.key,
    actor: link.contribution.actor.id,
    operations: [...link.contribution.operations],
  }));
  assert.deepEqual(links, expected.chain);
});
