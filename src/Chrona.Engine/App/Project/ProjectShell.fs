/// The page's shell: which screen shows, the navigation, where the records
/// and the unsent changes stand, local clocks, and the reference options.
module Chrona.Engine.App.ProjectShell

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.ProjectWords

/// Whether the page shows a screen of these places: the address names one,
/// and no problem with it is being shown instead.
let internal shows (model: Model) (screen: Places.Place -> bool) = model.RouteProblem.IsNone && screen model.Place

/// A place's relative link ("#/track"), for an `href`.
let internal hrefOf (model: Model) (place: Places.Place) =
    match Places.href (addressOf model place) with
    | Ok href -> href
    // Every place has an address (PlacesTests); one without is a defect.
    | Error error -> invalidOp $"No address for {place}: %A{error}"

/// The navigation section a place belongs to. A day, an activity and a
/// review belong to Today; reports to Month.
let internal sectionOf (place: Places.Place) =
    match place with
    | Places.Today
    | Places.Day _
    | Places.ThisWeek
    | Places.Week _
    | Places.ThisPeriod
    | Places.Period _
    | Places.Entry _
    | Places.ReviewToday
    | Places.Review _
    | Places.Candidates _
    | Places.Candidate _ -> "today"
    | Places.Track -> "track"
    | Places.ThisMonth
    | Places.Month _
    | Places.Reports _ -> "month"
    | Places.Projects
    | Places.Project _
    | Places.Settings _ -> "more"
    | Places.SignIn _ -> ""

let internal navigation (model: Model) =
    let section = if model.RouteProblem.IsSome then "" else sectionOf model.Place

    [ "today", "Today", "TD", Places.Today
      "track", "Track", "TR", Places.Track
      "month", "Month", "MO", Places.ThisMonth
      "more", "More", "MR", Places.Settings None ]
    |> List.map (fun (id, label, mark, place) ->
        [ t "id" id
          t "label" label
          t "mark" mark
          t "href" (hrefOf model place)
          t "current" (if id = section then "page" else "false") ])

/// Where unsent changes are kept, for the person (23).
let internal unsentDetail (sync: SyncState) =
    match sync.Holder, sync.KeptInBrowser, sync.Note with
    | HeldElsewhere _, _, _ ->
        "Another tab is holding your unsent changes; the changes made here are sent from this page. Keep this page open until they are sent."
    | _, true, _ -> "They are kept in this browser and sent, in order, when GitHub can be reached."
    | _, false, Some note -> $"{note} Keep this page open until they are sent."
    | _, false, None -> "This browser cannot keep them. Keep this page open until they are sent."

/// Where this browser's unsent changes are kept, as a mode (WI-0067,
/// WI-0059: Limen LCP-065).
let internal queueMode (kind: StoreKind) (sync: SyncState) =
    match kind, sync.Holder, sync.KeptInBrowser, sync.Durability with
    // A deployment that stores nothing acknowledges every change at once.
    | InMemory, _, _, _ -> "None"
    | _, HeldElsewhere _, _, _ -> "Held by another Chrona tab"
    | _, HeldHere, true, InLocalStorage -> "Kept in this browser (localStorage) by this tab"
    | _, HeldHere, true, _ -> "Kept in this browser (IndexedDB) by this tab"
    | _, _, false, _ -> "In this page only"

/// Changes made now would live only in this page: GitHub cannot be reached
/// and this browser keeps nothing for this tab (LCP-065: said before a change
/// is made, not after).
let internal offlineInPage (sync: SyncState) =
    sync.Offline && sync.Holder = HeldHere && not sync.KeptInBrowser

/// A moment as a date and clock time in the person's zone: when the records
/// shown from the read cache were read from GitHub (WI-0057), or when an
/// entry was recorded.
let internal localStamp (model: Model) (instant: DateTimeOffset) =
    match model.Zone with
    | Some zone ->
        let o = occurrence zone instant
        $"{Format.longDate o.LocalDate}, {Format.clock o.LocalTime}"
    | None -> instant.ToString("u")

let internal storeLines (model: Model) =
    let store = model.Store

    match store.Problem, store.Kind with
    | _, _ when not store.Conflicts.IsEmpty ->
        "attention",
        "Not saved: changed elsewhere",
        plural store.Conflicts.Length "change waits" "changes wait"
        + " for your decision under More. Nothing of yours was discarded."
    | Some(Conflict _), _ -> "attention", "Not saved: changed elsewhere", "Resolve it under More."
    | Some(Failed detail), _ -> "attention", "Not saved", detail
    | Some(OutcomeUnknown detail), _ -> "attention", "Save outcome unknown", detail
    // Shown from the read cache, never as current (LCP-084).
    | _, _ when store.Cached.IsSome ->
        let waiting =
            if store.Pending.IsEmpty then
                ""
            else
                plural store.Pending.Length "change waits" "changes wait" + " to be sent. "

        "attention",
        "Offline: your records as of " + localStamp model store.Cached.Value,
        waiting
        + "GitHub cannot be reached, so these are your records as GitHub last gave them to this browser. Changes you make are kept and checked against GitHub's records before they are sent."
    | _, _ when not store.Pending.IsEmpty && store.Sync.Offline ->
        "attention",
        "Offline: " + plural store.Pending.Length "change waits" "changes wait" + " to be sent",
        unsentDetail store.Sync
    | _, _ when not store.Pending.IsEmpty && not store.Sync.KeptInBrowser ->
        "progress", "Saving…", plural store.Pending.Length "change" "changes" + ". " + unsentDetail store.Sync
    | _, _ when not store.Pending.IsEmpty -> "progress", "Saving…", plural store.Pending.Length "change" "changes"
    | _, InMemory ->
        "memory",
        "Kept in this tab only",
        "This deployment stores nothing: records last until this tab closes."
    | _, Durable name -> "ok", "All changes saved", $"Saved to {name}."

let internal elapsedSpan (model: Model) (timer: Timer.ActiveTimer) =
    timer.Segments
    |> List.sumBy (fun s -> max 0.0 ((defaultArg s.Finish model.Now) - s.Start).TotalSeconds)
    |> TimeSpan.FromSeconds
    |> fun span -> TimeSpan.FromSeconds(Math.Floor span.TotalSeconds)

let internal localClock (model: Model) (instant: DateTimeOffset) =
    match model.Zone with
    | Some zone -> Format.clock (occurrence zone instant).LocalTime
    | None -> Format.clock (TimeOnly.FromDateTime instant.UtcDateTime)

let internal options (model: Model) (kind: Reference.Kind) (selected: string) =
    Reference.selectable kind model.References
    |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "selected" (item.Id = selected) ])

let internal tagOptions (model: Model) (chosen: string list) =
    Reference.selectable Reference.Tag model.References
    |> List.map (fun item -> [ t "id" item.Id; t "name" item.Name; f "checked" (List.contains item.Id chosen) ])
