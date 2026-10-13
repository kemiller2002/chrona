/// Signing out with unsent changes (WI-0058), and the identity events.
module Chrona.Engine.App.UpdateSignOut

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateCommands
open Chrona.Engine.App.UpdateRecording
open Chrona.Engine.App.UpdateCorrections
open Chrona.Engine.App.UpdateReports
open Chrona.Engine.App.UpdatePlaces
open Chrona.Engine.App.UpdatePeriods
open Chrona.Engine.App.UpdateCandidates
open Chrona.Engine.App.UpdateMembers
open Chrona.Engine.App.UpdateResolving
open Chrona.Engine.App.UpdateEvents
open Chrona.Engine.App.UpdateDeviceTimer
open Chrona.Engine.App.UpdateSession

/// This account's changes that have not reached GitHub: those waiting to be
/// sent, and those not saved because the records moved (kept only in this
/// page until resolved).
let unsentCount (model: Model) =
    model.Store.Pending.Length + model.Store.Conflicts.Length

/// Everything of this account's that has not reached GitHub: changes, and
/// the device's timer.
let unsentWork (model: Model) =
    unsentCount model + (if hasKeptTimer model then 1 else 0)

let internal policy (model: Model) =
    model.Deployment |> Option.map _.SharedDevice |> Option.defaultValue Deployment.Ask

/// Keeping them on this device for this account: only where the deployment
/// allows it, and only for changes the device can keep (a change not saved
/// because the records moved lives only in this page).
let canKeepUnsent (model: Model) =
    policy model = Deployment.Ask && model.Store.Conflicts.IsEmpty && unsentWork model > 0

/// Clearing Chrona's data from this browser: where records were read from
/// GitHub, and only when nothing of this account's would be lost with it.
let canClearDevice (model: Model) =
    (match model.Store.Kind with
     | Durable _ -> true
     | InMemory -> false)
    && unsentWork model = 0
    && model.Store.Conflicts.IsEmpty

/// Sending them now: when there are some to send. Whether GitHub can be
/// reached is found out by trying; if not, the choice comes back.
let canSendUnsent (model: Model) = not model.Store.Pending.IsEmpty

/// While signing out sends the unsent changes: signed out once every one is
/// stored; back to the choice when some could not be sent.
let internal afterSending (model: Model) =
    let identity = model.Identity
    let choose note = { model with Identity = { identity with SignOut = Some ChoosingUnsent; SignOutNote = Some note } }, []

    match identity.SignOut with
    | Some SendingUnsent when model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty && hasKeptTimer model ->
        choose "Your changes are stored. Your timer is still on this device: keep it for this account, or discard it."
    | Some SendingUnsent when model.Store.Pending.IsEmpty && model.Store.Conflicts.IsEmpty ->
        { model with Identity = { identity with SignOut = None; SignOutNote = None } }, [ LeaveDevice(SentUnsent, 0); SignOut ]
    | Some SendingUnsent when model.Store.Pending.IsEmpty ->
        choose "Some changes were not saved because they changed elsewhere first. Resolve them under More, or discard them."
    | Some SendingUnsent when model.Store.Sync.Offline -> choose "GitHub cannot be reached, so they could not be sent."
    | _ -> model, []

let internal onIdentityEvent (name: string) (value: string) (model: Model) =
    let identity = model.Identity

    match name, identity.Mode with
    | "signInRetention", _ ->
        let retention = if value = "tab" then ThisTab else ThisPage
        { model with Identity = { identity with Retention = retention } }, []
    | "signIn", SignInRequired false ->
        // The address to come back to is kept in this tab first: GitHub's
        // callback returns to the deployment's address, without it (CHX-460).
        let target =
            match model.Place with
            | Places.SignIn returnTo -> returnTo
            | _ -> None
            |> Option.orElse identity.ReturnTo

        { model with Identity = { identity with Mode = SignInRequired true; Notice = None } }, [ KeepReturn target; SignIn identity.Retention ]
    // Unsent changes are never left behind unknowingly (WI-0058).
    | "signOut", SignedInMode when unsentWork model > 0 ->
        { model with Identity = { identity with SignOut = Some ChoosingUnsent; SignOutNote = None } }, []
    | "signOut", SignedInMode -> model, [ LeaveDevice(NothingUnsent, 0); SignOut ]
    | "signOutSend", SignedInMode when identity.SignOut = Some ChoosingUnsent && canSendUnsent model ->
        { model with Identity = { identity with SignOut = Some SendingUnsent; SignOutNote = None } }, [ SendUnsent ]
    | "signOutKeep", SignedInMode when identity.SignOut = Some ChoosingUnsent && canKeepUnsent model ->
        { model with
            Identity = { identity with SignOut = None }
            Announcement = $"Your unsent work is kept on this device for {model.Session.DisplayName}." },
        [ LeaveDevice(KeptUnsent, unsentWork model); SignOut ]
    | "signOutDiscard", SignedInMode when identity.SignOut = Some ChoosingUnsent ->
        { model with Identity = { identity with SignOut = Some ConfirmingDiscard } }, []
    | "signOutDiscardConfirmed", SignedInMode when identity.SignOut = Some ConfirmingDiscard ->
        { model with Identity = { identity with SignOut = Some DiscardingUnsent } }, [ DiscardUnsent ]
    | "signOutCancel", SignedInMode when identity.SignOut.IsSome && identity.SignOut <> Some DiscardingUnsent ->
        { model with Identity = { identity with SignOut = None; SignOutNote = None } }, []
    | "clearDevice", SignedInMode when canClearDevice model ->
        { model with Identity = { identity with DeviceClear = Some ConfirmingClear; DeviceNote = None } }, []
    | "clearDeviceConfirmed", SignedInMode when identity.DeviceClear = Some ConfirmingClear && canClearDevice model ->
        { model with Identity = { identity with DeviceClear = Some Clearing } }, [ ClearDevice ]
    | "clearDeviceCancel", SignedInMode when identity.DeviceClear = Some ConfirmingClear ->
        { model with Identity = { identity with DeviceClear = None } }, []
    | "retryStore", SignedInMode when model.Store.Failure.IsSome -> openStore model
    | "confirmAdministrator", SignedInMode when model.Store.Confirmation |> Option.exists snd ->
        { model with Store = { model.Store with Opening = true; Confirmation = None } }, [ ConfirmAdministrator ]
    // Working in another of the deployment's organizations: nothing of the
    // current one stays in the page, and the other's records are opened.
    | "chooseOrganization", SignedInMode when value <> model.Session.OrganizationId ->
        match model.Deployment |> Option.bind (fun config -> Deployment.organization config value) with
        | Some organization ->
            let session = { model.Session with OrganizationId = organization.Id }
            let opened, effects = openStore (fresh session identity model)
            // Its home, named by the organization (CHX-460).
            let moved, more = navigate Places.Today opened
            moved, effects @ more
        | None -> model, []
    | _ -> model, []
