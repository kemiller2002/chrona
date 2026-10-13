/// Sign-in (CHX-022, CHX-023), the person's access and the organization's
/// members.
module Chrona.Engine.App.ProjectIdentity

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords
open Chrona.Engine.App.ProjectShell
open Chrona.Engine.App.ProjectToday
open Chrona.Engine.App.ProjectTrack
open Chrona.Engine.App.ProjectMore
open Chrona.Engine.App.ProjectConflicts
open Chrona.Engine.App.ProjectCandidates
open Chrona.Engine.App.ProjectObligations
open Chrona.Engine.App.ProjectDetail
open Chrona.Engine.App.ProjectReview
open Chrona.Engine.App.ProjectReports
open Chrona.Engine.App.ProjectWeeks
open Chrona.Engine.App.ProjectAddresses

/// What a sign-in outcome means to the person, by its stable code.
let noticeText (code: string) =
    match code with
    | "signed_out" -> "You signed out. This tab no longer holds your GitHub token."
    | "state_invalid" -> "That sign-in did not start in this tab, or was already used. Sign in again."
    | "state_expired" -> "The sign-in took longer than ten minutes. Sign in again."
    | "provider_denied" -> "GitHub sign-in was cancelled."
    | "expired" -> "Your session ended. Sign in again."
    | "revoked" -> "Your session is no longer valid. Sign in again."
    | "provider_unavailable" -> "GitHub or the sign-in service cannot be reached. Chrona will try again."
    | "repository_access_denied" -> "Your GitHub account cannot read this deployment's data repository."
    | "sign_in_failed" -> "Sign-in could not start. Try again."
    | other -> $"Sign-in was refused ({other})."

let internal retentionText =
    function
    | ThisPage -> "Your token is kept in this page only: reloading or closing it signs you out."
    | ThisTab -> "Your token is kept in this tab until it closes."

let internal identityView (model: Model) =
    let identity = model.Identity
    let notice = identity.Notice

    [ flag "shellVisible" (canWork model)
      flag "screenConfiguring" (identity.Mode = Configuring)
      flag "screenMisconfigured" (match identity.Mode with Misconfigured _ -> true | _ -> false)
      text "misconfiguredDetail" (match identity.Mode with Misconfigured detail -> detail | _ -> "")
      flag "screenSignIn" (match identity.Mode with SignInRequired _ -> true | _ -> false)
      flag "signInBusy" (identity.Mode = SignInRequired true)
      text "signInButton" (if identity.Mode = SignInRequired true then "Signing in…" else "Sign in with GitHub")
      flag "retentionPage" (identity.Retention = ThisPage)
      flag "retentionTab" (identity.Retention = ThisTab)
      flag "hasSignInNotice" (notice.IsSome && not (canWork model))
      flag "hasSessionNotice" (notice.IsSome && canWork model)
      text "signInNotice" (notice |> Option.map noticeText |> Option.defaultValue "")
      text "signInNoticeCode" (notice |> Option.defaultValue "")
      flag "accountSignedIn" (identity.Mode = SignedInMode)
      // Signing out with unsent changes (WI-0058).
      flag "signOutChoosing" (identity.SignOut = Some ChoosingUnsent)
      flag "signOutSending" (identity.SignOut = Some SendingUnsent)
      flag "signOutConfirming" (identity.SignOut = Some ConfirmingDiscard)
      flag "signOutDiscarding" (identity.SignOut = Some DiscardingUnsent)
      flag "signOutCannotSend" (not (UpdateSignOut.canSendUnsent model))
      flag "signOutCannotKeep" (not (UpdateSignOut.canKeepUnsent model))
      text
          "signOutSummary"
          (let count = UpdateSignOut.unsentCount model
           let changes = if count = 1 then "1 change has" else $"{count} changes have"

           match count, UpdateDeviceTimer.hasKeptTimer model with
           | 0, true -> "Your timer is on this device. Signing out must not leave it behind without your knowing."
           | _, true -> changes + " not reached GitHub, and your timer is on this device. Signing out must not leave them behind without your knowing."
           | _ -> changes + " not reached GitHub. Signing out must not leave them behind without your knowing.")
      text "signOutNote" (identity.SignOutNote |> Option.defaultValue "")
      text
          "signOutDiscardText"
          (let count = UpdateSignOut.unsentCount model
           let changes = if count = 1 then "1 change" else $"{count} changes"

           (match count, UpdateDeviceTimer.hasKeptTimer model with
            | 0, true -> "Discard your timer?"
            | _, true -> $"Discard {changes} and your timer?"
            | _ -> $"Discard {changes}?")
           + " They will not be saved anywhere, and this cannot be undone.")
      // Clearing this device (WI-0057).
      flag "canClearDevice" (UpdateSignOut.canClearDevice model && identity.DeviceClear.IsNone && identity.SignOut.IsNone)
      flag "clearDeviceConfirming" (identity.DeviceClear = Some ConfirmingClear)
      flag "clearDeviceClearing" (identity.DeviceClear = Some Clearing)
      flag "hasDeviceNote" identity.DeviceNote.IsSome
      text "deviceNote" (identity.DeviceNote |> Option.defaultValue "")
      flag "accountLocal" (identity.Mode = LocalOnly)
      text "accountLogin" model.Session.DisplayName
      text "accountProvider" (match model.Session.Kind with SignedIn provider -> provider | LocalSession -> "")
      text "accountRetention" (retentionText identity.Retention)
      flag "screenOpening" (identity.Mode = SignedInMode && model.Store.Opening)
      flag "screenStoreFailed" (identity.Mode = SignedInMode && model.Store.Failure.IsSome)
      // A newer Chrona deployed since this page started (WI-0063).
      flag "shellUpdate" model.Shell.Newer.IsSome
      flag "shellReloadBlocked" (not model.Store.Conflicts.IsEmpty)
      text
          "shellUpdateText"
          (if model.Store.Conflicts.IsEmpty then
               "Reload when you are ready. Your unsent changes and your timer are kept; text typed into a form and not saved is not."
           else
               "Resolve the changes not saved under More first: they live only in this page.")
      text "storeFailure" (model.Store.Failure |> Option.defaultValue "")
      // Starting offline: the device's own timer and unsent changes (WI-0055).
      flag "offlineTimer" (identity.Mode = SignedInMode && model.Store.Failure.IsSome && UpdateDeviceTimer.hasKeptTimer model)
      text
          "offlineWaiting"
          (match model.Store.Waiting with
           | 0 -> ""
           | 1 -> "1 change waits in this browser and is sent when your records open."
           | n -> $"{n} changes wait in this browser and are sent when your records open.")
      flag "offlineStopped" model.Stopped.IsSome
      text
          "offlineStoppedText"
          (model.Stopped
           |> Option.map (fun stopped -> $"Stopped: {Format.minutes stopped.TotalMinutes} held. Complete it once your records open.")
           |> Option.defaultValue "")
      flag "hasStoreProblems" (not model.Store.Integrity.IsEmpty)
      text "storeProblemSummary" (plural model.Store.Integrity.Length "record needs attention" "records need attention")
      items
          "storeProblems"
          (model.Store.Integrity
           |> List.mapi (fun index d -> [ t "id" $"{index}"; t "code" (code d); t "text" (describe model d) ])) ]

