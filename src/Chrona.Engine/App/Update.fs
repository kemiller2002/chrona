/// Every state change of the Chrona application: `update ctx msg model`
/// returns the next model and the effects it wants, as data. Pure: the
/// current instant and fresh ids arrive in `Ctx`; nothing here reads a clock,
/// touches the browser or stores anything. Every rule is the domain's
/// (`Chrona.Domain`); this module only routes intent to it.
///
/// This module holds what passes between the engine and the edge: `Ctx`,
/// `Msg` and `Effect`. The rules that answer each message are in the
/// Update* modules beside it, and `UpdateDispatch.update` runs them.
module Chrona.Engine.App.Update

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.Model

/// What the edge supplies with each message.
[<NoComparison; NoEquality>]
type Ctx =
    { Now: DateTimeOffset
      /// A fresh, unique id with the given prefix, e.g. `NewId "ACT"`.
      NewId: string -> string }

/// Why text goes to the clipboard.
type CopyPurpose =
    /// A report's export.
    | ExportCopy
    /// A link to the current view (CHX-460).
    | LinkCopy

type Msg =
    /// The page started: its own address, the fragment it was opened at, and
    /// the query it was opened with (the provider's sign-in callback carries
    /// `code` and `state` there).
    | Started of page: PageAddress * fragment: string * query: (string * string) list
    /// The return target this tab kept across a sign-in round trip, if any.
    | ReturnRead of target: string option
    /// The deployment's configuration document, or None when it could not be read.
    | ConfigurationRead of text: string option
    | IdentityChanged of IdentityChange
    /// The organization's records, read from the store.
    | StoreOpened of StoreContents
    /// The organization's records could not be read, and why.
    | StoreUnavailable of reason: string
    /// The organization has no listed administrator: why, and whether this
    /// person may confirm themselves as one.
    | StoreNeedsConfirmation of reason: string * canConfirm: bool
    | EnvironmentDescribed of timeZone: string
    | EnvironmentUnavailable
    /// The browser moved to another address (Back, Forward, a link).
    | LocationMoved of fragment: string
    | Ticked of generation: int
    | StoreAnswered of commitId: string * StoreOutcome
    /// Changes queued in this browser before the page opened, still unsent,
    /// sent now in order (WI-0033). Each is answered like any other.
    | StoreResumed of StoreRequest list
    /// Where the unsent changes stand now.
    | SyncChanged of SyncState
    /// The activity index was rebuilt from the records, or could not be: what
    /// happened, for the person.
    | IndexRebuilt of summary: string
    /// A month asked for could not be read, and why.
    | StoreReadFailed of reason: string
    /// This account's unsent changes were discarded from this device: how many.
    | UnsentDiscarded of count: int
    /// Another tab took this browser's unsent changes over ("use this tab
    /// instead" there): these, kept before it did, are now that tab's to
    /// send (WI-0059, LCP-060).
    | UnsentHandedOver of commitIds: string list
    /// Where reading producers' inboxes stands (WI-0038).
    | InboxReconciled of InboxStatus
    /// Other organizations read for a report across organizations (WI-0075).
    | ElsewhereRead of Elsewhere list
    /// Chrona's data was cleared from this browser, or why it was not.
    | DeviceCleared of Result<unit, string>
    /// The device's kept timer under this key, if any (WI-0055).
    | TimerLoaded of key: string * value: string option
    /// The records could not be opened; this many of this account's changes
    /// wait in this browser to be sent.
    | UnsentWaiting of count: int * notice: string option
    /// The build this page runs (WI-0063).
    | BuildKnown of build: string
    /// The page is back after being hidden, frozen or restored from the
    /// back/forward cache: it catches up.
    | PageReturned
    /// The browser says it has, or has no, network.
    | ConnectionChanged of online: bool
    /// The build the deployment serves now, if it could be read.
    | ShellChecked of build: string option
    /// The activity index changed with a commit: each person's months, and
    /// what it covers.
    | IndexChanged of history: ActivityIndex.MonthTotal list * summary: string
    /// Whether the browser took the text onto its clipboard.
    | Copied of purpose: CopyPurpose * succeeded: bool
    /// A page event: its name, the enclosing item's key, the control's value
    /// and, for a checkbox, whether it is checked.
    | Ui of name: string * key: string option * value: string * isChecked: bool option

