// Chrona against GitHub in Chromium (WI-0069), offline and across tabs: two
// tabs of one browser and the queue of unsent changes they share (WI-0067,
// WI-0059): only the tab that holds it keeps it, in IndexedDB, and sends it;
// "use this tab instead" takes it over at once; the other tab holds it when
// the holder closes; nothing is sent twice. The move from the localStorage
// queue an older Chrona kept, including one interrupted after the copy. And
// opening offline from this browser's read cache (WI-0057). The shared
// fixtures are in ./github-app.js.
//
// Going offline is the browser's own (context.setOffline), so requests fail
// as they would without a network and the browser logs each one; this suite
// therefore keeps its own guard, which allows only those.
import { test, expect, QUEUE, signedIn, references, record, stored, outstanding, legacy, inIndexedDb, kept, returnTo, headline, online } from "./github-app.js";
import { history } from "./github-fake.js";

test.use({ timezoneId: "America/New_York" });

// ---- two tabs, one queue of unsent changes (WI-0067) ------------------------

// Two signed-in tabs of one browser, the organization set up with its
// reference data; the first tab holds the queue.
async function twoTabs(context) {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  const second = await signedIn(context);
  await expect(second.locator("#queue-elsewhere")).toBeVisible();
  await expect(first.locator("#queue-elsewhere")).toHaveCount(0);
  return { first, second };
}

test("with two tabs open, only the tab holding the unsent changes keeps them, in IndexedDB, and each change is sent once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  await second.click(".chrona-nav__link:has-text('More')");
  await expect(second.locator("#queue-mode")).toHaveText("Held by another Chrona tab");
  await first.click(".chrona-nav__link:has-text('More')");
  await expect(first.locator("#queue-mode")).toHaveText("Kept in this browser (IndexedDB) by this tab");
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await expect(headline(first)).toHaveText("Offline: 1 change waits to be sent");
  await expect(headline(second)).toHaveText("Offline: 1 change waits to be sent");
  // Only the holder's change is kept in the browser, and in IndexedDB.
  const queued = await inIndexedDb(first);
  expect(queued).toHaveLength(1);
  expect(JSON.stringify(queued)).toContain("Pairing");
  // Read from GitHub, so conditioned on the state of the organization's
  // folder it was read at (Arca 0.4.0), and naming its account.
  expect(queued[0].operation.expectedNamespaceToken).toEqual(expect.any(String));
  expect(queued[0].operation.accountId).toBe("actor:github:583231");
  expect(await legacy(first)).toEqual([]);
  expect(history(github.current()).length).toBe(before);

  // Back online, each tab sends its own change, once.
  await online(context, github, true);
  await expect(headline(first)).toHaveText("All changes saved", { timeout: 20_000 });
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 2);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(stored(github, "Review")).toHaveLength(1);
  expect(await kept(first)).toEqual([]);
});

test("\"use this tab instead\" takes the unsent changes over at once; the other tab says so, and every change is sent once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");
  await second.click("#take-over-queue");

  // The second tab holds both now; the first hears it lost them.
  await expect(second.locator("#queue-elsewhere")).toBeHidden();
  await expect(headline(second)).toHaveText("Offline: 2 changes wait to be sent");
  await expect(first.locator("#queue-elsewhere")).toBeVisible();
  expect(await inIndexedDb(second)).toHaveLength(2);

  // The first tab goes on, its new change in its page only.
  await record(first, "13:00", "14:00", "Planning");
  await expect(headline(first)).toHaveText("Offline: 1 change waits to be sent");
  expect(await inIndexedDb(second)).toHaveLength(2);

  await online(context, github, true);
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  await expect(headline(first)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 3);
  for (const description of ["Pairing", "Review", "Planning"]) expect(stored(github, description)).toHaveLength(1);
  expect(await kept(second)).toEqual([]);
});