let internal accessName (capabilities: Set<Access.Capability>) =
    if capabilities = Access.Grants.administrator then "administrator"
    elif capabilities = Access.Grants.reviewer then "reviewer"
    elif capabilities = Access.Grants.ownTime then "ownTime"
    else "custom"

let internal organizationName (model: Model) (organizationId: string) =
    model.Deployment
    |> Option.bind (fun config -> Deployment.organization config organizationId)
    |> Option.map _.DisplayName
    |> Option.defaultValue organizationId

/// The organizations of the deployment, the members of the current one,
/// and the gate for someone who is not a member (3, 2.5).
let internal membershipView (model: Model) =
    let organizations = model.Deployment |> Option.map _.Organizations |> Option.defaultValue []
    let me = model.Session.ActorId
    let subject = (me.Split(':', 2) |> Array.last)

    let notMember =
        model.Identity.Mode = SignedInMode
        && not model.Store.Opening
        && model.Store.Failure.IsNone
        && model.Store.Confirmation.IsNone
        && not (model.Roster.Members.ContainsKey me)

    [ flag "screenNotMember" notMember
      flag "screenConfirmAdministrator" (model.Identity.Mode = SignedInMode && model.Store.Confirmation.IsSome)
      text "confirmationDetail" (model.Store.Confirmation |> Option.map fst |> Option.defaultValue "")
      flag "canConfirmAdministrator" (model.Store.Confirmation |> Option.exists snd)
      text
          "notMemberDetail"
          $"You signed in as {model.Session.DisplayName}. GitHub account {subject} is not a member of {organizationName model model.Session.OrganizationId}. Ask one of its administrators to add GitHub account {subject}."
      text "organizationName" (organizationName model model.Session.OrganizationId)
      flag "canChooseOrganization" (model.Identity.Mode = SignedInMode && organizations.Length > 1)
      items
          "organizationOptions"
          [ for organization in organizations ->
                [ t "id" organization.Id; t "name" organization.DisplayName; f "selected" (organization.Id = model.Session.OrganizationId) ] ]
      // A local session has no one else to add.
      flag "canManageMembers" (model.Identity.Mode = SignedInMode && permits model Access.ManageOrganizationSettings)
      items
          "members"
          [ for KeyValue(id, membership) in model.Roster.Members ->
                [ t "id" id
                  t "name" membership.Principal.DisplayName
                  t "account" (id.Split(':', 2) |> Array.last)
                  t "access" (accessName membership.Capabilities)
                  f "isYou" (id = me) ] ]
      text "memberId" model.MemberDraft.Id
      text "memberName" model.MemberDraft.Name
      text "memberAccess" model.MemberDraft.Access
      flag "hasMemberProblems" (model.Problems.ContainsKey MemberForm)
      items "memberProblems" (problemItems model MemberForm)
      // The activity index: derived, kept with every change, rebuilt from
      // the records on request (40).
      flag "canRebuildIndex" (model.Identity.Mode = SignedInMode && permits model Access.ManageOrganizationSettings)
      text "indexStatus" model.Store.Index
      flag "hasIndexProblems" (model.Problems.ContainsKey IndexForm)
      items "indexProblems" (problemItems model IndexForm) ]

/// What the session's person may do in the organization (3).
let internal accessView (model: Model) =
    let held = Access.capabilitiesOf model.Roster model.Session.ActorId

    [ text "accessOrganization" model.Roster.OrganizationId
      items
          "accessCapabilities"
          [ for capability in Access.allCapabilities do
                if held.Contains capability then
                    let name = Access.capabilityName capability
                    [ t "id" name; t "name" (capabilityText name) ] ]
      flag "hasPeriodProblems" (model.Problems.ContainsKey PeriodForm)
      items "periodProblems" (problemItems model PeriodForm) ]
