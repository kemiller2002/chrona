/// Sign-in and the records it opens (CHX-022, CHX-023): the session before
/// anyone signs in, the deployment's configuration, opening the store, the
/// organization an address names, and the page coming back (WI-0063).
module Chrona.Engine.App.UpdateSession

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
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

/// The session a page has before anyone signs in; it can do no work.
let noOne =
    { ActorId = "no-one"
      OrganizationId = "local"
      DisplayName = ""
      Kind = LocalSession }

/// The page is back after being hidden, frozen or restored from the
/// back/forward cache. Nothing was lost while it was away (the queue and the
/// timer are kept as they change), but it may be behind: the timer's
/// wake-ups start again from its timestamps, unsent changes are sent, the
/// records are read again, and the deployment is asked whether a newer
/// Chrona is served.
let internal pageReturned (model: Model) =
    let generation = model.TickGeneration + 1

    let ticking =
        match model.Timer with
        | Timer.Running _ -> [ Wake(generation, TickMs) ]
        | _ -> []

    let records =
        match model.Store.Kind with
        | Durable _ when canWork model -> [ ReadMonths [] ]
        | _ -> []

    let unsent = if model.Store.Pending.IsEmpty && model.Store.Cached.IsNone then [] else [ SendUnsent ]
    let shell = if model.Shell.Build = Development then [] else [ CheckShell ]
    // The tab holding this browser's unsent changes may have closed meanwhile:
    // asked first, so what is sent next includes what it kept.
    let claim = if model.Store.Sync.Holder = HeldElsewhere false then [ ClaimQueue ] else []
    { model with TickGeneration = generation }, ticking @ claim @ unsent @ records @ shell

/// Reloads for the newer Chrona, unless something the page cannot keep would
/// be lost: changes not saved because they changed elsewhere live only in
/// this page until they are resolved. Unsent changes and the timer are kept
/// across a reload already.
let internal reloadShell (model: Model) =
    match model.Shell.Newer with
    | Some _ when model.Store.Conflicts.IsEmpty -> model, [ ReloadPage ]
    | _ -> model, []

/// The model a new session starts from: nothing of the previous person's
/// remains in memory. The route, the zone and the deployment carry over.
let internal fresh (session: Session) (identity: IdentityState) (model: Model) =
    { Model.initial session model.Store.Kind model.Now with
        Place = model.Place
        RouteProblem = model.RouteProblem
        Router = model.Router
        Page = model.Page
        Zone = model.Zone
        PeriodConfig = model.PeriodConfig
        Deployment = model.Deployment
        Identity = identity }

let internal configurationRead (text: string option) (model: Model) =
    let misconfigured detail =
        { model with Identity = { model.Identity with Mode = Misconfigured detail } }, []

    match text |> Option.map Deployment.parse with
    | None -> misconfigured "The deployment's configuration could not be read."
    | Some(Error diagnostic) -> misconfigured $"The deployment's configuration is not valid ({code diagnostic})."
    | Some(Ok config) ->
        let model = { model with Deployment = Some config }

        match config.Identity with
        | None ->
            let local = { model with Identity = { model.Identity with Mode = LocalOnly; Callback = [] } }
            local, [ LoadTimer(timerKey local) ]
        | Some identity ->
            { model with Identity = { model.Identity with Mode = SignInRequired true; Callback = [] } },
            [ BeginIdentity(identity, model.Identity.Callback) ]

/// The dates the first read covers: today and the current period, so the
/// today view, the period and the obligations have their records (38).
let internal openingDates (model: Model) =
    let today = Model.today model
    let period = Periods.containing model.PeriodConfig today
    [ today; period.Start; period.Finish ] |> List.distinct

/// Starts reading the organization's records when the deployment keeps them;
/// until they arrive nothing can be done.
let internal openStore (model: Model) =
    match model.Deployment with
    | Some config when config.Location.IsSome ->
        { model with
            Store =
                { model.Store with
                    Kind = Durable "GitHub"
                    Opening = true
                    Failure = None
                    Confirmation = None } },
        [ OpenStore(config, model.Session, openingDates model) ]
    | _ -> model, []

