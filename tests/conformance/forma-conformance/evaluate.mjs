// Vendored verbatim from @echelon-foundry/forma-conformance 0.1.0 (Forma PR #158, not yet
// published): kemiller2002/forma packages/conformance/src/evaluate.mjs at commit
// 6c71725a927187cacd6c6fc318bf266a21bbbdf8 (branch feat/ui-conformance-pilot-2026-10; src last
// changed in 7569ae5fe440dee7994dea08827b33935a85dbb8).
// Do not edit. Replace this directory with the published package when it ships;
// the specs import only its public API.

// Pure evaluation for visual-engineering/UI-LAYOUT-INTERACTION@1.0.0
// (rule IDs ULI-L01..L13, ULI-A01..A10). No browser, no I/O, no mutation:
// every function maps observations to a report. The browser adapters in
// index.mjs only gather observations. This module cites the contract; it does
// not restate or fork its rules.

export const CONTRACT = Object.freeze({ id: "visual-engineering/UI-LAYOUT-INTERACTION", version: "1.0.0" });

export const STATUS = Object.freeze({
  PASS: "PASS",
  FAIL: "FAIL",
  UNKNOWN: "UNKNOWN"
});

// Widths every surface is tested at (ULI-L09).
export const CONTRACT_WIDTHS = Object.freeze([320, 390, 768, 1280]);

export const DEFAULT_TOLERANCE_CSS_PX = 1;
export const DEFAULT_TIMEOUT_MS = 5000;

const EDGES = Object.freeze({
  top: box => box.y,
  bottom: box => box.y + box.height,
  center: box => box.y + box.height / 2,
  left: box => box.x,
  right: box => box.x + box.width
});

// Roles Playwright cannot address with getByRole. `disclosure` targets a
// native <summary> by its text, because <summary> has no implicit ARIA role.
export const PSEUDO_ROLES = Object.freeze(["disclosure"]);

export const describeTarget = target => {
  if (target === "controlled") return "the element the action controls";
  const { role, name } = target;
  return `${role} "${name instanceof RegExp ? name.source : name}"`;
};

export const requireSemanticTarget = (target, label) => {
  if (!target?.role || target.name === undefined || target.name === "") {
    throw new Error(`${label} must specify both accessible role and name.`);
  }
  return target;
};

export const edgeCoordinate = (box, edge) => {
  const read = EDGES[edge];
  if (!read) throw new Error(`Unsupported alignment edge "${edge}". Use one of: ${Object.keys(EDGES).join(", ")}.`);
  return read(box);
};

// ---------------------------------------------------------------------------
// ULI-L01/L02/L10: declared alignment groups
// ---------------------------------------------------------------------------

// Classify one raw observation of a declared peer. Only a single, rendered,
// non-zero box can contribute a coordinate; everything else is UNKNOWN.
export const classifyPeerObservation = ({ peer, count, box }) => {
  const element = describeTarget(peer);
  if (count === 0) return { element, status: STATUS.UNKNOWN, reason: "element not found in the accessibility tree (missing or hidden)" };
  if (count > 1) return { element, status: STATUS.UNKNOWN, reason: `accessible name is ambiguous (${count} matches)` };
  if (!box) return { element, status: STATUS.UNKNOWN, reason: "no bounding box (not rendered)" };
  if (!(box.width > 0) || !(box.height > 0)) {
    return { element, status: STATUS.UNKNOWN, reason: `zero-size box (${box.width}x${box.height})`, box };
  }
  return { element, status: STATUS.PASS, box };
};

export const validateAlignmentGroup = group => {
  const { ruleId = "ULI-L01", id, peers, edge = "top", toleranceCssPx = DEFAULT_TOLERANCE_CSS_PX, scope } = group ?? {};
  if (!id) throw new Error("A declared alignment group requires an id.");
  if (!Array.isArray(peers) || peers.length < 2) throw new Error(`Alignment group ${id} requires at least two declared peers.`);
  if (!/^ULI-L\d{2}$/.test(ruleId)) throw new Error(`Alignment group ${id} must cite a ULI-L rule ID.`);
  if (!(toleranceCssPx >= 0)) throw new Error(`Alignment group ${id} tolerance must be a non-negative number.`);
  peers.forEach((peer, index) => requireSemanticTarget(peer, `Peer ${index + 1} of ${id}`));
  edgeCoordinate({ x: 0, y: 0, width: 0, height: 0 }, edge);
  return Object.freeze({ ruleId, id, peers, edge, toleranceCssPx, scope });
};

