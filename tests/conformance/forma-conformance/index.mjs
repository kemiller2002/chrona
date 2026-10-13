// Vendored verbatim from @echelon-foundry/forma-conformance 0.1.0 (Forma PR #158, not yet
// published): kemiller2002/forma packages/conformance/src/index.mjs at commit
// 6c71725a927187cacd6c6fc318bf266a21bbbdf8 (branch feat/ui-conformance-pilot-2026-10; src last
// changed in 7569ae5fe440dee7994dea08827b33935a85dbb8).
// Do not edit. Replace this directory with the published package when it ships;
// the specs import only its public API.

import { expect } from "@playwright/test";
import {
  CONTRACT_WIDTHS,
  DEFAULT_TIMEOUT_MS,
  STATUS,
  evaluateActionOutcome,
  evaluateActionPremise,
  evaluateAlignment,
  evaluateLabelIntegrity,
  evaluateOverflow,
  formatReport,
  matchesExpected,
  normalizeText,
  sameObservations,
  validateActionContract,
  validateAlignmentGroup
} from "./evaluate.mjs";

// @echelon-foundry/forma-conformance: reference Playwright helpers for
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 (ULI-L01..L13, ULI-A01..A10).
// Dev/test only: nothing here ships in the zero-runtime design-system package.
// Tests declare meaningful peers and expected outcomes; the helpers never
// guess UI intent and never report PASS without an observation.
//
// Structure: pure evaluation (evaluate.mjs) decides the status; the adapters
// below only observe. Missing evidence is UNKNOWN, never PASS.

export * from "./evaluate.mjs";

// ---------------------------------------------------------------------------
// Locator layer
// ---------------------------------------------------------------------------

const escapeRegExp = value => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

const pageOf = root => (typeof root.page === "function" ? root.page() : root);

// Resolve a declared target. `scope` (a Locator) narrows the search when the
// same accessible name legitimately appears in several regions. The
// `disclosure` pseudo-role addresses a native <summary>, which has no implicit
// ARIA role and so cannot be reached with getByRole.
export const resolveTarget = (root, { role, name, scope }) => {
  const base = scope ?? root;
  if (role === "text") return base.getByText(name, { exact: typeof name === "string" });
  if (role === "disclosure") {
    const pattern = name instanceof RegExp ? name : new RegExp(`^\\s*${escapeRegExp(name)}\\s*$`);
    return base.locator("summary").filter({ hasText: pattern });
  }
  return base.getByRole(role, { name, exact: typeof name === "string" });
};

const withScope = (target, scope) => (target && typeof target === "object" && scope && !target.scope ? { ...target, scope } : target);

// Playwright role queries do not honour `inert`, so actionability checks it.
const isInert = locator => locator.evaluate(element => Boolean(element.closest("[inert]")));

// Wait until web fonts have loaded and two animation frames have elapsed.
export const settleFontsAndFrames = root =>
  pageOf(root).evaluate(() => document.fonts.ready.then(() =>
    new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))));

// ---------------------------------------------------------------------------
// ULI-L01/L02/L10: declared alignment groups
// ---------------------------------------------------------------------------

const observePeer = async (root, peer, timeoutMs) => {
  const locator = resolveTarget(root, peer);
  const count = await locator.count();
  const box = count === 1 ? await locator.boundingBox({ timeout: timeoutMs }) : null;
  return { peer, count, box };
};

const observePeers = (root, peers, timeoutMs) => Promise.all(peers.map(peer => observePeer(root, peer, timeoutMs)));

// Declared groups in markup (the attribute vocabulary Forma Studio exports):
//   data-uli-align-group="id"  data-uli-align-edge="top|bottom|center|left|right"
//   data-uli-align-peer="name" (optional; defaults to the element's text)
// The declaring elements themselves are the peers; nothing is inferred.
export const readDeclaredGroups = root => root.locator("[data-uli-align-group]").evaluateAll(elements =>
  Object.values(elements.reduce((groups, element, index) => {
    const id = element.getAttribute("data-uli-align-group");
    const group = groups[id] ?? { id, edge: null, peers: [] };
    const rect = element.getBoundingClientRect();
    return { ...groups, [id]: {
      ...group,
      edge: group.edge ?? element.getAttribute("data-uli-align-edge"),
      peers: [...group.peers, {
        name: element.getAttribute("data-uli-align-peer") || element.textContent.replace(/\s+/g, " ").trim().slice(0, 60) || `${element.tagName.toLowerCase()}#${index}`,
        box: element.checkVisibility?.() === false ? null : { x: rect.x, y: rect.y, width: rect.width, height: rect.height }
      }]
    } };
  }, {})));

