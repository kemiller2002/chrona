/// What the page holds while a person works: why an address shows nothing,
/// the drafts of its forms, and where sign-in and signing out stand.
module Chrona.Engine.App.ModelForms

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain
open Chrona.Engine.App.ModelPorts

/// Why the address shows no place (CHX-460): a clear page, never a blank one
/// or another place.
type RouteProblem =
    /// The address names nothing in Chrona, names it wrongly, or names a
    /// place this person may not see (Limen's route outcome).
    | AddressProblem of Limen.Routing.RouteError
    /// The address names a record that does not exist, by its kind and id.
    | RecordMissing of kind: string * id: string

/// What a form says about classification, as typed (strings, unvalidated).
type ClassificationDraft =
    { ActivityTypeId: string
      ProjectId: string
      Description: string
      BusinessPurpose: string
      Tags: string list }

let emptyClassification =
    { ActivityTypeId = ""
      ProjectId = ""
      Description = ""
      BusinessPurpose = ""
      Tags = [] }

type ManualDraft =
    { Classification: ClassificationDraft
      StartDate: string
      StartTime: string
      EndDate: string
      EndTime: string
      Reason: string }

let emptyManual =
    { Classification = emptyClassification
      StartDate = ""
      StartTime = ""
      EndDate = ""
      EndTime = ""
      Reason = "" }

/// The activity detail screen's drafts, opened at the revision the person
/// saw: every command they send names that revision, so a change made
/// elsewhere in between is a conflict, never overwritten (21).
type Detail =
    { ActivityId: string
      Revision: int
      Amend: ClassificationDraft
      AmendReason: string
      VoidReason: string
      SplitFirst: string
      SplitSecond: string
      /// Which split part (1 or 2) carries each evidence id; 0 is neither.
      SplitEvidence: Map<string, int>
      EvidenceKind: string
      EvidenceUrl: string
      EvidenceLabel: string
      EvidenceSource: string
      EvidenceNotes: string }

/// The report form, as typed.
type ReportDraft =
    { From: string
      To: string
      ProjectId: string
      ActivityTypeId: string
      Tag: string
      /// "mine" or "everyone" (WI-0039).
      People: string
      /// One person's actor id among everyone's, or "".
      Person: string
      /// "this" or "all" organizations (WI-0075).
      Organizations: string
      /// "", "manual" or "timer".
      Method: string
      /// "", "billable", "non-billable" or "pending".
      Billability: string
      Text: string
      IncludeRemoved: bool
      /// "project", "activityType", "tag", "day" or "person".
      Grouping: string
      /// "csv" or "json".
      Format: string
      /// When the export shown was generated: set when the report is opened
      /// or changed, so the text copied is the text the person saw.
      GeneratedAt: DateTimeOffset option }

let emptyReport =
    { From = ""
      To = ""
      ProjectId = ""
      ActivityTypeId = ""
      Tag = ""
      People = "mine"
      Person = ""
      Organizations = "this"
      Method = ""
      Billability = ""
      Text = ""
      IncludeRemoved = false
      Grouping = "project"
      Format = "csv"
      GeneratedAt = None }

/// Where a signed-in session's tokens are kept (CHX-023). Session-only
/// retention is the default; neither survives closing the tab.
type Retention =
    /// In memory: gone when the page reloads or closes.
    | ThisPage
    /// In this tab's session storage: gone when the tab closes.
    | ThisTab

/// Where this tab is with sign-in (CHX-022).
type IdentityMode =
    /// Reading the deployment's configuration.
    | Configuring
    /// The deployment configures no sign-in: one person, in this tab.
    | LocalOnly
    /// The deployment's configuration cannot be used; nothing else can run.
    | Misconfigured of detail: string
    /// Sign-in is configured and no one is signed in. `Busy` while a sign-in
    /// or a callback is under way.
    | SignInRequired of busy: bool
    /// Someone signed in: `Model.Session` is their identity, resolved by the
    /// provider, never typed.
    | SignedInMode

/// Signing out while this account's changes have not reached GitHub
/// (WI-0058): nothing is lost silently, and nothing is left behind on the
/// device unknowingly.
type SignOutStep =
    /// The person chooses: send them now, keep them on this device for this
    /// account (when the deployment allows), or discard them.
    | ChoosingUnsent
    /// Sending them; signed out once every one is stored.
    | SendingUnsent
    /// Confirming that they are discarded.
    | ConfirmingDiscard
    /// Discarding them; signed out once they are gone.
    | DiscardingUnsent

/// Clearing Chrona's data from this browser ("Clear this device"; WI-0057,
/// Limen LCP-070, LCP-086): the unsent-changes queue and the read cache,
/// for every account, then signing out.
type DeviceClearStep =
    /// The person confirms; nothing is cleared yet.
    | ConfirmingClear
    /// Clearing; signed out once it is done.
    | Clearing

type IdentityState =
    { Mode: IdentityMode
      /// Signing out, while changes are unsent.
      SignOut: SignOutStep option
      /// Clearing this device.
      DeviceClear: DeviceClearStep option
      /// Why the device could not be cleared, for the person.
      DeviceNote: string option
      /// Why sending them did not finish, for the person.
      SignOutNote: string option
      Retention: Retention
      /// The code of the last sign-in outcome to tell the person about, for
      /// example `state_expired` or `signed_out`.
      Notice: string option
      /// The provider's callback parameters this page was opened with, until
      /// the configuration is read and the sign-in can be completed.
      Callback: (string * string) list
      /// The address to return to after sign-in, kept in this tab across the
      /// round trip to GitHub (its callback carries no fragment), until it
      /// is resumed (CHX-460).
      ReturnTo: string option
      /// A callback page is reading the kept return target; nothing is
      /// resumed until it is read.
      ReadingReturn: bool }

/// What the edge reports about sign-in.
type IdentityChange =
    | SignedInAs of Session
    | SigningIn
    /// No one is signed in now, and why (a callback failure, a sign-out, an
    /// expired or revoked session), when there is something to say.
    | SignedOutWith of notice: string option
    /// Signed in, but the provider or exchange could not be reached; the
    /// session is kept and the next attempt retries.
    | ProviderUnavailable

let initialIdentity =
    { Mode = Configuring
      SignOut = None
      DeviceClear = None
      DeviceNote = None
      SignOutNote = None
      Retention = ThisPage
      Notice = None
      Callback = []
      ReturnTo = None
      ReadingReturn = false }

/// A new member, as typed: their GitHub account's numeric id (shown to them
/// when they are not yet a member), a name, and what they may do.
type MemberDraft =
    { Id: string
      Name: string
      /// "ownTime", "reviewer" or "administrator".
      Access: string }

let emptyMember = { Id = ""; Name = ""; Access = "ownTime" }