// Decide a declared group's status from observations. Missing evidence is
// UNKNOWN, never PASS; a measured delta above tolerance is FAIL.
export const evaluateAlignment = (group, observations) => {
  const { ruleId, id, edge, toleranceCssPx } = validateAlignmentGroup(group);
  const measurements = observations.map(classifyPeerObservation)
    .map(item => (item.status === STATUS.PASS ? { ...item, coordinate: edgeCoordinate(item.box, edge) } : item));
  const base = { ruleId, group: id, edge, toleranceCssPx, measurements };
  const unknown = measurements.filter(item => item.status !== STATUS.PASS);
  if (unknown.length > 0) {
    return { ...base, status: STATUS.UNKNOWN, deltaCssPx: null, reason: unknown.map(item => `${item.element}: ${item.reason}`).join("; ") };
  }
  const coordinates = measurements.map(item => item.coordinate);
  const deltaCssPx = Math.max(...coordinates) - Math.min(...coordinates);
  const status = deltaCssPx <= toleranceCssPx ? STATUS.PASS : STATUS.FAIL;
  return {
    ...base,
    status,
    deltaCssPx,
    reason: `${edge}-edge delta ${deltaCssPx.toFixed(2)} CSS px ${status === STATUS.PASS ? "<=" : ">"} tolerance ${toleranceCssPx} CSS px`
  };
};

export const sameObservations = (a, b) =>
  a.length === b.length && a.every((item, index) => {
    const other = b[index];
    if (item.count !== other.count) return false;
    if (!item.box || !other.box) return item.box === other.box;
    return ["x", "y", "width", "height"].every(key => Math.abs(item.box[key] - other.box[key]) < 0.01);
  });

export const formatReport = report => {
  const details = (report.measurements ?? [])
    .map(item => `  - ${item.element}: ${item.status}${item.coordinate !== undefined ? ` ${report.edge}=${item.coordinate.toFixed(2)}px` : ""}${item.reason ? ` (${item.reason})` : ""}`)
    .join("\n");
  const subject = report.group ? `group ${report.group}` : report.action ?? report.subject ?? "";
  return `${report.ruleId} ${report.status}: ${subject}: ${report.reason}${details ? `\n${details}` : ""}`;
};

// ---------------------------------------------------------------------------
// ULI-A01/A02/A05: action outcomes
// ---------------------------------------------------------------------------

export const normalizeText = value => (value ?? "").replace(/\s+/g, " ").trim();

export const matchesExpected = (observed, expected) => {
  if (observed === null || observed === undefined) return false;
  if (expected instanceof RegExp) return new RegExp(expected.source, expected.flags.replace("g", "")).test(String(observed));
  return observed === expected;
};

// `open` observes native open state (details[open], dialog[open],
// :popover-open) of a semantic target or of "controlled": the element the
// action names through popovertarget, commandfor, aria-controls, or the
// <details> a <summary> belongs to. Controlled targets are declared in markup,
// never guessed.
export const OUTCOME_KINDS = Object.freeze({
  text: { targets: "semantic", expectedType: "string-or-regexp" },
  visible: { targets: "semantic", expectedType: "boolean" },
  checked: { targets: "semantic", expectedType: "boolean" },
  attribute: { targets: "semantic", expectedType: "string-or-regexp" },
  open: { targets: "semantic-or-controlled", expectedType: "boolean" },
  url: { targets: "none", expectedType: "string-or-regexp" }
});

export const validateActionContract = contract => {
  const { ruleId = "ULI-A02", action, outcome, timeoutMs = DEFAULT_TIMEOUT_MS, scope } = contract ?? {};
  requireSemanticTarget(action, "Action control");
  if (!/^ULI-A\d{2}$/.test(ruleId)) throw new Error("An action assertion must cite a ULI-A rule ID.");
  const kind = OUTCOME_KINDS[outcome?.kind];
  if (!kind) throw new Error(`An action requires an observable outcome kind: ${Object.keys(OUTCOME_KINDS).join(", ")}.`);
  if (kind.targets === "semantic") requireSemanticTarget(outcome.target, `Outcome target for ${outcome.kind}`);
  if (kind.targets === "semantic-or-controlled" && outcome.target !== "controlled") requireSemanticTarget(outcome.target, `Outcome target for ${outcome.kind}`);
  if (outcome.kind === "attribute" && !outcome.attribute) throw new Error("Attribute outcome requires an attribute name.");
  const typeOk = kind.expectedType === "boolean"
    ? typeof outcome.expected === "boolean"
    : typeof outcome.expected === "string" || outcome.expected instanceof RegExp;
  if (!typeOk) throw new Error(`Outcome ${outcome.kind} requires an explicit expected ${kind.expectedType} post-state.`);
  if (!(timeoutMs > 0 && Number.isFinite(timeoutMs))) throw new Error("Action assertions require a bounded positive timeoutMs.");
  return Object.freeze({ ruleId, action, outcome, timeoutMs, scope });
};