const readStableDeclaredGroups = async (root, deadline, previous) => {
  await settleFontsAndFrames(root);
  const current = await readDeclaredGroups(root);
  if (previous && JSON.stringify(previous) === JSON.stringify(current)) return current;
  if (Date.now() >= deadline) return null;
  return readStableDeclaredGroups(root, deadline, current);
};

export const measureDeclaredGroups = async (root, { ruleId = "ULI-L01", toleranceCssPx, timeoutMs = DEFAULT_TIMEOUT_MS } = {}) => {
  const groups = await readStableDeclaredGroups(root, Date.now() + timeoutMs, null);
  if (!groups) return [{ ruleId, group: "*", status: STATUS.UNKNOWN, reason: "layout did not stabilize within the timeout", measurements: [] }];
  return groups.map(group => (group.peers.length < 2
    ? { ruleId, group: group.id, status: STATUS.UNKNOWN, reason: "a declared group needs at least two peers", measurements: [] }
    : evaluateAlignment(
      { ruleId, id: group.id, edge: group.edge ?? "top", toleranceCssPx, peers: group.peers.map(peer => ({ role: "declared", name: peer.name })) },
      group.peers.map(peer => ({ peer: { role: "declared", name: peer.name }, count: 1, box: peer.box })))));
};

export const expectDeclaredGroups = async (root, options) => {
  const reports = await measureDeclaredGroups(root, options);
  expect(reports.length, "no data-uli-align-group declarations found").toBeGreaterThan(0);
  const failing = reports.filter(report => report.status !== STATUS.PASS);
  expect(failing.map(formatReport).join("\n")).toBe("");
  return reports;
};

// Re-read until two consecutive frame-separated observations agree, bounded
// by a deadline. Each retry waits for real animation frames, not a fixed sleep.
const readStableObservations = async (root, peers, timeoutMs, deadline, previous) => {
  await settleFontsAndFrames(root);
  const current = await observePeers(root, peers, timeoutMs);
  if (previous && sameObservations(previous, current)) return current;
  if (Date.now() >= deadline) return null;
  return readStableObservations(root, peers, timeoutMs, deadline, current);
};

// Measure a declared group once fonts are ready and layout is stable. Returns
// a report; never throws on a geometry failure. A group whose layout never
// stabilizes is UNKNOWN. `root` is a Page or a Locator.
export const measureAlignment = async (root, group, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) => {
  const declared = validateAlignmentGroup(group);
  const peers = declared.peers.map(peer => withScope(peer, declared.scope));
  const stable = await readStableObservations(root, peers, timeoutMs, Date.now() + timeoutMs, null);
  if (!stable) {
    return { ruleId: declared.ruleId, group: declared.id, edge: declared.edge, toleranceCssPx: declared.toleranceCssPx,
      measurements: [], status: STATUS.UNKNOWN, deltaCssPx: null, reason: "layout did not stabilize within the timeout" };
  }
  return evaluateAlignment(declared, stable);
};

export const expectPeerAlignment = async (root, group, options) => {
  const report = await measureAlignment(root, group, options);
  expect(report.status, formatReport(report)).toBe(STATUS.PASS);
  return report;
};

// ---------------------------------------------------------------------------
// ULI-A01/A02/A06: action outcomes
// ---------------------------------------------------------------------------

// The element an action controls (takes the pinned ElementHandle), named in markup: popovertarget, commandfor,
// aria-controls, the <details> of a <summary>, or the <dialog> a
// method="dialog" form submits to.
const controlledElement = control => control.evaluateHandle(element => {
  const byId = id => (id ? element.ownerDocument.getElementById(id) : null);
  if (element.tagName === "SUMMARY") return element.parentElement?.tagName === "DETAILS" ? element.parentElement : null;
  return byId(element.getAttribute("popovertarget")) ?? byId(element.getAttribute("commandfor")) ??
    byId(element.getAttribute("aria-controls")) ??
    (element.closest("form[method=dialog]") ? element.closest("dialog") : null);
});