/// What signing out did with this account's unsent work (WI-0058). What the
/// device keeps of the account follows it (WI-0057, LCP-070, LCP-086).
type UnsentChoice =
    | NothingUnsent
    | SentUnsent
    | KeptUnsent
    | DiscardedUnsent

/// What the engine asks the edge to do.
type Effect =
    /// Move the browser's address: a push for going somewhere, a replace for
    /// refining the view or correcting an address to its canonical form.
    | Navigate of Limen.Routing.NavigationEffect
    /// Keep (or, None, forget) the address to return to after sign-in in this
    /// tab's session storage: GitHub's callback carries no fragment.
    | KeepReturn of target: string option
    /// Read the return target this tab kept, after a sign-in callback.
    | ReadReturn
    /// Wake the engine after `afterMs` milliseconds, for this timer run.
    | Wake of generation: int * afterMs: int
    | DescribeEnvironment
    | Store of StoreRequest
    /// Put text on the clipboard.
    | CopyText of purpose: CopyPurpose * text: string
    /// Open the browser's print dialog for the page's printable document.
    | Print
    /// Read the deployment's configuration document.
    | ReadConfiguration
    /// Set up sign-in from the deployment's configuration: complete the
    /// provider's callback when the page was opened with one, otherwise
    /// restore a session kept in this tab.
    | BeginIdentity of Deployment.IdentityConfig * callback: (string * string) list
    /// Send the person to the provider to sign in.
    | SignIn of Retention
    /// Clear every token this tab holds and revoke it at the provider.
    | SignOut
    /// The account leaves this device: what the device keeps of it (its
    /// read cache) goes, unless its unsent work was kept here, under the
    /// deployment's shared-device policy (WI-0057). `unsent`: how much work
    /// was unsent when the person chose.
    | LeaveDevice of UnsentChoice * unsent: int
    /// Clear Chrona's data from this browser, for every account: the queue
    /// and the read cache (LCP-070, LCP-086).
    | ClearDevice
    /// Open the organization's records at the deployment's location, as this
    /// session, reading what these dates need.
    | OpenStore of Deployment.DeploymentConfig * Session * dates: DateOnly list
    /// Make this listed person the organization's administrator, then open it.
    | ConfirmAdministrator
    /// Read the month folders these dates need (a month not read yet).
    | ReadMonths of dates: DateOnly list
    /// Read other people's months, for a report across people (WI-0039).
    | ReadOthers of wanted: (string * DateOnly) list
    /// Read other organizations for a report across organizations
    /// (WI-0075): each organization, whether everyone's time is wanted, and
    /// the months (their first days).
    | ReadElsewhere of wanted: (string * bool * DateOnly list) list
    /// Rebuild the activity index from the stored records (40).
    | RebuildIndex
    /// Read the producers' inboxes now (WI-0038).
    | ReadInboxes
    /// Send this account's unsent changes now.
    | SendUnsent
    /// Move focus to the control with this id: the control the person used
    /// is gone from the page, and focus must not fall to the document (35).
    | FocusControl of id: string
    /// Take this browser's unsent changes over from the tab holding them
    /// ("use this tab instead"; WI-0067, WI-0059).
    | TakeOverQueue
    /// Send the unsent changes from an earlier version as this person's.
    | SendEarlier
    /// Discard the unsent changes from an earlier version.
    | DiscardEarlier
    /// Ask again to hold this browser's unsent changes: the tab holding them
    /// may have closed (WI-0059).
    | ClaimQueue
    /// Discard this account's unsent changes from this device.
    | DiscardUnsent
    /// Read the device's kept timer for this person (WI-0055).
    | LoadTimer of key: string
    /// Keep the device's timer for this person, or clear it (None).
    | SaveTimer of key: string * value: string option
    /// Ask which Chrona build the deployment serves now (WI-0063).
    | CheckShell
    /// Load the page again, to run the newer Chrona.
    | ReloadPage

