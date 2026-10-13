# Production completion gate (section 44)

Where Chrona demonstrates each item of the production completion gate
(`docs/requirements/CHRONA-REQUIREMENTS-EXPANSION.txt`, section 44; CHX-440,
WI-0040). Each item lists the tests, workflows or decisions that show it. A
test is named as `File: name`. `tests/Chrona.Tests/ProductionGateTests.fs`
checks that every item of section 44 is here, that every named test, file
and decision exists, and that every item not yet demonstrated names an open
work item.

**The gate is not yet met.** Three items wait on open work: GitHub identity
on a real deployment (WI-0052), the iPhone VoiceOver pass (WI-0068) and
SDE/Ordo validation (WI-0079).

| Item | Status | Evidence |
|---|---|---|
| correct manual entry | demonstrated | `TimeDomainTests.fs: scenario 1: a manual entry records exact minutes and its local context`; `app.spec.js: manual entry reports every problem, then records exact time on Today` |
| correct timer behavior | demonstrated | `TimerTests.fs: scenario 2: a timer records exact minutes as an interval`; `TimerTests.fs: scenario 3: paused time is not worked time, and each worked run is its own interval`; `TimerTests.fs: scenario 5: a timer across midnight is split at the business-day boundary`; `app.spec.js: the timer starts, ticks from timestamps, pauses, resumes and stops` |
| persistent/recoverable active timer | demonstrated | `TimerTests.fs: scenario 4: a persisted timer is recovered explicitly with its elapsed time`; `StoreSignOutTests.fs: a running timer survives a refresh, and is recovered with its elapsed time from its timestamps`; `app.spec.js: a running timer survives a reload and is recovered from its timestamps` |
| overlap protection | demonstrated | `TimeDomainTests.fs: scenario 8: overlap is rejected within an actor and organization only`; `TimerTests.fs: scenario 9: overlapping timers from two devices are a reconciliation obligation` |
| revision-safe amendments | demonstrated | `LedgerTests.fs: scenario 10 and 32: a command against a stale revision is a conflict, never last-write-wins`; `StoreOpeningTests.fs: an edit made elsewhere first makes this device's stale edit a conflict, never an overwrite` |
| void/restore/split/merge correctness | demonstrated | `LedgerTests.fs: scenario 11: void keeps the record and frees the time; restore rechecks overlap`; `LedgerTests.fs: scenarios 12 and 14: split preserves total time, lineage and assigns evidence once`; `LedgerTests.fs: scenarios 13 and 14: merge keeps every source id and supersedes, never deletes` |
| audit provenance | demonstrated | `StoreDerivedTests.fs: who changed what is stored with the records, once, beside the activity it concerns`; `StoreDerivedTests.fs: accepting an outside edit is audited, and revisions made before the trail was stored say so` |
| offline usability | demonstrated | `StoreOfflineQueueTests.fs: scenario 33: time recorded offline waits in this browser, is shown as unsent, and is sent in order on reconnect`; `app-github.spec.js: with GitHub out of reach, a new tab opens the records from this browser's read cache, and a change made there is sent once GitHub is back` |
| deterministic GitHub synchronization | demonstrated | `StoreOpeningTests.fs: scenario 34: independent changes from two devices both land, and each device is shown the other's`; `StoreDerivedTests.fs: the activity index is kept with every change, exactly as a rebuild from the records makes it` |
| explicit unknown outcomes | demonstrated | `StoreOpeningTests.fs: scenario 31: a commit whose outcome GitHub did not report is reconciled, not sent again`; `StoreOfflineQueueTests.fs: a change whose answer was lost with the connection is reconciled before anything is sent again` |
| semantic conflict handling | demonstrated | `StoreOpeningTests.fs: time that overlaps what another device stored first is a conflict decided on what is stored`; `StoreOfflineQueueTests.fs: an edit queued offline that another device changed first becomes a conflict on reconnect, neither side dropped` |
| organization separation | demonstrated | `StoreOrganizationTests.fs: a person works in one of the deployment's organizations at a time, each in its own folder`; `StoreOrganizationTests.fs: only members work in an organization; an administrator adds people, and the roster is shared across devices` |
| GitHub identity handling | not yet: WI-0052 | `SignInTests.fs: a person signs in with GitHub, and Chrona works as the identity GitHub resolved`; `app-sign-in.spec.js: a person signs in with GitHub and works as the identity GitHub resolved, then signs out`; `app-sign-in.spec.js: session-only retention keeps the token in the page, never in storage`. Against Fides and GitHub fakes; the check on a real deployment is WI-0052 |
| optional approval flow | demonstrated | `ReviewBillingTests.fs: scenarios 18 and 19: submission covers exact revisions and approval records the approver`; `ReviewBillingTests.fs: scenarios 20 and 21: rejection and reopening are explicit transitions`; `app-github.spec.js: a member submits their period, the administrator approves it, and the member's period is closed` |
| billability separation | demonstrated | `ReviewBillingTests.fs: scenario 23: only approved, billable, recorded time passes the publication gates`; `ReviewBillingTests.fs: scenario 24: billing rounds a projection and never the exact time` |
| clean Summa publication contract | demonstrated | `ReviewBillingTests.fs: scenario 35: approved billable time publishes with its policy identity`; `ReviewBillingTests.fs: scenario 36: retrying a publication is idempotent; republishing is refused`; `tests/Chrona.Tests/SummaBillingTests.fs` (Summa's own contract through Conditor; its transport is WI-0061) |
| correction propagation | demonstrated | `LedgerTests.fs: scenarios 37 and 38: changing published or invoiced time creates a correction obligation`; `ReviewBillingTests.fs: scenario 22: an amendment after approval makes the approval stale, never silently valid` |
| legacy migration or explicit retirement decision | demonstrated | `research/decisions/DF-CHRONA-2026-0002--reconstruct-not-migrate-legacy-reconciliation-and-data-status.md`; `CompatibilityTests.fs: scenario 39: legacy record formats match by exact version, and unknown formats are refused (reconstruction, not migration: DF-CHRONA-2026-0002)` |
| accessibility verification | not yet: WI-0068 | `app-accessibility.spec.js: scenario 43: the timer, from the keyboard alone, keeps focus on the control that took the pressed one's place`; `app-accessibility.spec.js: scenario 44: a screen reader hears each timer transition once, and never the ticks`; `app.spec.js: keyboard only: skip link, navigation and a manual entry`. Automated; the iPhone VoiceOver pass by a person is WI-0068 |
| mobile core workflow | demonstrated | `app-accessibility.spec.js: scenario 42: on a phone`; `app.spec.js: the sidebar gives way to a bottom tab bar, with no sideways scrolling` |
| derived-state rebuild | demonstrated | `StoreDerivedTests.fs: scenario 40: every projection is rebuilt from the stored records alone`; `StoreDerivedTests.fs: a record changed outside Chrona shows where the index is out of date, and a rebuild repairs it` |
| ROS validation | demonstrated | `.github/workflows/praxis-validation.yml` (`praxis registry check` and `praxis validate` on every push and pull request) |
| SDE/Ordo validation | not yet: WI-0079 | Chrona's states, legal transitions, refusals, revision checks and obligations are modeled and tested in `src/Chrona.Domain`; no Ordo model of them is validated yet (WI-0079) |
| Limen architecture verification | demonstrated | `.github/workflows/limen-verify.yml` (`limen verify --strict` with the recorded Limen version); `ArchitectureTests.fs: the engine depends on the domain alone` |