const readOpen = handle => handle.evaluate(element => {
  if (!element) return null;
  if (element.hasAttribute("popover")) return element.matches(":popover-open");
  if ("open" in element) return Boolean(element.open);
  return element.getAttribute("aria-expanded") === "true";
});

const observeOutcome = async (root, control, outcome, timeoutMs) => {
  if (outcome.kind === "url") return { value: pageOf(root).url(), missing: false };
  if (outcome.target === "controlled") {
    const handle = await controlledElement(control);
    const value = await readOpen(handle);
    return { value, missing: value === null, count: value === null ? 0 : 1 };
  }
  const target = resolveTarget(root, outcome.target);
  const count = await target.count();
  if (outcome.kind === "visible") {
    return { value: count === 1 ? await target.isVisible() : false, missing: count === 0, count };
  }
  if (count !== 1) return { value: null, missing: count === 0, count };
  switch (outcome.kind) {
    case "text": return { value: normalizeText(await target.textContent({ timeout: timeoutMs })), missing: false, count };
    case "checked": return { value: await target.isChecked({ timeout: timeoutMs }), missing: false, count };
    case "attribute": return { value: await target.getAttribute(outcome.attribute, { timeout: timeoutMs }), missing: false, count };
    case "open": return { value: await readOpen(await target.elementHandle({ timeout: timeoutMs })), missing: false, count };
    default: throw new Error(`Unsupported observable outcome: ${outcome.kind}`);
  }
};

const inspectControl = async control => {
  const count = await control.count();
  if (count === 0) return "action control not found";
  if (count > 1) return `action control name is ambiguous (${count} matches)`;
  if (!(await control.isVisible())) return "action control is not visible";
  if (!(await control.isEnabled())) return "action control is disabled";
  if (await isInert(control)) return "action control is inert";
  return null;
};

// Capture pre-state, activate the control through its role/name, wait for
// the explicitly expected post-state with Playwright auto-waiting bounded by
// timeoutMs, and return a report. No coordinates and no fixed sleeps.
// `root` is a Page or a Locator; `scope` on the contract or a target narrows
// resolution further.
export const runActionAssertion = async (root, contract) => {
  const { ruleId, action, outcome: declaredOutcome, timeoutMs, scope } = validateActionContract(contract);
  const outcome = { ...declaredOutcome, target: withScope(declaredOutcome.target, scope) };
  const control = resolveTarget(root, withScope(action, scope));
  const controlIssue = await inspectControl(control);
  // Pin the element: activation may legitimately remove the control from the
  // accessibility tree (a dialog's close button), but it is still the element
  // whose controlled target must be observed.
  const element = controlIssue ? null : await control.elementHandle({ timeout: timeoutMs });
  const pre = controlIssue ? { value: null, missing: true } : await observeOutcome(root, element, outcome, timeoutMs);
  const premise = evaluateActionPremise({ ruleId, action, outcome, pre, controlIssue });
  if (premise) return premise;

  // Focus first, as a keyboard user reaches it: a skip link is positioned
  // off-screen until it has focus, and focus never changes another control's
  // state. Then activate it through the pointer path.
  await control.focus({ timeout: timeoutMs });
  await control.click({ timeout: timeoutMs });

  const appeared = await expect(async () => {
    const observed = await observeOutcome(root, element, outcome, timeoutMs);
    expect(matchesExpected(observed.value, outcome.expected), `observed ${JSON.stringify(observed.value)}`).toBe(true);
  }).toPass({ timeout: timeoutMs }).then(() => true, () => false);
  const post = await observeOutcome(root, element, outcome, timeoutMs);
  return { ...evaluateActionOutcome({ ruleId, action, outcome, pre, post }), waitedForExpected: appeared };
};

export const expectActionOutcome = async (root, contract) => {
  const report = await runActionAssertion(root, contract);
  expect(report.status, `${formatReport(report)} (pre=${JSON.stringify(report.pre?.value)}, post=${JSON.stringify(report.post?.value)})`).toBe(STATUS.PASS);
  return report;
};

