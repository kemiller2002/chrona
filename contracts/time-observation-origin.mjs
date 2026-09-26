// Contract-verification harness for `chrona.time-observation-origin` v1
// (docs/requirements/CHRONA-AGENT-PROVENANCE-REQUIREMENTS.md, CHR-PROV-001..010).
// Not product code: Chrona's domain and integration logic is F# (§0.4); the
// future Chrona.Integration assembly must pass the same fixtures.
//
// Pure functions: inputs are never mutated; results are plain data.
import { readFileSync } from "node:fs";
import Ajv2020 from "ajv/dist/2020.js";
import { jsonEqual, looksLikeCredential, validate as validateRecord, validateActor } from "./praxis-provenance/praxis-provenance-record.mjs";

export const CONTRACT_NAME = "chrona.time-observation-origin";

export const schema = Object.freeze(JSON.parse(readFileSync(new URL("./time-observation-origin.v1.schema.json", import.meta.url), "utf8")));

const ajv = new Ajv2020({ allErrors: true, strict: true, strictRequired: false });
const schemaValid = ajv.compile(schema);
const lineageValid = ajv.getSchema(`${schema.$id}#/$defs/originLineage`);

const problem = (field, message) => Object.freeze({ field, message });
const schemaProblems = (errors) => (errors ?? []).map((error) => problem(error.instancePath || "/", error.message ?? "schema violation"));

const provenanceProblems = (origin) => {
  if (origin.provenance === undefined) return [];
  const reading = validateRecord(origin.provenance);
  if (reading.status === "invalid") return reading.problems.map((item) => problem(`provenance.${item.field}`, item.message));
  if (reading.status === "unsupported" || origin.originExecution === undefined) return []; // carried verbatim; never interpreted
  const contribution = reading.record.contributions.find((item) => item.key === origin.originExecution);
  if (!contribution) return [problem("originExecution", `'${origin.originExecution}' is not a contribution of the carried provenance record`)];
  const actor = contribution.actor;
  const agrees = ["kind", "id", "provider", "model", "runtime"].every((name) => actor[name] === (typeof origin.originActor[name] === "string" ? origin.originActor[name] : undefined));
  return agrees ? [] : [problem("originActor", `the carried provenance attributes ${origin.originExecution} to ${actor.kind}:${actor.id}`)];
};

/**
 * Every reason `origin` is not an acceptable v1 origin (CHR-PROV-002/004/010).
 * Empty means acceptable. Acceptable is not authorized (CHR-PROV-005).
 */
export const originProblems = (origin) => {
  if (!schemaValid(origin)) return schemaProblems(schemaValid.errors);
  return [
    ...validateActor(origin.originActor).map((item) => problem(`originActor.${item.field}`, item.message)),
    ...["sourceSystem", "originExecution"].filter((name) => looksLikeCredential(origin[name])).map((name) => problem(name, "value looks like a credential")),
    ...provenanceProblems(origin),
  ];
};

const lineageShapeProblems = (label, lineage) =>
  lineageValid(lineage)
    ? lineage.flatMap((entry, index) => originProblems(entry.origin).map((item) => problem(`${label}[${index}].origin.${item.field}`, item.message)))
    : schemaProblems(lineageValid.errors).map((item) => problem(`${label}${item.field}`, item.message));

/**
 * CHR-PROV-006: the lineage produced by accept/split/merge/amend must be
 * exactly the union of its sources' lineage entries, each verbatim and once.
 * Anything dropped, rewritten, collapsed, duplicated, or invented is reported.
 */
export const lineageProblems = (sourceLineages, resultLineage) => {
  const shape = [
    ...sourceLineages.flatMap((lineage, index) => lineageShapeProblems(`sources[${index}]`, lineage)),
    ...lineageShapeProblems("result", resultLineage),
  ];
  if (shape.length > 0) return shape;
  const expected = sourceLineages.flat().reduce((unique, entry) => (unique.some((item) => jsonEqual(item, entry)) ? unique : [...unique, entry]), []);
  return [
    ...expected.filter((entry) => !resultLineage.some((item) => jsonEqual(item, entry))).map((entry) => problem("result", `origin lineage for '${entry.from}' was dropped or rewritten`)),
    ...resultLineage.filter((entry) => !expected.some((item) => jsonEqual(item, entry))).map((entry) => problem("result", `origin lineage for '${entry.from}' was not in any source (invented or rewritten)`)),
    ...resultLineage.filter((entry, index) => resultLineage.findIndex((item) => jsonEqual(item, entry)) !== index).map((entry) => problem("result", `origin lineage for '${entry.from}' is duplicated`)),
  ];
};
