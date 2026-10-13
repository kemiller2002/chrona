// Chrona against GitHub, end to end in Chromium (WI-0069): records stored
// through Arca's GitHub provider in the WASM engine, over the browser's own
// fetch, against the fake GitHub API in ./github-fake.js. Sign-in is Fides'
// fake (./support.js). Then deep links to stored records, periods under
// review, reports across people, the organization in the address and the
// observation inboxes. The shared fixtures are in ./github-app.js; two tabs,
// the queue's move to IndexedDB and the read cache are in
// ./app-github-offline.spec.js.
//
// Going offline is the browser's own (context.setOffline), so requests fail
// as they would without a network and the browser logs each one; this suite
// therefore keeps its own guard, which allows only those.
import { test, expect, configuration, signedIn, references, record, stored, returnTo, headline } from "./github-app.js";
import { PAGE, signInAs, fakeDeployment } from "./support.js";
import { repository, headFiles, history, serveGitHub } from "./github-fake.js";

test.use({ timezoneId: "America/New_York" });

test("a signed-in person's organization is set up on GitHub and what they record is stored there", async ({ context, github }) => {
  const page = await signedIn(context);
  await expect(headline(page)).toHaveText("All changes saved");
  // The organization was founded by its listed administrator, in Chrona's folder.
  expect(Object.keys(headFiles(github.current())).some((path) => path.startsWith("deployments/test/chrona/"))).toBe(true);

  await references(page);
  await expect(headline(page)).toHaveText("All changes saved");
  const before = history(github.current()).length;
  await record(page, "08:10", "09:02", "Visual engineering research");
  await expect(headline(page)).toHaveText("All changes saved");
  expect(history(github.current()).length).toBe(before + 1);
  expect(stored(github, "Visual engineering research")).toHaveLength(1);

  // Another tab reads it back from GitHub.
  const other = await signedIn(context);
  await other.click(".chrona-nav__link:has-text('Today')");
  await expect(other.locator("#day-records .chrona-record__title")).toHaveText(["Visual engineering research"]);
});

// ---- deep links to stored records (CHX-460, WI-0071) -------------------------