// Enabled, rendered, non-inert action controls with the role and name
// Playwright resolves for them. A diagnostic inventory, NOT a no-op detector:
// it lists what needs an explicit scenario contract. <summary> is reported
// with the `disclosure` pseudo-role.
export const ACTION_SELECTOR = [
  "a[href]", "button", "summary",
  "input[type=submit]", "input[type=button]", "input[type=reset]", "input[type=image]",
  "[role=button]", "[role=link]", "[role=menuitem]", "[role=menuitemcheckbox]", "[role=menuitemradio]", "[role=tab]", "[role=switch]"
].join(", ");

export const inventoryActions = async (root, { includeHidden = false } = {}) => {
  const raw = await root.locator(ACTION_SELECTOR).evaluateAll((elements, includeHiddenInPage) => elements.map((element, index) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return {
      index,
      tag: element.tagName.toLowerCase(),
      type: element.getAttribute("type"),
      href: element.getAttribute("href"),
      // checkVisibility also excludes content of a closed <details>, which
      // can still report client rects.
      rendered: rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" &&
        (typeof element.checkVisibility !== "function" || element.checkVisibility({ visibilityProperty: true, contentVisibilityAuto: true })),
      disabled: element.matches(":disabled") || element.getAttribute("aria-disabled") === "true",
      inert: Boolean(element.closest("[inert]"))
    };
  }).filter(item => !item.disabled && !item.inert && (includeHiddenInPage || item.rendered)), includeHidden);
  const locator = root.locator(ACTION_SELECTOR);
  return Promise.all(raw.map(async item => {
    if (item.tag === "summary") {
      return { ...item, role: "disclosure", name: normalizeText(await locator.nth(item.index).textContent()) };
    }
    const element = locator.nth(item.index);
    const snapshot = item.rendered ? await element.ariaSnapshot().catch(() => "") : "";
    const match = /^- ([a-z]+)(?: "((?:[^"\\]|\\.)*)")?/.exec(snapshot.split("\n")[0] ?? "");
    if (match) return { ...item, role: match[1], name: match[2]?.replace(/\\(.)/g, "$1") ?? "" };
    // Hidden controls (closed dialogs and popovers) have no snapshot yet:
    // approximate from markup; the assertion re-resolves them once shown.
    const fallback = await element.evaluate(node => ({
      role: node.getAttribute("role") ?? (node.tagName === "A" ? "link" : "button"),
      name: (node.getAttribute("aria-label") ?? node.textContent ?? "").replace(/\s+/g, " ").trim()
    }));
    return { ...item, ...fallback, approximate: true };
  }));
};

// Backwards-compatible diagnostic inventory from the first helper release.
export const inventoryVisibleActions = page =>
  page.locator('button:not([disabled]), input[type="button"]:not([disabled]), input[type="submit"]:not([disabled]), a[role="button"]:not([aria-disabled="true"])')
    .evaluateAll(elements => elements.filter(el => {
      const rect = el.getBoundingClientRect();
      const style = getComputedStyle(el);
      return rect.width > 0 && rect.height > 0 && style.visibility !== "hidden" && style.display !== "none" && !el.closest("[inert]");
    }).map(el => ({
      tag: el.tagName.toLowerCase(),
      nameHint: el.getAttribute("aria-label") || el.textContent?.trim() || el.getAttribute("value") || "",
      type: el.getAttribute("type") || "",
      url: el.getAttribute("href") || null
    })));

// ---------------------------------------------------------------------------
// ULI-L05/L06/L09/A08: overflow, label integrity, text scale, focus order
// ---------------------------------------------------------------------------

export { CONTRACT_WIDTHS };