let internal storeOpened (contents: StoreContents) (model: Model) =
    { model with
        Ledger =
            { Activities = contents.Activities |> List.map (fun a -> a.ActivityId, a) |> Map.ofList
              Audit = contents.Audit }
        References =
            { OrganizationId = model.Session.OrganizationId
              Items = contents.References |> List.map (fun item -> (item.Kind, item.Id), item) |> Map.ofList }
        Attestations = contents.Attestations
        Roster =
            { OrganizationId = model.Session.OrganizationId
              Members = contents.Members |> List.map (fun m -> m.Principal.PrincipalId, m) |> Map.ofList }
        Store =
            { model.Store with
                Kind = Durable contents.Name
                Opening = false
                Failure = None
                Confirmation = None
                Integrity = contents.Problems
                Held = contents.Held
                Months = contents.Months
                Others = contents.Others
                OthersReading = model.Store.OthersReading |> List.filter (fun wanted -> not (List.contains wanted contents.Others))
                Audited = contents.Audit.Length
                Waiting = 0
                Reading = model.Store.Reading |> List.filter (fun month -> not (List.contains month contents.Months))
                History = contents.History
                Index = contents.Index
                IndexCurrent = contents.IndexCurrent
                Cached = contents.Cached }
        // The organization's periods as saved, reckoned in its zone (WI-0036).
        PeriodsStored = contents.Periods
        PeriodConfig =
            match contents.Periods with
            | Some stored -> { stored.Config with ZoneId = model.PeriodConfig.ZoneId }
            | None -> model.PeriodConfig
        Reviews = contents.Reviews
        Reviewing = contents.Reviewing
        Candidates = contents.Candidates
        // Said once, when they first open, with what recovering the timer
        // found; reading a further month is quiet.
        Announcement =
            if model.Store.Opening then
                match model.TimerNote with
                | Some note -> $"Your records are open. {note}"
                | None -> "Your records are open."
            else
                model.Announcement
        TimerNote = None },
    []

/// What an address's organization asks of the engine.
type internal OrganizationStep =
    | StayHere
    | SwitchTo of organization: string
    | MissingOrganization of organization: string

/// Whether the engine knows which organizations the person may work in:
/// someone may work, under a deployment it has read.
let internal knowsOrganizations (model: Model) =
    model.Deployment.IsSome
    && (match model.Identity.Mode with
        | LocalOnly
        | SignedInMode -> true
        | _ -> false)

let internal organizationStep (model: Model) (address: Places.Address) =
    match address.Organization with
    | Some organization when knowsOrganizations model ->
        let known =
            if namesOrganization model then
                model.Deployment |> Option.bind (fun config -> Deployment.organization config organization) |> Option.isSome
            else
                organization = model.Session.OrganizationId

        if not known then MissingOrganization organization
        elif organization <> model.Session.OrganizationId then SwitchTo organization
        else StayHere
    | _ -> StayHere

/// The organization an address should name: the person's, where addresses
/// name one; none where they do not; and, until that is known (before
/// sign-in), whatever the address named, so a sign-in keeps it.
let internal wantedOrganization (model: Model) (address: Places.Address) =
    if knowsOrganizations model then (addressOf model address.Place).Organization else address.Organization

/// The organization the page's address names, if the deployment serves it:
/// a sign-in opens that one rather than the first.
let internal addressedOrganization (model: Model) (config: Deployment.DeploymentConfig) =
    model.Router.Current
    |> Option.bind (fun location -> Places.parse Limen.Routing.Router.allowAll location |> Result.toOption)
    |> Option.bind _.Organization
    |> Option.bind (Deployment.organization config)
    |> Option.map _.Id

let internal identityChanged (change: IdentityChange) (model: Model) =
    let identity = model.Identity

    match change with
    | SignedInAs session ->
        // The person works in the organization the deployment serves.
        let session =
            { session with
                OrganizationId =
                    model.Deployment
                    |> Option.map (fun config -> addressedOrganization model config |> Option.defaultValue (Deployment.organizationId config))
                    |> Option.defaultValue session.OrganizationId }

        let model = fresh session { identity with Mode = SignedInMode; Notice = None } model
        let opened, effects = openStore { model with Announcement = $"Signed in as {session.DisplayName}." }
        opened, effects @ [ LoadTimer(timerKey opened) ]
    | SigningIn -> { model with Identity = { identity with Mode = SignInRequired true } }, []
    | SignedOutWith notice ->
        let signedOut = { identity with Mode = SignInRequired false; Notice = notice }

        match identity.Mode with
        // Whoever was signed in leaves nothing behind in this page.
        | SignedInMode -> { fresh noOne signedOut model with Announcement = "Signed out." }, []
        | _ -> { model with Identity = signedOut }, []
    | ProviderUnavailable -> { model with Identity = { identity with Notice = Some "provider_unavailable" } }, []