test("when the holding tab closes, the other tab holds both tabs' changes once the person returns to it, and sends each once", async ({ context, github }) => {
  const { first, second } = await twoTabs(context);
  const before = history(github.current()).length;

  await online(context, github, false);
  await record(first, "09:00", "10:00", "Pairing");
  await record(second, "11:00", "12:00", "Review");

  // The first tab closes with its change unsent; the person returns to the
  // second, which asks for the queue again and takes the change over.
  await first.close();
  // The browser lets go of the closed tab's lock once the tab is gone.
  await expect.poll(() => second.evaluate(async () => (await navigator.locks.query()).held.filter((lock) => lock.name.startsWith("arca.queue")).length)).toBe(0);
  await returnTo(second);
  await expect(second.locator("#queue-elsewhere")).toBeHidden();
  await expect(headline(second)).toHaveText("Offline: 2 changes wait to be sent");
  expect(await inIndexedDb(second)).toHaveLength(2);

  await online(context, github, true);
  await expect(headline(second)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 2);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(stored(github, "Review")).toHaveLength(1);
  expect(await kept(second)).toEqual([]);
  await second.click(".chrona-nav__link:has-text('Today')");
  await expect(second.locator("#day-records .chrona-record__title")).toHaveText(["Pairing", "Review"]);
});

// ---- the move from localStorage to IndexedDB (WI-0059) ----------------------

// A page as an older Chrona ran: no IndexedDB, so Arca keeps the queue in
// localStorage, under the key earlier releases used.
async function withoutIndexedDb(context) {
  const page = await context.newPage();
  await page.addInitScript(() => Object.defineProperty(window, "indexedDB", { value: undefined, configurable: true }));
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  return page;
}

// A tab signed in while GitHub cannot be reached: the records do not open.
async function unopened(context) {
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator("#store-failed")).toBeVisible({ timeout: 20_000 });
  return page;
}

// One change recorded offline where an older Chrona kept it: localStorage.
async function olderQueue(context, github) {
  const older = await withoutIndexedDb(context);
  await references(older);
  await expect(headline(older)).toHaveText("All changes saved");
  await older.click(".chrona-nav__link:has-text('More')");
  await expect(older.locator("#queue-mode")).toHaveText("Kept in this browser (localStorage) by this tab");
  await online(context, github, false);
  await record(older, "09:00", "10:00", "Pairing");
  await expect(headline(older)).toHaveText("Offline: 1 change waits to be sent");
  const text = await older.evaluate((key) => localStorage.getItem(key), QUEUE);
  expect(outstanding(text)).toHaveLength(1);
  await older.close();
  return text;
}

