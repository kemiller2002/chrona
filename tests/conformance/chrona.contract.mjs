// Chrona's declared UI conformance contract (GH-112, chrona#112) against
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 (ULI-L01..L13, ULI-A01..A10;
// the rules live there and are cited, never restated).
//
// Pure data. Every enabled action Chrona ships is declared here with its
// owner, the state that shows it, what it is for and the outcome a person
// can observe. `verify` names where that outcome is asserted:
//
//   "conformance"      tests/conformance/*.spec.js, through the vendored
//                      Forma helpers; the report is attached to the test.
//   { spec, title }    an existing browser test that asserts the outcome;
//                      it counts only if that test passed in the same run.
//   null               nothing asserts it yet: the evidence says UNKNOWN.
//
// The inventory test (app.spec.js) fails when the page shows an enabled
// action that is not declared here, so a new control joins this contract
// before it ships (the standard's "Discovery" rule).

const SPEC = {
  app: "tests/browser/app.spec.js",
  lifecycle: "tests/browser/app-lifecycle.spec.js",
  quick: "tests/browser/app-quick-entry.spec.js",
  reports: "tests/browser/app-reports.spec.js",
  deepLinks: "tests/browser/app-deep-links.spec.js",
  github: "tests/browser/app-github.spec.js",
  githubOffline: "tests/browser/app-github-offline.spec.js",
  signIn: "tests/browser/app-sign-in.spec.js",
  a11y: "tests/browser/app-accessibility.spec.js",
  reliability: "tests/browser/app-reliability.spec.js",
  slice: "tests/browser/kernel-slice.spec.js"
};

const existing = (spec, title) => ({ spec, title });

// An action. `pattern` (optional) matches inventoried names of a templated
// control (a record's title, a day's label); `name` is what the evidence
// shows for it.
const action = (id, role, name, intent, expectedOutcome, { owner, state = "local, empty", verify = null, pattern, reset = "fresh page per test" } = {}) => ({
  id, control: { role, name }, intent, expectedOutcome, owner, state, verify, pattern, reset
});

// Present on every screen of the signed-in or local shell.
export const SHELL_ACTIONS = [
  action("shell.skip", "button", "Skip to content", "Move keyboard focus past the navigation", "Focus moves to the page's main region; the address is unchanged",
    { owner: "engine: skipToContent", verify: existing(SPEC.deepLinks, "the skip link moves focus to the content and leaves the address alone") }),
  action("shell.nav-today", "link", "Today", "Go to today's ledger", "Address becomes #/ and the Today screen shows", { owner: "route table (Places.fs)", verify: "conformance" }),
  action("shell.nav-track", "link", "Track", "Go to the timer and manual entry", "Address becomes #/track and the Track screen shows", { owner: "route table (Places.fs)", verify: "conformance" }),
  action("shell.nav-month", "link", "Month", "Go to the monthly record", "Address becomes #/month and the Month screen shows", { owner: "route table (Places.fs)", verify: "conformance" }),
  action("shell.nav-more", "link", "More", "Go to reference data and session settings", "Address becomes #/more and the More screen shows", { owner: "route table (Places.fs)", verify: "conformance" }),
  action("shell.copy-link", "button", "Copy link", "Copy an absolute link to the current view", "\"Link copied.\" is shown and the clipboard holds the view's address",
    { owner: "engine: copyLink", verify: "conformance" }),
  action("shell.timer-chip", "button", "Running timer chip", "Return to the running timer", "Address becomes #/track",
    { owner: "engine: goTrack", state: "local, timer running, away from Track", pattern: /\d{2}:\d{2}:\d{2}$/, verify: "conformance" })
];

export const SURFACES = [
  {
    id: "app-today",
    path: "web/index.html#/ and #/day/{date}",
    kind: "product",
    routes: ["#/", "#/day/{date}"],
    actions: [
      action("today.previous-day", "button", "Previous day", "Show the day before", "Address becomes #/day/{the day before}", { owner: "engine: previousDay", verify: "conformance" }),
      action("today.next-day", "button", "Next day", "Show the day after", "Address becomes #/day/{the day after}", { owner: "engine: nextDay", verify: "conformance" }),
      action("today.today", "button", "Today", "Return to today from another day", "Address becomes #/ and the title is today's date", { owner: "engine: goToday", verify: "conformance" }),
      action("today.week", "link", "Week", "Show the week holding this day", "Address becomes #/week/{its Monday}", { owner: "route table", verify: "conformance" }),
      action("today.start-tracking", "button", "Start tracking", "Start recording on an empty day", "Address becomes #/track", { owner: "engine: goTrack", verify: "conformance" }),
      action("today.open-period", "link", "Open the period", "Open the timesheet period holding this day", "Address becomes #/periods/{its first day}", { owner: "route table", verify: "conformance" }),
      action("today.review", "button", "Review this day", "Review and attest this day", "Address becomes #/review/{day}", { owner: "engine: openReview", verify: "conformance" }),
      action("today.add-time", "button", "Add time", "Add time to the ledger", "Address becomes #/track", { owner: "engine: goTrack", verify: "conformance" }),
      action("today.record", "link", "Record title (e.g. First)", "Open an activity's detail", "Address becomes #/entries/{id} and its title is the heading",
        { owner: "route table", state: "local, a day with records", pattern: /^(First|Second)$/, verify: "conformance" }),
      action("today.merge-select", "checkbox", "Select to merge", "Choose activities to merge", "The merge form appears once two are chosen",
        { owner: "engine: mergeSelect", state: "local, a day with records", verify: existing(SPEC.lifecycle, "adjacent activities merge from Today; others are refused with the reason") }),
      action("today.merge", "button", "Merge", "Merge the selected adjacent activities", "One merged record replaces them; non-adjacent ones are refused with the reason",
        { owner: "engine: saveMerge", state: "local, two records selected", verify: existing(SPEC.lifecycle, "adjacent activities merge from Today; others are refused with the reason") }),
      action("today.obligation", "button", "Obligation action (e.g. Review again)", "Resolve what needs attention", "Opens the place the obligation names",
        { owner: "engine: resolveObligation", state: "local, an attested day changed later", verify: existing(SPEC.lifecycle, "a day is reviewed and attested; a later change is an obligation until reviewed again") })
    ],
    groups: [
      { id: "today-day-nav", relation: "top-edge", targets: [{ role: "button", name: "Previous day" }, { role: "button", name: "Next day" }, { role: "button", name: "Today" }],
        rationale: "The day navigation's related buttons (ULI-L10): aligned where they share a row, in source order where the row wraps (ULI-L09)." },
      { id: "today-day-nav-heights", relation: "bottom-edge", targets: [{ role: "button", name: "Previous day" }, { role: "button", name: "Next day" }, { role: "button", name: "Today" }],
        rationale: "Equivalent quiet buttons have one intrinsic height (ULI-L02, ULI-L07): with equal tops, equal bottoms." }
    ]
  },
  {
    id: "app-track",
    path: "web/index.html#/track",
    kind: "product",
    routes: ["#/track"],
    actions: [
      action("track.set-up", "button", "Set them up", "Add the reference data every record needs", "Address becomes #/more", { owner: "engine: goMore", verify: "conformance" }),
      action("track.start-timer", "button", "Start timer", "Start timing work", "Without a type and project the problems are listed; with them the timer runs (Pause shows)",
        { owner: "engine: startTimer", verify: "conformance" }),
      action("track.pause", "button", "Pause", "Pause the running timer", "The timer is paused and Resume replaces Pause",
        { owner: "engine: pauseTimer", state: "local, timer running", verify: "conformance" }),
      action("track.resume", "button", "Resume", "Resume a paused timer", "The timer runs and Pause replaces Resume",
        { owner: "engine: resumeTimer", state: "local, timer paused", verify: existing(SPEC.app, "the timer starts, ticks from timestamps, pauses, resumes and stops") }),
      action("track.stop", "button", "Stop activity", "Stop the timer and hold the time for completion", "The completion form holds the stopped time",
        { owner: "engine: stopTimer", state: "local, timer running or paused", verify: existing(SPEC.app, "the timer starts, ticks from timestamps, pauses, resumes and stops") }),
      action("track.save-completion", "button", "Save activity (completion)", "Record the stopped timer's time with its purpose", "The record is saved and announced",
        { owner: "engine: saveCompletion", state: "local, timer stopped", verify: existing(SPEC.app, "a stopped timer is held for completion and recorded with its business purpose") }),
      action("track.timer-recent", "button", "Recent work (timer)", "Fill the timer's type, project and description from recent work", "The timer form is filled",
        { owner: "engine: timerUseRecent", state: "local, with recent records", pattern: /^Research · HelixNote · /, verify: existing(SPEC.quick, "recent work fills the forms, a common duration sets a visible end, and an entry is copied as a draft") }),
      action("track.manual-recent", "button", "Recent work (manual)", "Fill the manual form from recent work", "The manual form is filled",
        { owner: "engine: manualUseRecent", state: "local, with recent records", pattern: /^Research · HelixNote · /, verify: existing(SPEC.quick, "recent work fills the forms, a common duration sets a visible end, and an entry is copied as a draft") }),
      action("track.duration", "button", "Duration (15m … 2h)", "Set the end from the start", "The end date and time show start plus the duration",
        { owner: "engine: manualDuration", state: "local, a start given", pattern: /^(15m|30m|45m|1h|1h 30m|2h)$/, verify: existing(SPEC.quick, "recent work fills the forms, a common duration sets a visible end, and an entry is copied as a draft") }),
      action("track.save-manual", "button", "Save activity", "Record time entered after the fact", "Every problem is listed beside the form; once corrected the record is saved (\"Saved …\" is announced)",
        { owner: "engine: saveManual", verify: "conformance" })
    ],
    groups: [
      { id: "track-timer-fields", relation: "top-edge", scope: { role: "region", name: "Ready when you are" }, targets: [{ role: "combobox", name: "Activity type" }, { role: "combobox", name: "Project" }],
        rationale: "The timer's two required choices share a row where the card is wide enough (ULI-L01); stacked in order otherwise (ULI-L09)." },
      { id: "track-manual-start", relation: "top-edge", scope: { role: "form", name: "Add time manually" }, targets: [{ role: "textbox", name: "Start date" }, { role: "textbox", name: "Start time" }],
        rationale: "A date and its time: side by side when each keeps its whole value, stacked in order otherwise (ULI-L01, ULI-L05, ULI-L09)." },
      { id: "track-manual-end", relation: "top-edge", scope: { role: "form", name: "Add time manually" }, targets: [{ role: "textbox", name: "End date" }, { role: "textbox", name: "End time" }],
        rationale: "As the start pair." },
      { id: "track-running-actions", relation: "top-edge", targets: [{ role: "button", name: "Pause" }, { role: "button", name: "Stop activity" }],
        rationale: "The running timer's two actions (ULI-L10): one row, equal tops, or stacked in order." }
    ]
  },
  {
    id: "app-activity",
    path: "web/index.html#/entries/{id}",
    kind: "product",
    routes: ["#/entries/{id}"],
    actions: [
      action("activity.back", "button", "← Back to today", "Return to today's ledger", "Address becomes #/", { owner: "engine: goToday", state: "local, a record open", verify: "conformance" }),
      action("activity.copy", "button", "Copy as a new entry", "Start a new entry from this one", "Address becomes #/track with the manual form filled as a draft",
        { owner: "engine: copyActivity", state: "local, a record open", verify: existing(SPEC.quick, "recent work fills the forms, a common duration sets a visible end, and an entry is copied as a draft") }),
      action("activity.amend", "button", "Save correction", "Correct the record", "Revision increments and the history lists Amended",
        { owner: "engine: saveAmend", state: "local, a record open", verify: existing(SPEC.lifecycle, "an activity is corrected, removed from totals and restored, with its history kept") }),
      action("activity.split", "button", "Split", "Split the record into two parts", "Two parts replace it; parts that do not add up are refused",
        { owner: "engine: saveSplit", state: "local, a record open", verify: existing(SPEC.lifecycle, "evidence is linked and checked, and a split assigns it to one part") }),
      action("activity.void", "button", "Remove from totals", "Void the record, keeping it", "The record is marked removed from totals; Restore appears",
        { owner: "engine: voidActivity", state: "local, a record open", verify: existing(SPEC.lifecycle, "an activity is corrected, removed from totals and restored, with its history kept") }),
      action("activity.restore", "button", "Restore to totals", "Undo a removal", "Announced as restored; the history lists Restored",
        { owner: "engine: restoreActivity", state: "local, a voided record open", verify: existing(SPEC.lifecycle, "an activity is corrected, removed from totals and restored, with its history kept") }),
      action("activity.attach", "button", "Attach evidence", "Link evidence to the record", "The evidence is listed; an invalid link or missing label is refused with the reason",
        { owner: "engine: attachEvidence", state: "local, a record open", verify: existing(SPEC.lifecycle, "evidence is linked and checked, and a split assigns it to one part") }),
      action("activity.unlink", "button", "Unlink", "Remove a piece of evidence", "The evidence is no longer listed",
        { owner: "engine: unlinkEvidence", state: "local, a record with evidence", verify: null })
    ],
    groups: []
  },
  {
    id: "app-review",
    path: "web/index.html#/review/{date}",
    kind: "product",
    routes: ["#/review", "#/review/{date}"],
    actions: [
      action("review.back", "button", "← Back to today", "Return to today's ledger", "Address becomes #/", { owner: "engine: goToday", verify: "conformance" }),
      action("review.attest", "button", "Attest to this day", "Attest the day's records", "Without a statement the problem is listed; with one the attestation is listed",
        { owner: "engine: attestDay", verify: "conformance" })
    ],
    groups: []
  },
  {
    id: "app-week",
    path: "web/index.html#/week/{date}",
    kind: "product",
    routes: ["#/week", "#/week/{date}"],
    actions: [
      action("week.previous", "button", "Previous week", "Show the week before", "Address becomes #/week/{Monday before}", { owner: "engine: previousWeek", verify: "conformance" }),
      action("week.next", "button", "Next week", "Show the week after", "Address becomes #/week/{Monday after}", { owner: "engine: nextWeek", verify: "conformance" }),
      action("week.day", "link", "Day of the week (e.g. Monday, October 12)", "Open that day", "Address becomes #/day/{date}",
        { owner: "route table", pattern: /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), [A-Z][a-z]+ \d{1,2}$/, verify: "conformance" })
    ],
    groups: [
      { id: "week-nav", relation: "top-edge", targets: [{ role: "button", name: "Previous week" }, { role: "button", name: "Next week" }], rationale: "Paired navigation (ULI-L10)." }
    ]
  },
  {
    id: "app-month",
    path: "web/index.html#/month/{yyyy-mm}",
    kind: "product",
    routes: ["#/month", "#/month/{period}"],
    actions: [
      action("month.previous", "button", "Previous month", "Show the month before", "Address becomes #/month/{month before}", { owner: "engine: previousMonth", verify: "conformance" }),
      action("month.next", "button", "Next month", "Show the month after", "Address becomes #/month/{month after}", { owner: "engine: nextMonth", verify: "conformance" }),
      action("month.report", "button", "Report on this month", "Open a report for the month", "Address becomes #/reports?from={first}&to={last}", { owner: "engine: reportMonth", verify: "conformance" }),
      action("month.history", "button", "Month with recorded time", "Show that month", "Address becomes #/month/{that month}",
        { owner: "engine: showMonth", state: "local, months with records", pattern: /^[A-Z][a-z]+ \d{4}$/, verify: null })
    ],
    groups: [
      { id: "month-nav", relation: "centerline", targets: [{ role: "button", name: "Previous month" }, { role: "button", name: "Next month" }], rationale: "Paired navigation either side of the month picker (ULI-L10)." }
    ]
  },
  {
    id: "app-period",
    path: "web/index.html#/periods/{date}",
    kind: "product",
    routes: ["#/periods", "#/periods/{date}"],
    actions: [
      action("period.previous", "button", "Previous period", "Show the period before", "Address becomes #/periods/{its first day}", { owner: "engine: previousPeriod", verify: "conformance" }),
      action("period.next", "button", "Next period", "Show the period after", "Address becomes #/periods/{its first day}", { owner: "engine: nextPeriod", verify: "conformance" }),
      action("period.day", "link", "Day of the period", "Open that day", "Address becomes #/day/{date}",
        { owner: "route table", pattern: /^(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday), [A-Z][a-z]+ \d{1,2}$/, verify: "conformance" }),
      action("period.submit", "button", "Submit this period", "Submit the period's time", "The period is held and Reopen is offered",
        { owner: "engine: submitPeriod", state: "local, a period with records", verify: "conformance" }),
      action("period.reopen", "button", "Reopen this period", "Reopen a held period with a reason", "Without a reason the problem is listed; with one the period is open again",
        { owner: "engine: reopenPeriod", state: "local, a submitted period", verify: "conformance" }),
      action("period.approve", "button", "Approve", "Approve a member's submitted period", "The submission is approved and leaves the list",
        { owner: "engine: approveSubmission", state: "GitHub, administrator with a submission", verify: existing(SPEC.github, "a member submits their period, the administrator approves it, and the member's period is closed") }),
      action("period.return", "button", "Return for correction", "Return a submission with a note", "The submission is returned to its member",
        { owner: "engine: rejectSubmission", state: "GitHub, administrator with a submission", verify: null })
    ],
    groups: [
      { id: "period-nav", relation: "top-edge", targets: [{ role: "button", name: "Previous period" }, { role: "button", name: "Next period" }], rationale: "Paired navigation (ULI-L10)." }
    ]
  },
  {
    id: "app-projects",
    path: "web/index.html#/projects and #/projects/{id}",
    kind: "product",
    routes: ["#/projects", "#/projects/{id}"],
    actions: [
      action("projects.project", "link", "Project name (e.g. HelixNote)", "Open the project", "Address becomes #/projects/{id}", { owner: "route table", state: "local, a project", pattern: /^HelixNote$/, verify: "conformance" }),
      action("project.all", "link", "← All projects", "Return to the list", "Address becomes #/projects", { owner: "route table", state: "local, a project open", verify: "conformance" }),
      action("project.record", "link", "Record on the project", "Open the record", "Address becomes #/entries/{id}", { owner: "route table", state: "local, a project with records", pattern: /^(First|Second)$/, verify: "conformance" }),
      action("project.today", "link", "Today on this project", "Today filtered to the project", "Address becomes #/day/{today}?project={id}", { owner: "route table", state: "local, a project open", verify: "conformance" }),
      action("project.week", "link", "This week", "This week filtered to the project", "Address becomes #/week/{Monday}?project={id}", { owner: "route table", state: "local, a project open", verify: "conformance" }),
      action("project.report", "link", "Report on this project", "A report filtered to the project", "Address becomes #/reports?project={id}", { owner: "route table", state: "local, a project open", verify: "conformance" })
    ],
    groups: []
  },
  {
    id: "app-candidates",
    path: "web/index.html#/candidates and #/candidates/{id}",
    kind: "product",
    routes: ["#/candidates", "#/candidates/{id}"],
    actions: [
      action("candidates.status", "link", "Status filter (Awaiting a decision, Decided, All)", "Filter candidates by status", "Address carries ?status= and that filter is current",
        { owner: "route table", pattern: /^(Awaiting a decision|Decided|All)$/, verify: "conformance" }),
      action("candidates.source", "link", "Source filter", "Filter candidates by source", "Address carries ?source=", { owner: "route table", state: "GitHub, candidates from sources", verify: null }),
      action("candidates.open", "link", "Candidate", "Open a candidate", "Address becomes #/candidates/{id}",
        { owner: "route table", state: "GitHub, a candidate", verify: existing(SPEC.github, "an observation a producer files becomes a candidate, and accepting it makes it the person's time") }),
      action("candidate.all", "link", "← All candidates", "Return to the list", "Address becomes #/candidates", { owner: "route table", state: "GitHub, a candidate open", verify: null }),
      action("candidate.link", "link", "Candidate's source link", "Open the observation at its source", "A valid external address (not followed)", { owner: "external", state: "GitHub, a candidate open", verify: null }),
      action("candidate.accept", "button", "Accept", "Make the candidate the person's time", "The candidate is decided and its time is recorded",
        { owner: "engine: acceptCandidate", state: "GitHub, a candidate open", verify: existing(SPEC.github, "an observation a producer files becomes a candidate, and accepting it makes it the person's time") }),
      action("candidate.reject", "button", "Reject", "Reject the candidate with a reason", "The candidate is decided as rejected", { owner: "engine: rejectCandidate", state: "GitHub, a candidate open", verify: null })
    ],
    groups: []
  },
  {
    id: "app-reports",
    path: "web/index.html#/reports",
    kind: "product",
    routes: ["#/reports"],
    actions: [
      action("reports.print", "button", "Print or save as PDF", "Print Folio's document of the report", "The browser's print dialog opens for the report document",
        { owner: "engine: printReport (chrona.print pack)", verify: existing(SPEC.reports, "printing opens the browser's dialog for Folio's document of the report") }),
      action("reports.copy-export", "button", "Copy export", "Copy the CSV or JSON export", "The clipboard holds the export and the copy is confirmed",
        { owner: "engine: copyExport", verify: existing(SPEC.reports, "the export is deterministic CSV or JSON and is copied to the clipboard") })
    ],
    groups: [
      { id: "reports-dates", relation: "top-edge", targets: [{ role: "textbox", name: "From" }, { role: "textbox", name: "To" }],
        rationale: "The report's date range shares a row where the grid gives two columns (ULI-L01)." }
    ]
  },
  {
    id: "app-more",
    path: "web/index.html#/more and #/more/{section}",
    kind: "product",
    routes: ["#/more", "#/more/{section}"],
    actions: [
      action("more.all-projects", "link", "See every project and your time on it", "Open the projects list", "Address becomes #/projects", { owner: "route table", verify: "conformance" }),
      action("more.add-project", "button", "Add project", "Add a project", "The project is listed (checked) under Projects", { owner: "engine: addProject", verify: "conformance" }),
      action("more.add-type", "button", "Add activity type", "Add an activity type", "The activity type is listed under Activity types", { owner: "engine: addActivityType", verify: "conformance" }),
      action("more.add-tag", "button", "Add tag", "Add a tag", "The tag is listed under Tags", { owner: "engine: addTag", verify: "conformance" }),
      action("more.reference-active", "checkbox", "Reference item (e.g. HelixNote)", "Stop or resume offering an item for new work", "Unchecked items stay listed but are no longer offered",
        { owner: "engine: referenceActive", state: "local, reference data", verify: existing(SPEC.app, "archived reference data stays listed but is no longer offered for new work") }),
      action("more.reports", "button", "Reports & export", "Open reports", "Address becomes #/reports", { owner: "engine: goReports", verify: "conformance" }),
      action("more.save-period", "button", "Save period settings", "Save the organization's timesheet period", "The period the Today screen shows follows the saved cadence",
        { owner: "engine: savePeriodSettings", verify: "conformance" }),
      action("more.sign-out", "button", "Sign out and clear the token", "Sign out", "The sign-in screen shows; the token is gone",
        { owner: "engine: signOut", state: "GitHub, signed in", verify: existing(SPEC.signIn, "a person signs in with GitHub and works as the identity GitHub resolved, then signs out") }),
      action("more.sign-out-send", "button", "Send them now, then sign out", "Sign out after sending unsent changes", "Unsent changes are sent, then the sign-in screen shows", { owner: "engine: signOutSend", state: "GitHub, unsent changes", verify: null }),
      action("more.sign-out-keep", "button", "Keep them on this device for this account", "Sign out keeping unsent changes", "Changes stay queued for this account", { owner: "engine: signOutKeep", state: "GitHub, unsent changes", verify: null }),
      action("more.sign-out-discard", "button", "Discard them", "Discard unsent changes, asking first", "A confirmation is asked", { owner: "engine: signOutDiscard", state: "GitHub, unsent changes", verify: null }),
      action("more.sign-out-cancel", "button", "Stay signed in", "Cancel signing out", "The choice closes; still signed in", { owner: "engine: signOutCancel", state: "GitHub, unsent changes", verify: null }),
      action("more.sign-out-discard-confirm", "button", "Discard and sign out", "Confirm discarding and sign out", "Changes are discarded and the sign-in screen shows", { owner: "engine: signOutDiscardConfirmed", state: "GitHub, unsent changes", verify: null }),
      action("more.sign-out-discard-cancel", "button", "Keep them and stay signed in", "Cancel the discard", "Still signed in, changes kept", { owner: "engine: signOutCancel", state: "GitHub, unsent changes", verify: null }),
      action("more.clear-device", "button", "Clear this device", "Remove what this device keeps, asking first", "A confirmation is asked",
        { owner: "engine: clearDevice", state: "GitHub, signed in", verify: existing(SPEC.githubOffline, "clearing this device removes what it keeps to open offline, then signs out") }),
      action("more.clear-device-confirmed", "button", "Clear and sign out", "Confirm clearing the device", "The read cache is removed and the person is signed out",
        { owner: "engine: clearDeviceConfirmed", state: "GitHub, signed in", verify: existing(SPEC.githubOffline, "clearing this device removes what it keeps to open offline, then signs out") }),
      action("more.clear-device-cancel", "button", "Keep them", "Cancel clearing the device", "Nothing is removed", { owner: "engine: clearDeviceCancel", state: "GitHub, signed in", verify: null }),
      action("more.redo", "button", "Redo the failed change", "Redo a change GitHub refused", "The change is made again", { owner: "engine: redoChange", state: "GitHub, a refused change", verify: null }),
      action("more.retry-change", "button", "Try again (change)", "Retry sending a change", "The change is sent again", { owner: "engine: retryChange", state: "GitHub, a failed change", verify: null }),
      action("more.keep-stored", "button", "Keep what is stored", "Keep the stored version over the local change", "The local change is dropped", { owner: "engine: keepStored", state: "GitHub, a conflict", verify: null }),
      action("more.accept-outside", "button", "Accept this version", "Accept a record edited outside Chrona", "The record counts again", { owner: "engine: acceptOutsideEdit", state: "GitHub, an outside edit", verify: null }),
      action("more.remove-member", "button", "Remove", "Remove a member", "The member is no longer listed", { owner: "engine: removeMember", state: "GitHub, administrator", verify: null }),
      action("more.admit", "button", "Add member", "Add a member", "The member is listed",
        { owner: "engine: admitMember", state: "GitHub, administrator", verify: existing(SPEC.github, "a member submits their period, the administrator approves it, and the member's period is closed") }),
      action("more.rebuild-index", "button", "Rebuild from the records", "Rebuild the activity index", "The index is rebuilt and reported", { owner: "engine: rebuildIndex", state: "GitHub, administrator", verify: null }),
      action("more.inbox-candidates", "link", "See the candidates", "Open the candidates", "Address becomes #/candidates", { owner: "route table", state: "GitHub, inboxes", verify: null }),
      action("more.read-inboxes", "button", "Read the inboxes now", "Read producers' inboxes", "New observations become candidates",
        { owner: "engine: readInboxes", state: "GitHub, inboxes", verify: existing(SPEC.github, "an observation a producer files becomes a candidate, and accepting it makes it the person's time") })
    ],
    groups: [
      { id: "more-add-project", relation: "bottom-edge", targets: [{ role: "textbox", name: "New project" }, { role: "button", name: "Add project" }],
        rationale: "A text field and its submit button share a bottom edge (the composition aligns them flex-end) or wrap in order (ULI-L01, ULI-L10)." },
      { id: "more-add-type", relation: "bottom-edge", targets: [{ role: "textbox", name: "New activity type" }, { role: "button", name: "Add activity type" }], rationale: "As above." },
      { id: "more-add-tag", relation: "bottom-edge", targets: [{ role: "textbox", name: "New tag" }, { role: "button", name: "Add tag" }], rationale: "As above." }
    ]
  },
  {
    id: "app-not-found",
    path: "web/index.html#/{unknown}",
    kind: "product",
    routes: ["#/{*rest}"],
    actions: [action("problem.home", "link", "Go to today", "Leave a page that names nothing", "Address becomes #/ and Today shows", { owner: "route table", verify: "conformance" })],
    groups: []
  },
  {
    id: "app-gates",
    path: "web/index.html (sign-in, opening, store failure, administrator and membership gates; shell notices)",
    kind: "product",
    routes: ["#/sign-in"],
    actions: [
      action("gate.sign-in", "button", "Sign in with GitHub", "Start GitHub sign-in", "GitHub's authorize page is reached and the person returns signed in",
        { owner: "engine: signIn", state: "deployment with sign-in", verify: existing(SPEC.signIn, "a person signs in with GitHub and works as the identity GitHub resolved, then signs out") }),
      action("gate.retention", "radio", "Keep me signed in choice", "Choose how long the token is kept", "The choice is checked and governs storage",
        { owner: "engine: signInRetention", state: "deployment with sign-in", verify: existing(SPEC.signIn, "session-only retention keeps the token in the page, never in storage") }),
      action("gate.retry-store", "button", "Try again", "Retry opening the records", "GitHub is asked again; while it cannot be reached the failure stays on screen (no false success)",
        { owner: "engine: retryStore", state: "GitHub out of reach", verify: existing(SPEC.a11y, "the problem, and trying again, are on the screen") }),
      action("gate.offline-pause", "button", "Pause (offline)", "Pause the device's timer while records cannot open", "The timer pauses", { owner: "engine: pauseTimer", state: "GitHub out of reach, timer running", verify: null }),
      action("gate.offline-resume", "button", "Resume (offline)", "Resume the device's timer", "The timer runs", { owner: "engine: resumeTimer", state: "GitHub out of reach, timer paused", verify: null }),
      action("gate.offline-stop", "button", "Stop and hold the time", "Stop and hold the time until records open", "The stopped time is held", { owner: "engine: stopTimer", state: "GitHub out of reach, timer on", verify: null }),
      action("gate.confirm-admin", "button", "Confirm me as administrator", "Become the organization's administrator", "The organization opens with administrator rights", { owner: "engine: confirmAdministrator", state: "GitHub, new organization", verify: null }),
      action("gate.sign-out", "button", "Sign out", "Sign out from a gate", "The sign-in screen shows", { owner: "engine: signOut", state: "GitHub, not a member or confirming", verify: null }),
      action("shell.reload", "button", "Reload now", "Reload into a newer Chrona", "The page reloads safely",
        { owner: "engine: reloadShell", state: "a newer build published", verify: existing(SPEC.reliability, "a hidden page that comes back keeps its timer, and a newer Chrona is offered as a safe reload") }),
      action("shell.take-over", "button", "Use this tab instead", "Take unsent changes over from another tab", "This tab holds and sends them; the other stops",
        { owner: "engine: takeOverQueue", state: "GitHub, two tabs", verify: existing(SPEC.githubOffline, "\"use this tab instead\" takes the unsent changes over at once; the other tab says so, and every change is sent once") }),
      action("shell.send-earlier", "button", "They are mine: send them", "Send unsent changes from an earlier version", "They are sent", { owner: "engine: sendEarlier", state: "GitHub, earlier unsent changes", verify: null }),
      action("shell.keep-earlier", "button", "Keep them (earlier)", "Keep earlier unsent changes", "They are kept", { owner: "engine: keepEarlier", state: "GitHub, earlier unsent changes", verify: null }),
      action("shell.discard-earlier", "button", "Discard them (earlier)", "Discard earlier unsent changes, asking first", "A confirmation is asked", { owner: "engine: discardEarlier", state: "GitHub, earlier unsent changes", verify: null }),
      action("shell.discard-earlier-confirmed", "button", "Discard", "Confirm discarding", "They are discarded", { owner: "engine: discardEarlierConfirmed", state: "GitHub, earlier unsent changes", verify: null }),
      action("shell.discard-earlier-cancel", "button", "Cancel", "Cancel discarding", "They are kept", { owner: "engine: discardEarlierCancel", state: "GitHub, earlier unsent changes", verify: null })
    ],
    groups: []
  },
  {
    id: "kernel-slice",
    path: "web/kernel-slice.html",
    kind: "other",
    routes: [],
    actions: [
      action("slice.save", "button", "Save", "Save a label through the engine and Storage", "The label is saved and shown",
        { owner: "engine (Chrona.Engine slice): save", state: "slice page", verify: existing(SPEC.slice, "the bridge vocabulary, a Storage round trip and reload, end to end") }),
      action("slice.load", "button", "Load", "Load the saved label", "The saved label is shown",
        { owner: "engine (Chrona.Engine slice): load", state: "slice page", verify: existing(SPEC.slice, "the bridge vocabulary, a Storage round trip and reload, end to end") })
    ],
    groups: []
  }
];

