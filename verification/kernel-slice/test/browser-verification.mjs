// Observes the kernel bridge running in a real browser.
//
// Everything here is an observation of live behavior, not a claim derived
// from reading source. Run: node verification/kernel-slice/test/browser-verification.mjs
// (requires a static server on BASE, started by run-browser-verification.sh)

import assert from "node:assert/strict";
import { execSync } from "node:child_process";
import { pathToFileURL } from "node:url";

// Playwright is installed globally in this environment, not as a project
// dependency. NODE_PATH does not apply to ESM, so resolve it by path.
const globalRoot = (process.env.PLAYWRIGHT_ROOT ?? execSync("npm root -g").toString()).trim();
const playwright = await import(pathToFileURL(`${globalRoot}/playwright/index.js`).href);
// playwright ships CJS; depending on lexing, named exports may only appear
// under `default`.
const chromium = playwright.chromium ?? playwright.default?.chromium;
if (!chromium) throw new Error(`Could not resolve chromium from ${globalRoot}/playwright`);

const BASE = process.env.SLICE_BASE ?? "http://127.0.0.1:4173";
const PAGE = `${BASE}/verification/kernel-slice/index.html`;

const checks = [];
const check = async (name, fn) => {
  try {
    await fn();
    checks.push({ name, ok: true });
    console.log(`ok   ${name}`);
  } catch (error) {
    checks.push({ name, ok: false, detail: String(error && error.message) });
    console.log(`FAIL ${name}\n     ${error && error.message}`);
  }
};

const browser = await chromium.launch();
const page = await browser.newPage();
const consoleErrors = [];
page.on("console", (m) => { if (m.type() === "error") consoleErrors.push(m.text()); });
page.on("pageerror", (e) => consoleErrors.push(`pageerror: ${e.message}`));

await page.goto(PAGE, { waitUntil: "networkidle" });

const status = () => page.textContent("#status");
const bridgeErrors = () => page.evaluate(() => window.__bridgeErrors ?? ["<harness absent>"]);
const logItems = () => page.$$eval("#log li", (els) => els.map((e) => e.textContent));

await check("the module graph loads and the engine projects an initial view", async () => {
  assert.equal((await status())?.trim(), "Type a label, then save it.");
});

await check("data-bind-disabled reflects a boolean into the DOM property", async () => {
  assert.equal(await page.isDisabled("#save"), true, "save starts disabled from Empty");
  assert.equal(await page.isDisabled("#load"), false, "load starts enabled");
});

await check("data-event with data-on=input dispatches per keystroke and projects back", async () => {
  await page.fill("#label", "alpha");
  await page.waitForFunction(() => document.querySelector("#status")?.textContent === "Ready to save.");
  assert.equal(await page.isDisabled("#save"), false);
});

await check("data-if mounts a template only when its view key is truthy", async () => {
  assert.equal(await page.locator(".problem").count(), 0, "no problem panel while valid");
  await page.fill("#label", "x".repeat(25));
  await page.waitForSelector(".problem");
  assert.match(await page.textContent(".problem"), /at most 24/);
});

await check("data-if unmounts again when the key goes falsy", async () => {
  await page.fill("#label", "alpha");
  await page.waitForFunction(() => document.querySelectorAll(".problem").length === 0);
  assert.equal(await page.locator(".problem").count(), 0);
});

await check("the engine guard disables save, shadowing native validation entirely", async () => {
  await page.fill("#label", "");
  await page.waitForFunction(() => document.querySelector("#save")?.disabled === true);
  assert.equal(await page.isDisabled("#save"), true);
});

await check("native reportValidity gates dispatch when the button is reachable", async () => {
  // "a" is engine-valid (>= 1 char) so save is enabled, but HTML-invalid
  // (minlength=2), so the native gate must stop it before the engine sees it.
  await page.fill("#label", "a");
  await page.waitForFunction(() => document.querySelector("#save")?.disabled === false);
  const before = await logItems();
  await page.click("#save", { timeout: 5000 });
  await page.waitForTimeout(200);
  assert.deepEqual(await logItems(), before, "an HTML-invalid form must not reach the engine");
  assert.ok(!(await status()).startsWith("Saved"), "no save may have occurred");
});

await check("a full Storage effect round trip completes: request -> kernel -> result -> transition", async () => {
  await page.fill("#label", "alpha");
  await page.click("#save");
  await page.waitForFunction(() => document.querySelector("#status")?.textContent?.startsWith("Saved"));
  assert.equal((await status())?.trim(), 'Saved "alpha".');
});

await check("the effect actually reached localStorage (Tier 4 really executed it)", async () => {
  const stored = await page.evaluate(() => window.localStorage.getItem("chrona.kernel-slice.label"));
  assert.equal(stored, "alpha");
});

await check("data-each renders one keyed item per array entry, reading item scope", async () => {
  const items = await logItems();
  assert.ok(items.length >= 2, `expected round-trip log entries, got ${JSON.stringify(items)}`);
  assert.ok(items.some((t) => t.includes("save requested")));
  assert.ok(items.some((t) => t.includes("Saving: Success")));
});

const accumulatedBridgeErrors = [];
await check("no bridge errors occurred before reload", async () => {
  // window.__bridgeErrors is wiped by a reload, so it must be drained here
  // or the post-reload check below is vacuous.
  accumulatedBridgeErrors.push(...(await bridgeErrors()));
  assert.deepEqual(accumulatedBridgeErrors, []);
});

await check("state survives reload and Load projects it back", async () => {
  await page.reload({ waitUntil: "networkidle" });
  assert.equal((await status())?.trim(), "Type a label, then save it.", "fresh engine after reload");
  await page.click("#load");
  await page.waitForFunction(() => document.querySelector("#status")?.textContent?.startsWith("Loaded"));
  assert.equal((await status())?.trim(), 'Loaded "alpha".');
});

await check("data-each reconciliation keeps DOM node identity for unchanged keys", async () => {
  await page.evaluate(() => {
    const first = document.querySelector("#log li");
    if (first) first.setAttribute("data-probe", "kept");
  });
  await page.fill("#label", "beta");
  await page.click("#save");
  await page.waitForFunction(() => document.querySelector("#status")?.textContent?.startsWith("Saved"));
  const kept = await page.getAttribute("#log li", "data-probe");
  assert.equal(kept, "kept", "the first item's DOM node must be reused, not recreated");
});

await check("no bridge errors across the whole run, and no uncaught page errors", async () => {
  assert.deepEqual([...accumulatedBridgeErrors, ...(await bridgeErrors())], []);
  assert.deepEqual(consoleErrors, []);
});

await page.screenshot({ path: "verification/kernel-slice/browser-verification.png", fullPage: true });
await browser.close();

const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} browser checks passed`);
process.exit(failed.length === 0 ? 0 : 1);
