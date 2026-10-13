// Builds Chrona's UI conformance evidence (GH-112) for
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 from a Playwright JSON
// report, and validates it against the pinned evidence schema
// (tests/fixtures/visual-engineering/ui-conformance-1.0.0).
//
//   node tools/ui-conformance/evidence.mjs RESULTS.json [OUT.json]
//
// Mechanical: every status comes from a report a test attached in this run,
// or from whether a cited existing test passed in this run. Nothing is
// hand-written, and anything without evidence stays "unknown".
//
// Pure functions build the document; the IO at the bottom reads and writes.
import { readFileSync, writeFileSync, mkdirSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import Ajv2020 from "ajv/dist/2020.js";
import { ALL_SURFACES, CONTRACT_WIDTHS } from "../../tests/conformance/chrona.contract.mjs";

const REPORT_ATTACHMENT = "uli-report";
export const SCHEMA_PATH = fileURLToPath(new URL("../../tests/fixtures/visual-engineering/ui-conformance-1.0.0/ui-conformance.schema.json", import.meta.url));

// ---- reading the run ----------------------------------------------------------

const flattenSuites = (suites, rootDir, cwd) =>
  suites.flatMap((suite) => [
    ...(suite.specs ?? []).flatMap((spec) =>
      spec.tests.map((test) => ({
        file: relative(cwd, resolve(rootDir, spec.file)),
        title: spec.title,
        project: test.projectName,
        passed: test.status === "expected",
        reports: test.results.flatMap((result) =>
          result.attachments
            .filter((attachment) => attachment.name === REPORT_ATTACHMENT && attachment.body)
            .map((attachment) => JSON.parse(Buffer.from(attachment.body, "base64").toString("utf8"))))
      }))),
    ...flattenSuites(suite.suites ?? [], rootDir, cwd)
  ]);

export const readRun = (results, cwd) => flattenSuites(results.suites ?? [], results.config?.rootDir ?? cwd, cwd);

// ---- deciding -------------------------------------------------------------------

const STATUS = { PASS: "pass", FAIL: "fail", UNKNOWN: "unknown" };
const fromHelper = (status) => ({ PASS: STATUS.PASS, FAIL: STATUS.FAIL }[status] ?? STATUS.UNKNOWN);

// One FAIL fails the unit; any UNKNOWN keeps it unknown; PASS needs at least
// one observation and nothing else.
export const aggregate = (statuses) =>
  statuses.length === 0 ? STATUS.UNKNOWN
    : statuses.includes(STATUS.FAIL) ? STATUS.FAIL
      : statuses.includes(STATUS.UNKNOWN) ? STATUS.UNKNOWN
        : STATUS.PASS;

const reportsOf = (run, predicate) =>
  run.flatMap((test) => test.reports.filter(predicate).map((report) => ({ test, report })));

const describeReport = ({ test, report }) =>
  `${test.file} › ${test.title}: ${report.ruleId} ${report.status}${report.width ? ` @${report.width}px` : ""}${report.textScale > 1 ? ` x${report.textScale} text` : ""}${report.layout ? ` (${report.layout})` : ""}${report.reason ? ` — ${report.reason}` : ""}`;

export const actionEvidence = (declared, run) => {
  if (declared.verify === "conformance") {
    const found = reportsOf(run, (report) => report.kind === "action" && report.id === declared.id);
    // A report only counts from a test that completed as expected.
    const statuses = found.map(({ test, report }) => (test.passed ? fromHelper(report.status) : report.status === "PASS" ? STATUS.UNKNOWN : fromHelper(report.status)));
    return { status: aggregate(statuses), evidence: found.map(describeReport) };
  }
  if (declared.verify && declared.verify.spec) {
    const test = run.find((candidate) => candidate.file === declared.verify.spec && candidate.title === declared.verify.title);
    if (!test) return { status: STATUS.UNKNOWN, evidence: [`${declared.verify.spec} › ${declared.verify.title}: not run`] };
    return { status: test.passed ? STATUS.PASS : STATUS.FAIL, evidence: [`${test.file} › ${test.title}: ${test.passed ? "passed" : "failed"} (asserts the outcome)`] };
  }
  return { status: STATUS.UNKNOWN, evidence: [] };
};

const actionOf = (declared, run) => {
  const { status, evidence } = actionEvidence(declared, run);
  return {
    id: declared.id,
    control: declared.control,
    intent: declared.intent,
    expectedOutcome: `${declared.expectedOutcome} [owner: ${declared.owner}; state: ${declared.state}]`,
    status,
    evidence,
    resetStrategy: declared.reset
  };
};

const groupOf = (group, run) => ({
  id: group.id,
  relation: group.relation,
  toleranceCssPx: group.toleranceCssPx ?? 1,
  targets: group.targets.map(({ role, name }) => ({ role, name })),
  rationale: group.rationale,
  evidence: reportsOf(run, (report) => report.kind === "group" && report.id === group.id).map(describeReport)
});

const groupStatuses = (group, run) =>
  reportsOf(run, (report) => report.kind === "group" && report.id === group.id).map(({ report }) => fromHelper(report.status));

export const surfaceOf = (surface, run) => {
  const actions = surface.actions.map((declared) => actionOf(declared, run));
  const own = reportsOf(run, (report) => (report.kind === "surface" || report.kind === "inventory") && (report.id === surface.id || (surface.id === "app-shell" && report.kind === "inventory")));
  const groups = surface.groups.map((group) => groupOf(group, run));
  const measured = own.length > 0;
  const statuses = [
    // An unmeasured layout is missing evidence: the surface cannot pass.
    ...(measured ? [] : [STATUS.UNKNOWN]),
    ...own.map(({ report }) => fromHelper(report.status)),
    ...surface.groups.flatMap((group) => groupStatuses(group, run)),
    ...actions.map((item) => item.status)
  ];
  const unknownActions = actions.filter((item) => item.status === STATUS.UNKNOWN).map((item) => `action ${item.id} (${item.control.role} "${item.control.name}"): no outcome assertion in this run`);
  return {
    id: surface.id,
    path: surface.path,
    kind: surface.kind,
    status: aggregate(statuses),
    viewportWidths: measured ? [...CONTRACT_WIDTHS] : [],
    alignmentGroups: groups,
    actions,
    evidence: own.map(describeReport),
    unknownScope: [
      ...(measured ? [] : ["layout not measured by the conformance run (overflow, text scale, groups)"]),
      ...unknownActions,
      "right-to-left and translated text: not exercised (Chrona ships English, left-to-right only)"
    ]
  };
};

export const buildEvidence = ({ run, revision, repository = "kemiller2002/chrona" }) => ({
  standardVersion: "1.0.0",
  repository,
  revision,
  classification: "app-ui",
  surfaces: ALL_SURFACES.map((surface) => surfaceOf(surface, run)),
  exceptions: [],
  notes: [
    "Contract: visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 (visual-engineering 8da8fbc); helpers: @echelon-foundry/forma-conformance 0.1.0, vendored from Forma PR #158 (tests/conformance/forma-conformance).",
    "Generated by tools/ui-conformance/evidence.mjs from one Playwright run of every project in playwright.config.js; statuses are only what that run observed.",
    "Print/PDF output (ULI-L12) of the report document is Folio's and is not measured here; tests/browser/app-reports.spec.js proves the print dialog opens for it.",
    "Upstream (Forma) defects are recorded in docs/ui-conformance/upstream-defects.md and stay FAIL here."
  ].join(" ")
});

export const validate = (document, schema) => {
  const ajv = new Ajv2020({ allErrors: true, strict: false });
  const check = ajv.compile(schema);
  return check(document) ? [] : check.errors.map((error) => `${error.instancePath || "/"} ${error.message}`);
};

export const summarize = (document) => {
  const count = (items) => items.reduce((totals, { status }) => ({ ...totals, [status]: (totals[status] ?? 0) + 1 }), {});
  return {
    surfaces: count(document.surfaces),
    actions: count(document.surfaces.flatMap((surface) => surface.actions))
  };
};

// ---- IO -------------------------------------------------------------------------

const revisionOf = (cwd) => {
  if (process.env.GITHUB_SHA) return process.env.GITHUB_SHA;
  try {
    return execFileSync("git", ["rev-parse", "HEAD"], { cwd, encoding: "utf8" }).trim();
  } catch {
    return "unknown-revision";
  }
};

const main = ([resultsPath, outPath = "test-results/ui-conformance/chrona.ui-conformance.json"]) => {
  if (!resultsPath) throw new Error("usage: node tools/ui-conformance/evidence.mjs RESULTS.json [OUT.json]");
  const cwd = process.cwd();
  const results = JSON.parse(readFileSync(resultsPath, "utf8"));
  const document = buildEvidence({ run: readRun(results, cwd), revision: revisionOf(cwd) });
  const problems = validate(document, JSON.parse(readFileSync(SCHEMA_PATH, "utf8")));
  mkdirSync(dirname(resolve(outPath)), { recursive: true });
  writeFileSync(outPath, JSON.stringify(document, null, 2) + "\n");
  console.log(`UI conformance evidence: ${outPath}`);
  console.log(JSON.stringify(summarize(document)));
  if (problems.length > 0) {
    console.error(`The evidence does not validate against ${relative(cwd, SCHEMA_PATH)}:\n${problems.join("\n")}`);
    process.exitCode = 1;
  }
};

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main(process.argv.slice(2));