/// The timer display refreshes once a second while running (the label is
/// whole seconds); the engine computes it, never a client-side counter.
[<Literal>]
let TickMs = 1000

/// The device a timer was started on. One browser is one device until
/// sign-in gives devices identities (10.5).
[<Literal>]
let ThisDevice = "this-browser"

/// Every `data-event` name the page may send; anything else is a defect.
let eventNames =
    [ "signIn"; "signInRetention"; "signOut"; "retryStore"; "chooseOrganization"; "confirmAdministrator"; "reloadShell"
      "signOutSend"; "signOutKeep"; "signOutDiscard"; "signOutDiscardConfirmed"; "signOutCancel"; "takeOverQueue"
      "sendEarlier"; "keepEarlier"; "discardEarlier"; "discardEarlierConfirmed"; "discardEarlierCancel"
      "clearDevice"; "clearDeviceConfirmed"; "clearDeviceCancel"
      "memberId"; "memberName"; "memberAccess"; "admitMember"; "changeMemberAccess"; "removeMember"
      "keepStored"; "retryChange"; "redoChange"; "acceptOutsideEdit"; "rebuildIndex"
      "goToday"; "goTrack"; "goMore"; "showDate"; "previousDay"; "nextDay"
      "timerActivityType"; "timerProject"; "timerDescription"; "startTimer"; "pauseTimer"; "resumeTimer"; "stopTimer"
      "completeActivityType"; "completeProject"; "completeDescription"; "completePurpose"; "completeTag"; "confirmLongTimer"; "saveCompletion"
      "manualActivityType"; "manualProject"; "manualStartDate"; "manualStartTime"; "manualEndDate"; "manualEndTime"
      "manualDescription"; "manualPurpose"; "manualReason"; "manualTag"; "saveManual"
      "newProjectName"; "newActivityTypeName"; "newTagName"; "addProject"; "addActivityType"; "addTag"; "referenceActive"
      "openReview"; "previousMonth"; "nextMonth"; "showMonth"
      "timerUseRecent"; "manualUseRecent"; "manualDuration"; "copyActivity"
      "amendActivityType"; "amendProject"; "amendDescription"; "amendPurpose"; "amendReason"; "saveAmend"
      "voidReason"; "voidActivity"; "restoreActivity"
      "splitFirst"; "splitSecond"; "splitEvidence"; "saveSplit"
      "evidenceKind"; "evidenceUrl"; "evidenceLabel"; "evidenceSource"; "evidenceNotes"; "attachEvidence"; "unlinkEvidence"
      "mergeSelect"; "mergeActivityType"; "mergeProject"; "mergeDescription"; "mergePurpose"; "saveMerge"
      "attestStatement"; "attestDay"; "resolveObligation"; "periodCadence"; "periodWeekStart"
      "periodSubmissionExpected"; "periodApprovalRequired"; "savePeriodSettings"; "submitPeriod"; "reopenReason"; "reopenPeriod"
      "reviewNote"; "approveSubmission"; "rejectSubmission"
      "candidateActivityType"; "candidateProject"; "candidateDescription"; "candidatePurpose"; "candidateReason"
      "acceptCandidate"; "rejectCandidate"; "readInboxes"
      "reportFrom"; "reportTo"; "reportProject"; "reportActivityType"; "reportTag"; "reportMethod"; "reportBillability"; "reportText"
      "reportIncludeRemoved"; "reportPeople"; "reportPerson"; "reportOrganizations"; "reportGrouping"; "reportFormat"; "copyExport"; "printReport"; "reportMonth"; "goReports"
      "copyLink"; "skipToContent"; "dayProject"; "previousWeek"; "nextWeek"; "weekProject"; "previousPeriod"; "nextPeriod" ]