test("an activity's link opens it cold in a new tab, through sign-in, with its month read from GitHub", async ({ context, github }) => {
  const page = await signedIn(context);
  await references(page);
  await record(page, "08:10", "09:02", "Visual engineering research");
  await expect(headline(page)).toHaveText("All changes saved");
  await page.click(".chrona-nav__link:has-text('Today')");
  const href = await page.locator("#day-records .chrona-record__title a").getAttribute("href");
  expect(href).toMatch(/^#\/entries\/[^?]+\?on=\d{4}-\d{2}-\d{2}$/);
  expect(stored(github, "Visual engineering research")).toHaveLength(1);

  // A new tab: no session yet, so sign-in first, keeping the target.
  const other = await context.newPage();
  await other.goto(`/web/index.html${href}`);
  await expect(other.locator("#sign-in")).toBeVisible();
  await expect(other).toHaveURL(/#\/sign-in\?returnTo=%2Fentries%2F/);
  await other.click("#sign-in-button");
  await expect(other.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(other).toHaveURL(`${PAGE}${href}`);
  await expect(other.locator("#activity-title")).toHaveText("Visual engineering research");

  // An activity the records do not hold is the not-found page, once they are read.
  const missing = await context.newPage();
  await missing.goto(`/web/index.html#/entries/ACT-none?on=${href.slice(-10)}`);
  await missing.click("#sign-in-button");
  await expect(missing.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(missing.locator("#problem-title")).toHaveText("Not found");
});

test("someone who is not an administrator is refused the administrators' parts of More", async ({ context, github }) => {
  // The administrator adds a member who records only their own time.
  const page = await signedIn(context);
  await page.click(".chrona-nav__link:has-text('More')");
  await page.fill("#member-id", "42");
  await page.fill("#member-name", "Hubot");
  await page.selectOption("#member-access", "ownTime");
  await page.click("#admit");
  await expect(page.locator("#people")).toContainText("Hubot");
  await expect(headline(page)).toHaveText("All changes saved");

  // That member opens the people page from a link.
  signInAs("42", "hubot");
  const member = await context.newPage();
  await member.goto("/web/index.html#/more/people");
  await member.click("#sign-in-button");
  await expect(member.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  await expect(member).toHaveURL(`${PAGE}#/more/people`);
  await expect(member.locator("#problem-title")).toHaveText("Not permitted");
  await expect(member.locator("#problem-detail")).toContainText("administrators");
  await expect(member.locator("#screen-more")).toHaveCount(0);

  // The parts of More every member works in open.
  await member.goto("/web/index.html#/more/references");
  await expect(member.locator("#screen-more")).toBeVisible();
  await expect(member.locator("#screen-problem")).toHaveCount(0);
});

// ---- periods under review (WI-0036) ------------------------------------------

test("a member submits their period, the administrator approves it, and the member's period is closed", async ({ context, github }) => {
  // The administrator adds a member, requires approval and saves it.
  const admin = await signedIn(context);
  await references(admin);
  await admin.fill("#member-id", "42");
  await admin.fill("#member-name", "Hubot");
  await admin.selectOption("#member-access", "ownTime");
  await admin.click("#admit");
  await expect(admin.locator("#people")).toContainText("Hubot");
  await admin.check("#period-submission-expected");
  await admin.check("#period-approval-required");
  await admin.click("#save-period-settings");
  await expect(headline(admin)).toHaveText("All changes saved");
  expect(Object.keys(headFiles(github.current())).some((path) => path.endsWith("records/chrona.configuration/periods.json"))).toBe(true);
  await admin.close();

  // The member records time and submits the period.
  signInAs("42", "hubot");
  const member = await signedIn(context);
  await record(member, "08:10", "09:02", "Hubot's research");
  await expect(headline(member)).toHaveText("All changes saved");
  await member.goto("/web/index.html#/periods");
  await member.click("#submit-period");
  await expect(member.locator("#period-review-state")).toContainText("waiting for approval");
  await expect(headline(member)).toHaveText("All changes saved");
  // Its time is held: a new entry in it is refused with the stable code.
  await member.click(".chrona-nav__link:has-text('Track')");
  await member.selectOption("#manual-activity-type", { label: "Research" });
  await member.selectOption("#manual-project", { label: "HelixNote" });
  await member.fill("#manual-start-date", await member.evaluate(() => new Date().toLocaleDateString("en-CA")));
  await member.fill("#manual-start-time", "10:00");
  await member.fill("#manual-end-time", "10:30");
  await member.fill("#manual-description", "Too late");
  await member.fill("#manual-purpose", "Ship the slice");
  await member.click("#save-manual");
  await expect(member.locator('[data-code="CHRONA.REVIEW.SUBMITTED_PERIOD"]')).toBeVisible();
  await member.close();

  // The administrator sees it waiting, and approves it with a note.
  signInAs("583231", "octocat");
  const approver = await signedIn(context);
  await approver.goto("/web/index.html#/periods");
  await expect(approver.locator("#approvals")).toContainText("Hubot");
  await approver.fill("#approvals input", "Looks right");
  await approver.click("#approvals button:has-text('Approve')");
  await expect(approver.locator("#approvals")).toHaveCount(0);
  await expect(headline(approver)).toHaveText("All changes saved");
  await approver.close();

  // The member's period is closed, approved by the administrator.
  signInAs("42", "hubot");
  const back = await signedIn(context);
  await back.goto("/web/index.html#/periods");
  await expect(back.locator("#period-review-state")).toContainText("Approved by octocat");
  await expect(back.locator("#period-review-state")).toContainText("Looks right");
  expect(Object.keys(headFiles(github.current())).filter((path) => path.includes("records/chrona.review/"))).toHaveLength(2);
});

// ---- reports across people, on stored data (WI-0039) ---------------------------

test("an administrator's link to everyone's time opens cold, reads each person's month from GitHub, and a member is not permitted it", async ({ context, github }) => {
  const admin = await signedIn(context);
  await references(admin);
  await admin.fill("#member-id", "42");
  await admin.fill("#member-name", "Hubot");
  await admin.selectOption("#member-access", "ownTime");
  await admin.click("#admit");
  await expect(admin.locator("#people")).toContainText("Hubot");
  await record(admin, "07:30", "08:00", "Octocat's planning");
  await expect(headline(admin)).toHaveText("All changes saved");
  await admin.close();

  signInAs("42", "hubot");
  const member = await signedIn(context);
  await record(member, "08:10", "09:02", "Hubot's research");
  await expect(headline(member)).toHaveText("All changes saved");
  await member.close();

  // The administrator opens a link to everyone's time, grouped by person.
  signInAs("583231", "octocat");
  const lead = await context.newPage();
  await lead.goto("/web/index.html#/reports?people=everyone&group=person");
  await expect(lead.locator("html")).toHaveAttribute("data-kernel", "running");
  await lead.click("#sign-in-button");
  await expect(lead.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
  // The records open, then Hubot's month is read from GitHub: one more read,
  // its folder and records read together and only Hubot's record's history
  // asked for (WI-0077), within the default wait.
  await expect(lead.locator("#report-table")).toContainText("Hubot's research");
  await expect(lead.locator("#report-table")).toContainText("Hubot · Research · HelixNote");
  await expect(lead.locator("#report-table")).toContainText("Octocat's planning");
  await expect(lead.locator("#report-people")).toHaveValue("everyone");
  await expect(lead.locator("#report-groups")).toContainText("Hubot");
  await expect(lead.locator("#report-groups")).toContainText("octocat");
  // One person's time among everyone's; the address follows.
  await lead.selectOption("#report-person", { label: "Hubot" });
  await expect(lead.locator("#report-table")).not.toContainText("Octocat's planning");
  await expect(lead.locator("#report-table")).toContainText("Hubot's research");
  await expect(lead).toHaveURL(/people=everyone&person=github%3A42/);
  await lead.close();

  // The member may see only their own time: the same link is not permitted.
  signInAs("42", "hubot");
  const refused = await signedIn(context);
  await refused.goto("/web/index.html#/reports?people=everyone");
  await expect(refused.locator("#problem-title")).toHaveText("Not permitted");
  await expect(refused.locator("#report-table")).toHaveCount(0);
});

// ---- the organization in the address (CHX-460, WI-0071) -----------------------

test.describe("a deployment of two organizations", () => {
  const two = {
    ...configuration,
    organizations: [
      ...configuration.organizations,
      { id: "org_eu", displayName: "Acme Europe", slug: "acme-eu", timeZone: "Europe/Berlin", administrators: ["583231"] }
    ]
  };

  test("a link names its organization, and opens it cold through sign-in", async ({ context }) => {
    await fakeDeployment(context, two);
    await serveGitHub(context, repository({ owner: "acme", name: "chrona-data" }));

    // Signing in at home names the first organization in the address.
    const page = await signedIn(context);
    await expect(page).toHaveURL(`${PAGE}#/?org=org_acme`);
    await page.click(".chrona-nav__link:has-text('More')");
    await expect(page).toHaveURL(`${PAGE}#/more?org=org_acme`);

    // A link into the other organization, opened in a new tab.
    const other = await context.newPage();
    await other.goto("/web/index.html#/more?org=org_eu");
    await other.click("#sign-in-button");
    await expect(other.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
    await expect(other).toHaveURL(`${PAGE}#/more?org=org_eu`);
    await expect(other.locator("#organization")).toHaveValue("org_eu");

    // An organization the deployment does not serve is not found.
    await other.goto("/web/index.html#/more?org=org_none");
    await expect(other.locator("#problem-title")).toHaveText("Not found");
  });

  test("a link to a report across organizations opens cold, and reads each organization's own folder (WI-0075)", async ({ context }) => {
    await fakeDeployment(context, two);
    await serveGitHub(context, repository({ owner: "acme", name: "chrona-data" }));

    // Time in Acme Consulting, then in Acme Europe, each with its own reference data.
    const page = await signedIn(context);
    await references(page);
    await record(page, "08:10", "09:02", "Acme research");
    await expect(headline(page)).toHaveText("All changes saved");
    await page.goto("/web/index.html#/more?org=org_eu");
    await expect(page.locator("#organization")).toHaveValue("org_eu");
    await references(page);
    await record(page, "10:00", "10:30", "Europe research");
    await expect(headline(page)).toHaveText("All changes saved");
    await page.close();

    // A link to all my organizations' time, grouped by organization, opened cold.
    const lead = await context.newPage();
    await lead.goto("/web/index.html#/reports?organizations=all&group=organization&org=org_acme");
    await lead.click("#sign-in-button");
    await expect(lead.locator(".chrona-shell")).toBeVisible({ timeout: 20_000 });
    await expect(lead.locator("#report-table")).toContainText("Europe research");
    await expect(lead.locator("#report-table")).toContainText("Acme research");
    await expect(lead.locator("#report-table")).toContainText("Acme Europe · Research · HelixNote");
    await expect(lead.locator("#report-organizations")).toHaveValue("all");
    await expect(lead.locator("#report-groups")).toContainText("Acme Consulting");
    await expect(lead.locator("#report-groups")).toContainText("Acme Europe");

    // This organization only: the address follows.
    await lead.selectOption("#report-organizations", "this");
    await expect(lead.locator("#report-table")).not.toContainText("Europe research");
    await expect(lead).not.toHaveURL(/organizations=all/);
  });
});

// ---- observation inboxes (WI-0038, requirement 18) ------------------------------

// Version 1 of the time-observation contract, as a producer writes it.
const observationText = (id, projectId, description) =>
  JSON.stringify({
    contract: "chrona.time-observation",
    version: 1,
    observationId: id,
    sourceSystem: "github",
    organizationId: "org_acme",
    projectId,
    actorId: "github:583231",
    workItemId: "PR-7",
    externalUrl: "https://example.test/pull/7",
    timing: { kind: "duration", minutes: 45 },
    description,
    evidence: [{ kind: "pull-request", reference: "https://example.test/pull/7" }],
    observedAt: new Date(Date.now() - 60 * 60 * 1000).toISOString()
  });

const dataset = "deployments/test/chrona/datasets/org_acme";

test("an observation a producer files becomes a candidate, and accepting it makes it the person's time", async ({ context, github }) => {
  const page = await signedIn(context);
  await references(page);
  await expect(headline(page)).toHaveText("All changes saved");
  const projectPath = Object.keys(headFiles(github.current())).find((path) => /\/records\/[^/]+\/project\/[^/]+\.json$/.test(path));
  const projectId = projectPath.split("/").pop().replace(/\.json$/, "");

  // A producer files an observation, and a payload that is not one.
  github.commit({
    [`${dataset}/inbox/github/obs-1.json`]: observationText("obs-1", projectId, "Reviewed pull request 7"),
    [`${dataset}/inbox/github/broken.json`]: "{}"
  });

  await page.goto("/web/index.html#/more/inbox");
  await page.click("#read-inboxes");
  await expect(page.locator("#inbox-status")).toContainText("1 observation was received");
  await expect(page.locator("#inbox-invalid")).toContainText("inbox/github/broken.json");
  const files = headFiles(github.current());
  expect(Object.keys(files)).toContain(`${dataset}/records/chrona.candidate/open/CAND-github-obs-1.json`);
  expect(Object.keys(files)).toContain(`${dataset}/records/chrona.receipt/github/obs-1.json`);
  expect(Object.keys(files)).toContain(`${dataset}/records/chrona.receipt/github/broken.json`);
  expect(Object.keys(files)).not.toContain(`${dataset}/inbox/github/obs-1.json`);

  // It waits for the person's decision, on Today and in the candidates.
  await page.click(".chrona-nav__link:has-text('Today')");
  await expect(page.locator("#obligations")).toContainText("1 observation awaits your decision");
  await page.goto("/web/index.html#/candidates");
  await page.click("#candidate-list a:has-text('Reviewed pull request 7')");
  await expect(page).toHaveURL(/#\/candidates\/CAND-github-obs-1$/);
  await expect(page.locator("#candidate-timing")).toContainText("45");

  await page.selectOption("#candidate-activity-type", { label: "Research" });
  await page.fill("#candidate-purpose", "Code review");
  await page.click("#accept-candidate");
  await expect(page.locator("p[role=status][aria-live=polite]")).toHaveText(/^Accepted /);
  await expect(page.locator("#candidate-state")).toHaveText("Accepted");
  await expect(headline(page)).toHaveText("All changes saved");

  const after = Object.keys(headFiles(github.current()));
  expect(after).not.toContain(`${dataset}/records/chrona.candidate/open/CAND-github-obs-1.json`);
  expect(after.some((path) => path.startsWith(`${dataset}/records/chrona.candidate/decided/`))).toBe(true);
  expect(stored(github, "Reviewed pull request 7")).toHaveLength(1);
});

test.describe("on a phone", () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });

  test("a candidate's page fits the screen, and its link opens it cold through sign-in", async ({ context, github }) => {
    const page = await signedIn(context);
    await references(page, ".chrona-tabbar__link");
    const projectFile = () => Object.keys(headFiles(github.current())).find((path) => /\/records\/[^/]+\/project\/[^/]+\.json$/.test(path));
    await expect.poll(projectFile).toBeTruthy();
    const projectPath = projectFile();
    const projectId = projectPath.split("/").pop().replace(/\.json$/, "");
    github.commit({ [`${dataset}/inbox/github/obs-2.json`]: observationText("obs-2", projectId, "Paired on the release") });
    await page.close();

    // Opened cold from its link: sign-in, the inbox read, then the candidate.
    const cold = await context.newPage();
    await cold.goto("/web/index.html#/candidates/CAND-github-obs-2");
    await expect(cold.locator("html")).toHaveAttribute("data-kernel", "running");
    await cold.click("#sign-in-button");
    await expect(cold.locator("#candidate-title")).toHaveText("Paired on the release", { timeout: 20_000 });
    await expect(cold).toHaveURL(/#\/candidates\/CAND-github-obs-2$/);
    await expect(cold.locator("#accept-candidate")).toBeVisible();
    expect(await cold.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBe(0);
  });
});