// The shell's actions belong to every app surface; the evidence lists them
// once, under their own surface.
export const SHELL_SURFACE = {
  id: "app-shell",
  path: "web/index.html (sidebar, tab bar, top bar on every screen)",
  kind: "product",
  routes: ["*"],
  actions: SHELL_ACTIONS,
  groups: [
    { id: "topbar-actions", relation: "centerline", targets: [{ role: "button", name: "Copy link" }, { role: "img", name: "Local session" }],
      rationale: "The top bar's action and the identity mark share a centre line (ULI-L07, ULI-L10)." }
  ]
};

export const ALL_SURFACES = [SHELL_SURFACE, ...SURFACES];

export const CONTRACT_WIDTHS = [320, 390, 768, 1280];

// The declared action an inventoried control belongs to, among `surfaces`.
const matches = (declared, { role, name }) =>
  declared.control.role === role && (declared.pattern ? declared.pattern.test(name) : declared.control.name === name);

export const declaredActionFor = (surfaces, control) =>
  surfaces.flatMap((surface) => surface.actions).find((declared) => matches(declared, control)) ?? null;

export const actionById = (id) => ALL_SURFACES.flatMap((surface) => surface.actions).find((declared) => declared.id === id);
export const groupById = (id) => ALL_SURFACES.flatMap((surface) => surface.groups).find((group) => group.id === id);