const actionBase = ({ ruleId, action, outcome, pre, post }) =>
  ({ ruleId, action: describeTarget(action), outcome: outcome.kind, expected: String(outcome.expected), pre, post });

// Decide whether an action can demonstrate a transition at all. Returns a
// report when it cannot (UNKNOWN for missing evidence, FAIL when the expected
// post-state is already present before activation), otherwise null.
export const evaluateActionPremise = ({ ruleId, action, outcome, pre, controlIssue }) => {
  const base = actionBase({ ruleId, action, outcome, pre, post: null });
  if (controlIssue) return { ...base, status: STATUS.UNKNOWN, reason: controlIssue };
  if (pre.missing && outcome.kind !== "visible") return { ...base, status: STATUS.UNKNOWN, reason: "outcome target not found before activation" };
  if (pre.count > 1) return { ...base, status: STATUS.UNKNOWN, reason: `outcome target name is ambiguous (${pre.count} matches)` };
  if (matchesExpected(pre.value, outcome.expected)) {
    return { ...base, status: STATUS.FAIL, reason: "pre-state already satisfies the expected post-state; no transition can be demonstrated" };
  }
  return null;
};

// Decide an action's status from pre/post observations. An action passes only
// when the pre-state did NOT already satisfy the expectation, the expected
// post-state was observed, and the observable value actually changed.
export const evaluateActionOutcome = ({ ruleId, action, outcome, pre, post, controlIssue }) => {
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;
  const base = actionBase({ ruleId, action, outcome, pre, post });
  if (!post) return { ...base, status: STATUS.FAIL, reason: "no post-state observed" };
  if (!matchesExpected(post.value, outcome.expected)) {
    return { ...base, status: STATUS.FAIL, reason: "expected post-state never appeared within the timeout" };
  }
  if (post.value === pre.value) return { ...base, status: STATUS.FAIL, reason: "post-state equals pre-state" };
  return { ...base, status: STATUS.PASS, reason: "expected post-state observed after activation" };
};

// ---------------------------------------------------------------------------
// ULI-L05/L06: overflow and label integrity
// ---------------------------------------------------------------------------

// Horizontal overflow from an observation. Text escaping the viewport, or a
// container that clips (overflow hidden/clip), FAILS even when clipping hides
// it. Content inside a scroll container (overflow auto/scroll) is reachable
// and is the contract's permitted contained scroller, so callers must not
// report it as an escape.
export const evaluateOverflow = ({ scrollWidth, clientWidth, escapes = [] }) => {
  if (!(clientWidth > 0)) return { ruleId: "ULI-L05", status: STATUS.UNKNOWN, reason: "no viewport width observed", escapes: [] };
  const pageOverflow = scrollWidth - clientWidth;
  const failed = pageOverflow > 0 || escapes.length > 0;
  return {
    ruleId: "ULI-L05",
    status: failed ? STATUS.FAIL : STATUS.PASS,
    escapes,
    reason: `document scrollWidth ${scrollWidth} vs clientWidth ${clientWidth}; ${escapes.length} text run(s) escaping the viewport or a clipping container`
  };
};

// A label is intact when its content fits its own box and the box lies in the
// viewport. An inline box has no client size to compare, so it is UNKNOWN
// rather than a vacuous PASS.
export const evaluateLabelIntegrity = ({ name, display, scrollWidth, clientWidth, scrollHeight, clientHeight, left, right, viewport }) => {
  const base = { ruleId: "ULI-L06", name };
  if (display === "inline" || !(clientWidth > 0) || !(clientHeight > 0)) {
    return { ...base, status: STATUS.UNKNOWN, reason: "label has no measurable box (inline or zero-size); measure its block container" };
  }
  const clipped = scrollWidth > clientWidth + 1 || scrollHeight > clientHeight + 1;
  const offscreen = left < -0.5 || right > viewport + 0.5;
  return {
    ...base,
    status: clipped || offscreen ? STATUS.FAIL : STATUS.PASS,
    reason: `${clipped ? "content clipped" : "content fits"}; ${offscreen ? "box offscreen" : "box on screen"}`
  };
};