// Observe horizontal overflow of the page. Only containers that clip
// (overflow hidden or clip) count as hiding text; scroll containers keep
// their content reachable and are skipped.
export const measureOverflow = async page => {
  await settleFontsAndFrames(page);
  const observation = await page.evaluate(() => {
    const viewport = document.documentElement.clientWidth;
    const clippingAncestor = element => {
      for (let node = element; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowX;
        if (overflow === "auto" || overflow === "scroll") return { scroller: true };
        if (overflow === "hidden" || overflow === "clip") {
          const rect = node.getBoundingClientRect();
          // A 1px clipped box is the visually-hidden pattern: not visible text.
          return rect.width <= 1 || rect.height <= 1 ? { hidden: true } : { rect };
        }
      }
      return null;
    };
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const texts = [];
    for (let text = walker.nextNode(); text; text = walker.nextNode()) texts.push(text);
    const escapes = texts.flatMap(text => {
      const element = text.parentElement;
      if (!text.textContent.trim() || !element?.checkVisibility()) return [];
      const own = element.getBoundingClientRect();
      if (own.width <= 1 && own.height <= 1) return []; // visually hidden text
      const clip = clippingAncestor(element);
      if (clip?.scroller || clip?.hidden) return [];
      const range = document.createRange();
      range.selectNodeContents(text);
      const escaped = [...range.getClientRects()].find(rect => rect.width > 0 &&
        (rect.right > viewport + 0.5 || rect.left < -0.5 || (clip?.rect && (rect.right > clip.rect.right + 0.5 || rect.left < clip.rect.left - 0.5))));
      return escaped ? [`${element.tagName.toLowerCase()} "${text.textContent.trim().slice(0, 40)}" ${escaped.left.toFixed(1)}..${escaped.right.toFixed(1)}`] : [];
    });
    return { scrollWidth: document.documentElement.scrollWidth, clientWidth: viewport, escapes };
  });
  return evaluateOverflow(observation);
};

export const expectNoOverflow = async page => {
  const report = await measureOverflow(page);
  expect(report.status, `${report.ruleId} ${report.status}: ${report.reason}\n${report.escapes.join("\n")}`).toBe(STATUS.PASS);
  return report;
};

// Scale the browser's default text size, as a user's font-size setting does,
// so rem/em sizes and em-based media queries respond. Chromium only (CDP).
export const scaleDefaultText = async (page, factor) => {
  if (!(factor > 0)) throw new Error("scaleDefaultText requires a positive factor.");
  const session = await page.context().newCDPSession(page);
  await session.send("Page.setFontSizes", { fontSizes: { standard: Math.round(16 * factor), fixed: Math.round(13 * factor) } });
  await expect.poll(() => page.evaluate(() => parseFloat(getComputedStyle(document.documentElement).fontSize)),
    { message: "default text size did not change (is the root font size fixed in px?)" }).toBeGreaterThan(16 * factor - 1);
  await settleFontsAndFrames(page);
};

export const measureLabelIntegrity = async locator => {
  await expect(locator).toBeVisible();
  const observation = await locator.evaluate(element => {
    const rect = element.getBoundingClientRect();
    return {
      name: element.textContent.replace(/\s+/g, " ").trim(),
      display: getComputedStyle(element).display,
      scrollWidth: element.scrollWidth, clientWidth: element.clientWidth,
      scrollHeight: element.scrollHeight, clientHeight: element.clientHeight,
      left: rect.left, right: rect.right, viewport: document.documentElement.clientWidth
    };
  });
  return evaluateLabelIntegrity(observation);
};

export const expectLabelIntact = async locator => {
  const report = await measureLabelIntegrity(locator);
  expect(report.status, `${report.ruleId} ${report.status}: "${report.name}": ${report.reason}`).toBe(STATUS.PASS);
  return report;
};

// An in-page destination is reached when it is in the viewport and not
// covered by a sticky header (ULI-L05).
export const expectDestinationClearOfHeader = async (page, destination, header, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) => {
  await expect(destination).toBeInViewport({ timeout: timeoutMs });
  await expect.poll(async () => {
    const [target, bar] = await Promise.all([destination.boundingBox(), header.boundingBox()]);
    return target && bar ? target.y - (bar.y + bar.height) : Number.NEGATIVE_INFINITY;
  }, { timeout: timeoutMs, message: "ULI-L05: destination must not sit under the sticky header" }).toBeGreaterThanOrEqual(0);
};

// Press Tab up to maxSteps times and record which elements received focus
// (ULI-A08). The record lives in the page so locators can query it.
export const recordTabSequence = async (page, maxSteps) => {
  if (!(Number.isInteger(maxSteps) && maxSteps > 0)) throw new Error("recordTabSequence requires a positive integer maxSteps.");
  await page.evaluate(() => { window.__uliFocused = new Set(); });
  const step = async remaining => {
    if (remaining === 0) return;
    await page.keyboard.press("Tab");
    await page.evaluate(() => window.__uliFocused.add(document.activeElement));
    await step(remaining - 1);
  };
  await step(maxSteps);
};

export const wasFocusedDuringSequence = locator => locator.evaluate(element => window.__uliFocused?.has(element) ?? false);
