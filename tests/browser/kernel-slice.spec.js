// The kernel verification slice end to end: DOM event -> Limen kernel ->
// WASM shim -> F# engine -> Storage effect -> kernel -> result -> engine ->
// view. Ported check for check from the TypeScript slice's
// verification/kernel-slice/test/browser-verification.mjs; every check is an
// observation of live behaviour, not a claim derived from reading source.
import { test, expect } from "./support.js";

const status = (page) => page.locator("#status");
const logItems = (page) => page.locator("#log li").allTextContents();

test("the page runs on Limen, Forma and Folio from the pinned packages", async ({ slice: page }) => {
  const sheets = await page.evaluate(() =>
    Array.from(document.styleSheets).flatMap((sheet) =>
      Array.from(sheet.cssRules).filter((rule) => rule instanceof CSSImportRule).map((rule) => rule.href)));
  expect(sheets).toEqual(expect.arrayContaining([
    "../node_modules/@echelon-foundry/design-system/dist/all.css",
    "../node_modules/@echelon-foundry/print-components/src/styles/print.css"
  ]));
  const token = await page.evaluate(() =>
    getComputedStyle(document.documentElement).getPropertyValue("--ef-color-accent-primary").trim());
  expect(token).not.toBe("");
  // A plain <span> is inline; Forma's status lozenge rule lays it out.
  await expect(page.locator("#log-count")).not.toHaveCSS("display", "inline");

  // Folio: its print elements are registered (upgraded), not unknown tags.
  expect(await page.evaluate(() => ["ef-print-document", "ef-print-table", "ef-print-page-number"]
    .every((name) => customElements.get(name) !== undefined))).toBe(true);
  // Forma's authoring wrappers stay inert: never registered as elements.
  expect(await page.evaluate(() => customElements.get("ef-alert"))).toBeUndefined();
});

test("the bridge vocabulary, a Storage round trip and reload, end to end", async ({ slice: page }) => {
  // the module graph loads and the engine projects an initial view
  await expect(status(page)).toHaveText("Type a label, then save it.");

  // the 0.7.x handshake negotiates protocol 1.4 on the core contract, with no capability packs
  await expect(page.locator("html")).toHaveAttribute("data-protocol", "1.4");
  await expect(page.locator("html")).toHaveAttribute("data-capabilities", "");

  // data-bind-disabled reflects a boolean into the DOM property
  await expect(page.locator("#save")).toBeDisabled(); // save starts disabled from Empty
  await expect(page.locator("#load")).toBeEnabled(); // load starts enabled

  // data-event with data-on=input dispatches per keystroke and projects back
  await page.fill("#label", "alpha");
  await expect(status(page)).toHaveText("Ready to save.");
  await expect(page.locator("#save")).toBeEnabled();

  // data-if mounts a template only when its view key is truthy
  await expect(page.locator(".problem")).toHaveCount(0); // no problem panel while valid
  await page.fill("#label", "x".repeat(25));
  await expect(page.locator("#problem")).toHaveText(/at most 24/);
  // the problem kind is a data-* cue for CSS, not an inline style
  await expect(page.locator(".problem")).toHaveAttribute("data-problem", "invalid");

  // data-if unmounts again when the key goes falsy
  await page.fill("#label", "alpha");
  await expect(page.locator(".problem")).toHaveCount(0);

  // the engine guard disables save, shadowing native validation entirely
  await page.fill("#label", "");
  await expect(page.locator("#save")).toBeDisabled();

  // native reportValidity gates dispatch when the button is reachable:
  // "a" is engine-valid (>= 1 char) so save is enabled, but HTML-invalid
  // (minlength=2), so the native gate must stop it before the engine sees it.
  await page.fill("#label", "a");
  await expect(page.locator("#save")).toBeEnabled();
  const before = await logItems(page);
  await page.click("#save", { timeout: 5000 });
  await page.waitForTimeout(200);
  expect(await logItems(page), "an HTML-invalid form must not reach the engine").toEqual(before);
  await expect(status(page)).not.toHaveText(/^Saved/); // no save may have occurred

  // a full Storage effect round trip completes: request -> kernel -> result -> transition
  await page.fill("#label", "alpha");
  await page.click("#save");
  await expect(status(page)).toHaveText('Saved "alpha".');
  await expect(status(page)).toHaveAttribute("data-phase", "Saved");

  // the effect actually reached localStorage (the kernel really executed it)
  expect(await page.evaluate(() => window.localStorage.getItem("chrona.kernel-slice.label"))).toBe("alpha");

  // data-each renders one keyed item per array entry, reading item scope
  const items = await logItems(page);
  expect(items.length).toBeGreaterThanOrEqual(2);
  expect(items.some((t) => t.includes("save requested"))).toBe(true);
  expect(items.some((t) => t.includes("Saving: Success"))).toBe(true);

  // state survives reload and Load projects it back
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(status(page)).toHaveText("Type a label, then save it."); // fresh engine after reload
  await page.click("#load");
  await expect(status(page)).toHaveText('Loaded "alpha".');
  await expect(page.locator("#label")).toHaveValue("alpha");
});

test("a browser navigation reaches the engine as LocationChanged and changes nothing", async ({ slice: page }) => {
  await page.fill("#label", "alpha");
  await expect(status(page)).toHaveText("Ready to save.");
  const before = { status: await status(page).textContent(), log: await logItems(page) };
  const dispatchedBefore = await page.evaluate(() => window.__dispatched.length);
  await page.evaluate(() => history.pushState(null, "", "#probe"));
  await page.evaluate(() => new Promise((resolve) => {
    window.addEventListener("popstate", () => setTimeout(resolve, 100), { once: true });
    history.back();
  }));
  await expect(status(page)).toHaveText(before.status);
  expect(await logItems(page)).toEqual(before.log);
  const dispatched = await page.evaluate((n) => window.__dispatched.slice(n), dispatchedBefore);
  expect(dispatched, "exactly one LocationChanged must reach the engine").toEqual(["LocationChanged"]);
  await expect(page.locator("#operational-fault")).toHaveCount(0);
});

test("data-each reconciliation keeps DOM node identity for unchanged keys", async ({ slice: page }) => {
  await page.fill("#label", "alpha");
  await page.click("#save");
  await expect(status(page)).toHaveText('Saved "alpha".');
  await page.evaluate(() => document.querySelector("#log li")?.setAttribute("data-probe", "kept"));
  await page.fill("#label", "beta");
  await page.click("#save");
  await expect(status(page)).toHaveText('Saved "beta".');
  // the first item's DOM node must be reused, not recreated
  await expect(page.locator("#log li").first()).toHaveAttribute("data-probe", "kept");
});

test("the print surface is Folio's document, projecting the same round trips", async ({ slice: page }) => {
  await page.fill("#label", "alpha");
  await page.click("#save");
  await expect(status(page)).toHaveText('Saved "alpha".');
  await expect(page.locator(".print-surface")).toBeHidden();
  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".print-surface")).toBeVisible();
  await expect(page.locator("#log")).toBeHidden();
  const rows = await page.locator(".print-surface ef-print-table tbody tr").evaluateAll((trs) =>
    trs.map((tr) => Array.from(tr.querySelectorAll("td")).map((td) => td.textContent.trim())));
  expect(rows).toEqual([["entry-1", "save requested"], ["entry-2", "Saving: Success (value=null)"]]);
});
