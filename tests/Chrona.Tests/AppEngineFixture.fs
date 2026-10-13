/// What the engine's tests share: a session, the clock and fresh ids, playing
/// messages through `update`, a model with reference data, and reading the
/// projected view.
module Chrona.Tests.AppEngineFixture

open System
open Xunit
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Engine.App.UpdateDispatch

let session =
    { ActorId = "person-1"
      OrganizationId = "org-1"
      DisplayName = "Kevin Miller"
      Kind = LocalSession }

/// 2026-10-08 14:10:00 in New York (EDT).
let start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)

/// Ids are unique across the whole test run, as the edge's are.
let sequence = ref 0

let ctxAt (now: DateTimeOffset) =
    { Now = now
      NewId = fun prefix -> $"{prefix}-{System.Threading.Interlocked.Increment sequence}" }

/// Applies messages in order, each at its own instant; keeps every effect.
let play (steps: (DateTimeOffset * Msg) list) (model: Model) =
    steps
    |> List.fold
        (fun (model, effects) (at, msg) ->
            let next, produced = update (ctxAt at) msg model
            next, effects @ produced)
        (model, [])

let ui name value = Ui(name, None, value, None)

let keyed name key value = Ui(name, Some key, value, None)

/// The browser asked to add a history entry for an address, or to replace
/// the current one (CHX-460).
let push location = Navigate(Limen.Routing.NavigationEffect.Push location)

let replace location = Navigate(Limen.Routing.NavigationEffect.Replace location)

let check name key on = Ui(name, Some key, "", Some on)

/// A deployment that configures neither storage nor sign-in: a local session.
let localConfiguration = """{"environment":"local","environmentName":"local"}"""

let fresh = initial session InMemory start

/// Answers every outstanding store request, as the in-memory store does.
let settle (model: Model) =
    model.Store.Pending |> List.fold (fun m request -> fst (update (ctxAt start) (StoreAnswered(request.CommitId, Committed)) m)) model

/// Started in New York with a project, an activity type and a tag, all stored.
let ready =
    play
        [ start, Started(Support.testPage, "#/track", [])
          start, ConfigurationRead(Some localConfiguration)
          start, EnvironmentDescribed "America/New_York"
          start, ui "newProjectName" "HelixNote"
          start, ui "addProject" ""
          start, ui "newActivityTypeName" "Research"
          start, ui "addActivityType" ""
          start, ui "newTagName" "Backend"
          start, ui "addTag" "" ]
        fresh
    |> fst
    |> settle

let idOf (kind: Reference.Kind) (model: Model) = (Reference.selectable kind model.References |> List.head).Id

let project = idOf Reference.Project ready

let activityType = idOf Reference.ActivityType ready

let tag = idOf Reference.Tag ready

let view (model: Model) = Project.project model |> Map.ofList

let textOf (key: string) (model: Model) =
    match (view model)[key] with
    | Value(Text t) -> t
    | other -> failwith $"{key} is {other}"

let flagOf (key: string) (model: Model) =
    match (view model)[key] with
    | Value(Flag f) -> f
    | other -> failwith $"{key} is {other}"

let itemsOf (key: string) (model: Model) =
    match (view model)[key] with
    | Items rows -> rows |> List.map Map.ofList
    | other -> failwith $"{key} is {other}"

let field (name: string) (row: Map<string, Scalar>) =
    match row[name] with
    | Text t -> t
    | Flag f -> string f
    | Number n -> string n

let at (minutes: float) = start.AddMinutes minutes

let chooseForTimer =
    [ start, ui "timerActivityType" activityType
      start, ui "timerProject" project
      start, ui "timerDescription" "Product research" ]

let manualDraft (date: string) =
    [ start, ui "manualActivityType" activityType
      start, ui "manualProject" project
      start, ui "manualStartDate" date
      start, ui "manualStartTime" "09:00"
      start, ui "manualEndTime" "10:15"
      start, ui "manualDescription" "Visual research"
      start, ui "manualPurpose" "Mobile patterns" ]

let withThree =
    let entry (start: string) (finish: string) (description: string) =
        [ at 0.0, ui "manualActivityType" activityType
          at 0.0, ui "manualProject" project
          at 0.0, ui "manualStartDate" "2026-10-08"
          at 0.0, ui "manualStartTime" start
          at 0.0, ui "manualEndTime" finish
          at 0.0, ui "manualDescription" description
          at 0.0, ui "manualPurpose" "Delivery"
          at 0.0, ui "saveManual" "" ]

    play (entry "09:00" "10:00" "First" @ entry "10:00" "10:30" "Second" @ entry "11:00" "11:30" "Third") ready |> fst |> settle

let activityId (description: string) (model: Model) =
    model.Ledger.Activities |> Map.toList |> List.map snd |> List.find (fun a -> a.Classification.Description = description) |> _.ActivityId

let opened (description: string) (model: Model) =
    fst (update (ctxAt start) (LocationMoved $"#/entries/{activityId description model}?on=2026-10-08") model)

let entry (date: string) (startTime: string) (endTime: string) (description: string) =
    [ start, ui "manualActivityType" activityType
      start, ui "manualProject" project
      start, ui "manualStartDate" date
      start, ui "manualStartTime" startTime
      start, ui "manualEndTime" endTime
      start, ui "manualDescription" description
      start, ui "manualPurpose" "Delivery"
      start, ui "saveManual" "" ]
