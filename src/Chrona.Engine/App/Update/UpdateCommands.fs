/// What every command shares (WI-0020 onwards): its problems on its form, the
/// ledger context it runs in, sending what changed to the store, a draft
/// edited in place, and moving the address (CHX-460).
module Chrona.Engine.App.UpdateCommands

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update

let internal withProblems (form: Form) (problems: Diagnostic list) (model: Model) =
    { model with Problems = if problems.IsEmpty then model.Problems.Remove form else model.Problems.Add(form, problems) }

let internal clear (form: Form) (model: Model) = withProblems form [] model

let internal commandContext (ctx: Ctx) (model: Model) (zone: Zone) : Ledger.CommandContext =
    { Performer = model.Session.ActorId
      At = ctx.Now
      Source = "chrona-web"
      Zone = zone
      References = model.References
      CorrelationId = None }

/// Sends a request to the store: it waits, pending, until answered.
let internal sendRequest (request: StoreRequest) (model: Model) =
    { model with
        Store = { model.Store with Pending = model.Store.Pending @ [ request ] } },
    [ Store request ]

/// Sends what became authoritative to the store.
let internal commitWith (ctx: Ctx) (activities: Activity list) (references: Reference.Item list) (attestations: Review.Attestation list) (model: Model) =
    // The command's audit entries go with its records (WI-0056).
    let entries = model.Ledger.Audit |> List.skip (min model.Store.Audited model.Ledger.Audit.Length)
    let known = activities @ (model.Ledger.Activities |> Map.toList |> List.map snd)

    let request =
        { emptyRequest (ctx.NewId "COMMIT") with
            Activities = activities
            References = references
            Attestations = attestations
            Audit = AuditRecord.place known entries }

    { model with
        Store =
            { model.Store with
                Pending = model.Store.Pending @ [ request ]
                Audited = model.Ledger.Audit.Length } },
    [ Store request ]

let internal commit (ctx: Ctx) (activities: Activity list) (references: Reference.Item list) (model: Model) =
    commitWith ctx activities references [] model

let internal toClassification (draft: ClassificationDraft) : Classification =
    { ProjectId = draft.ProjectId.Trim()
      ClientId = None
      EngagementId = None
      ActivityTypeId = draft.ActivityTypeId.Trim()
      Tags = draft.Tags
      Description = draft.Description.Trim()
      BusinessPurpose = draft.BusinessPurpose.Trim() }

let internal ofClassification (c: Classification) : ClassificationDraft =
    { ActivityTypeId = c.ActivityTypeId
      ProjectId = c.ProjectId
      Description = c.Description
      BusinessPurpose = c.BusinessPurpose
      Tags = c.Tags }

let internal toggle (item: string) (on: bool) (items: string list) =
    if on then (if List.contains item items then items else items @ [ item ]) else items |> List.filter ((<>) item)

let internal zoneOrProblem (model: Model) =
    match model.Zone with
    | Some zone -> Ok zone
    | None -> Error [ InvalidTimeZone "unknown" ]

let internal changed (before: Ledger.Ledger) (after: Ledger.Ledger) =
    after.Activities
    |> Map.toList
    |> List.filter (fun (id, a) -> before.Activities.TryFind id <> Some a)
    |> List.map snd

/// Runs a ledger command, refused when it changes time in a period that is
/// submitted or closed: the period is reopened first (WI-0036,
/// `CHRONA.REVIEW.SUBMITTED_PERIOD`).
let internal execute (model: Model) (context: Ledger.CommandContext) (ledger: Ledger.Ledger) (command: Ledger.Command) =
    Ledger.execute context ledger command
    |> Result.bind (fun after ->
        let touched = PeriodReview.touchedBy ledger.Activities (changed ledger after)

        match PeriodReview.restrictions model.PeriodConfig model.Reviews touched with
        | [] -> Ok after
        | problems -> Error problems)

/// Records activities one after another, all or nothing: the ledger only
/// changes if every one is accepted.
let internal recordAll (model: Model) (context: Ledger.CommandContext) (ledger: Ledger.Ledger) (activities: Activity list) =
    activities
    |> List.fold
        (fun state activity -> state |> Result.bind (fun l -> execute model context l (Ledger.Record activity)))
        (Ok ledger)

/// Moves to a place: the engine moves at once, and asks the browser to
/// follow (CHX-460). The place itself is settled from the new address after
/// every message (`settle`).
let internal move operation (place: Places.Place) (model: Model) =
    match operation PlaceAddresses.codec model.Router (addressOf model place) with
    | Ok(router, effect) -> { model with Router = router }, effect |> Option.map Navigate |> Option.toList
    // Every place has an address (PlacesTests); one without is a defect.
    | Error error -> invalidOp $"No address for {place}: %A{error}"

/// Going somewhere else: a new history entry, unless it is already current.
let internal navigate place model = move Limen.Routing.RouteCodec.navigate place model

/// Refining the current view (a filter, a date, a month): the current entry
/// is replaced, so Back steps to the previous place, not the previous filter.
let internal refine place model = move Limen.Routing.RouteCodec.refine place model

let internal draft (f: ClassificationDraft -> ClassificationDraft) (field: Model -> ClassificationDraft) (set: Model -> ClassificationDraft -> Model) (model: Model) =
    set model (f (field model)), []

let internal timerDraft f = draft f _.TimerDraft (fun m d -> { m with TimerDraft = d })
let internal completionDraft f = draft f _.CompletionDraft (fun m d -> { m with CompletionDraft = d })

let internal manualDraft f = draft f _.Manual.Classification (fun m d -> { m with Manual = { m.Manual with Classification = d } })

let internal manual (f: ManualDraft -> ManualDraft) (model: Model) = { model with Manual = f model.Manual }, []