test("unsent changes an older Chrona kept in localStorage move to IndexedDB on first load, unchanged, are said, and are sent once", async ({ context, github }) => {
  const text = await olderQueue(context, github);
  const before = history(github.current()).length;

  // The page loads again with GitHub still out of reach: the records cannot
  // open, but the queue is read, moved and counted, and the move is said.
  await context.setOffline(false);
  const page = await unopened(context);
  await expect(page.locator("#offline-waiting")).toHaveText("1 change waits in this browser and is sent when your records open.");
  await expect(page.locator("#store-failed-queue-notice")).toHaveText("1 unsent change kept in this browser's older storage moved to IndexedDB, unchanged.");
  expect(await legacy(page)).toEqual([]);
  expect((await inIndexedDb(page)).map((entry) => JSON.stringify(entry))).toEqual(outstanding(text).map((entry) => JSON.stringify(entry)));
  await page.close();

  // With GitHub back, the records open and the moved change is sent once.
  github.reachable(true);
  const back = await signedIn(context);
  await expect(headline(back)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(await kept(back)).toEqual([]);
});

test("a move to IndexedDB interrupted after the copy, before localStorage was cleared, finishes on the next load and sends nothing twice", async ({ context, github }) => {
  const text = await olderQueue(context, github);
  const before = history(github.current()).length;

  // The copy is made and verified (GitHub out of reach, so nothing is
  // sent); the old queue is then put back, as if the page closed before
  // removing it.
  await context.setOffline(false);
  const interrupted = await unopened(context);
  await expect.poll(() => inIndexedDb(interrupted)).toHaveLength(1);
  await interrupted.evaluate(([key, value]) => localStorage.setItem(key, value), [QUEUE, text]);
  expect(await legacy(interrupted)).toHaveLength(1);
  await interrupted.close();

  github.reachable(true);
  const page = await signedIn(context);
  await expect(headline(page)).toHaveText("All changes saved", { timeout: 20_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Pairing")).toHaveLength(1);
  expect(await kept(page)).toEqual([]);
});

test("an older localStorage queue that cannot be read is left exactly as it is, and the person is told", async ({ context, github }) => {
  const first = await signedIn(context);
  await first.evaluate((key) => localStorage.setItem(key, "{not a queue"), QUEUE);
  await first.close();

  const page = await signedIn(context);
  await expect(page.locator("#queue-notice")).toContainText("Unsent changes kept in this browser's older storage cannot be read. They were left exactly as they are.");
  expect(await page.evaluate((key) => localStorage.getItem(key), QUEUE)).toBe("{not a queue");
  await expect(headline(page)).toHaveText("All changes saved");
});

// ---- opening offline from the read cache (WI-0057) --------------------------

test("with GitHub out of reach, a new tab opens the records from this browser's read cache, and a change made there is sent once GitHub is back", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await record(first, "08:10", "09:02", "Visual engineering research");
  await expect(headline(first)).toHaveText("All changes saved");
  const before = history(github.current()).length;
  await first.close();

  // GitHub cannot be reached; the page itself still loads.
  github.reachable(false);
  const page = await context.newPage();
  await page.goto("/web/index.html");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.click("#sign-in-button");
  await expect(page.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(headline(page)).toHaveText(/^Offline: your records as of /);
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#day-records .chrona-record__title")).toHaveText(["Visual engineering research"]);

  // A change made from the cache waits, conditioned on nothing cached.
  await record(page, "09:18", "11:04", "HelixNote development");
  expect(history(github.current()).length).toBe(before);
  const waiting = await inIndexedDb(page);
  expect(waiting).toHaveLength(1);
  expect(waiting[0].operation.expectedChangeToken ?? null).toBeNull();

  // GitHub back: the records are read from it, the change decided on them
  // and sent once.
  github.reachable(true);
  await expect(headline(page)).toHaveText("All changes saved", { timeout: 30_000 });
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "HelixNote development")).toHaveLength(1);
  expect(await kept(page)).toEqual([]);
});

test("signing out clears this account's read cache, so the next start with GitHub out of reach cannot open the records", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  await first.click(".chrona-nav__link:has-text('More')");
  await first.click("#sign-out");
  await expect(first.locator("#sign-in")).toBeVisible();
  await first.close();

  github.reachable(false);
  const page = await unopened(context);
  await expect(page.locator("#store-failure")).toHaveText("GitHub could not be reached.");
});

test("clearing this device removes what it keeps to open offline, then signs out", async ({ context, github }) => {
  const first = await signedIn(context);
  await references(first);
  await expect(headline(first)).toHaveText("All changes saved");
  await first.click(".chrona-nav__link:has-text('More')");
  await first.click("#clear-device");
  await expect(first.locator("#clear-device-confirm")).toBeVisible();
  await first.click("#clear-device-confirmed");
  await expect(first.locator("#sign-in")).toBeVisible({ timeout: 20_000 });
  const databases = await first.evaluate(async () => (await indexedDB.databases()).map((database) => database.name).filter((name) => name.startsWith("chrona/")));
  expect(databases).toEqual([]);
  await first.close();

  github.reachable(false);
  const page = await unopened(context);
  await expect(page.locator("#store-failure")).toHaveText("GitHub could not be reached.");
});
