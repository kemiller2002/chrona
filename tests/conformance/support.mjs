// Chrona's glue for the UI conformance suite (GH-112): the app fixtures of
// the browser suite, the vendored Forma helpers, and a record of every
// report a test makes, attached to the test so tools/ui-conformance/
// evidence.mjs can build the evidence document from the run itself.
//
// Nothing here guesses which elements are peers or what an action does: the
// groups and actions are declared in chrona.contract.mjs.
import {
  STATUS,
  edgeCoordinate,
  formatReport,
  measureAlignment,
  measureOverflow,
  runActionAssertion,
  scaleDefaultText,
  settleFontsAndFrames,
  resolveTarget
} from "./forma-conformance/index.mjs";
import { actionById, groupById } from "./chrona.contract.mjs";
import { test as base, expect, setUp, yesterday, addEntry } from "../browser/support.js";

export { expect, setUp, yesterday, addEntry, STATUS };

export const REPORT_ATTACHMENT = "uli-report";

export const test = base;

// Attach one report. `subject` names the declared action, group or surface.
export const record = (subject, report) =>
  test.info().attach(REPORT_ATTACHMENT, {
    contentType: "application/json",
    body: JSON.stringify({ ...subject, ruleId: report.ruleId, status: report.status, reason: report.reason ?? "", detail: report.detail ?? null })
  });

export const setWidth = (page, width) => page.setViewportSize({ width, height: 900 });

// Waits until the engine has rendered the place the address names.
export async function open(page, hash, ready = ".chrona-shell") {
  await page.evaluate((target) => { window.location.hash = target; }, hash.replace(/^#/, ""));
  await expect(page.locator(ready)).toBeVisible();
  await settleFontsAndFrames(page);
}

const scoped = (page, scope) => (scope ? resolveTarget(page, scope) : page);

// ULI-A01/A02: activate a declared action and observe its declared outcome.
export async function expectOutcome(page, id, { name, observe, scope, timeoutMs = 10_000, ruleId = "ULI-A02" }) {
  const declared = actionById(id);
  if (!declared) throw new Error(`undeclared action ${id}`);
  const report = await runActionAssertion(scope ? scoped(page, scope) : page, {
    ruleId,
    action: { role: declared.control.role, name: name ?? declared.control.name },
    outcome: observe,
    timeoutMs
  });
  await record({ kind: "action", id }, { ...report, detail: { pre: report.pre?.value ?? null, post: report.post?.value ?? null } });
  expect(report.status, `${id}: ${formatReport(report)} (pre=${JSON.stringify(report.pre?.value)}, post=${JSON.stringify(report.post?.value)})`).toBe(STATUS.PASS);
  return report;
}

// A declared outcome observed by the test itself (a sequence the helper's
// single click cannot express, such as refuse-then-accept). The test makes
// the observations; this records them under the declared action.
export async function recordObserved(id, ruleId, observe) {
  if (!actionById(id)) throw new Error(`undeclared action ${id}`);
  try {
    await observe();
    await record({ kind: "action", id }, { ruleId, status: STATUS.PASS, reason: "observed by the test: pre-state, invocation and post-state asserted" });
  } catch (error) {
    await record({ kind: "action", id }, { ruleId, status: STATUS.FAIL, reason: String(error.message).split("\n")[0] });
    throw error;
  }
}

// ULI-L01/L02/L07/L10 with ULI-L09: a declared group either shares a row,
// and then its declared edge agrees within the tolerance, or the
// composition has stacked it, and then its members keep source order, each
// below the one before, without overlap.
export async function expectRowOrStack(page, groupId, width) {
  const group = groupById(groupId);
  if (!group) throw new Error(`undeclared group ${groupId}`);
  const root = scoped(page, group.scope);
  await settleFontsAndFrames(page);
  const boxes = await Promise.all(group.targets.map(async (target) => {
    const locator = resolveTarget(root, target);
    await expect(locator, `${groupId}: ${target.role} "${target.name}"`).toHaveCount(1);
    return { target, box: await locator.boundingBox() };
  }));
  const sameRow = boxes.every(({ box }) => box && edgeCoordinate(box, "top") < edgeCoordinate(boxes[0].box, "bottom") && edgeCoordinate(box, "bottom") > edgeCoordinate(boxes[0].box, "top"));
  const edge = { "top-edge": "top", "bottom-edge": "bottom", centerline: "center", "text-baseline": "top" }[group.relation];
  if (sameRow) {
    const report = await measureAlignment(root, { ruleId: "ULI-L01", id: `${groupId}@${width}`, edge, toleranceCssPx: group.toleranceCssPx ?? 1, peers: group.targets });
    await record({ kind: "group", id: groupId, width, layout: "row" }, { ...report, detail: { deltaCssPx: report.deltaCssPx ?? null } });
    expect(report.status, formatReport(report)).toBe(STATUS.PASS);
    return report;
  }
  // Stacked or wrapped: every member that is not on the first member's row
  // starts below the bottom of the one before it, unless they share a row.
  const ordered = boxes.every(({ box }, index) => index === 0 || (() => {
    const previous = boxes[index - 1].box;
    const sharesRow = box.y < previous.y + previous.height && box.y + box.height > previous.y;
    return sharesRow ? box.x >= previous.x + previous.width - 1 : box.y >= previous.y + previous.height - 1;
  })());
  const report = { ruleId: "ULI-L09", status: ordered ? STATUS.PASS : STATUS.FAIL, reason: ordered ? "wrapped or stacked in source order without overlap" : "members overlap or leave source order when wrapped" };
  await record({ kind: "group", id: groupId, width, layout: "stacked" }, report);
  expect(report.status, `${groupId}@${width}: ${report.reason}`).toBe(STATUS.PASS);
  return report;
}

// ULI-L05: no horizontal page scroll and no text escaping a clipping box.
export async function expectNoOverflowRecorded(page, surface, width, textScale = 1) {
  const report = await measureOverflow(page);
  await record({ kind: "surface", id: surface, check: "overflow", width, textScale }, { ...report, detail: { escapes: report.escapes } });
  expect(report.status, `${surface}@${width}x${textScale}: ${report.reason}\n${report.escapes.join("\n")}`).toBe(STATUS.PASS);
  return report;
}

export { scaleDefaultText, settleFontsAndFrames };
