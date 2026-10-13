/// The Chrona application's projection: authoritative state to Limen's flat
/// view state. Pure: no DOM, no clock (the model's `Now` is the last instant
/// the engine was told). Every word a person reads is decided here.
///
/// This module assembles the whole view; each part is in a Project* module
/// beside it.
module Chrona.Engine.App.Project

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
open Chrona.Engine.App.ProjectIdentity

// ---- the whole view ---------------------------------------------------------------

let project (model: Model) : View =
    // Without ViewOwnTime nothing of the person's time is shown.
    let model =
        if permits model Access.ViewOwnTime then
            model
        else
            { model with
                Ledger = Ledger.empty
                Attestations = []
                Timer = Timer.Idle
                Stopped = None }

    let tone, headline, detail = storeLines model

    let needsReferences =
        (Reference.selectable Reference.Project model.References).IsEmpty
        || (Reference.selectable Reference.ActivityType model.References).IsEmpty

    [ flag "screenToday" (shows model (function Places.Today | Places.Day _ -> true | _ -> false))
      flag "screenTrack" (shows model ((=) Places.Track))
      flag "screenMore" (shows model (function Places.Settings _ -> true | _ -> false))
      flag "screenActivity" (shows model (function Places.Entry _ -> true | _ -> false))
      yield! problemView model
      yield! linkView model
      yield! weekView model
      yield! periodPage model
      yield! projectsView model
      yield! projectView model
      yield! candidatesView model
      yield! candidateView model
      items "navigation" (navigation model)
      text "announcement" model.Announcement
      yield! identityView model
      yield! accessView model
      yield! membershipView model
      text "sessionName" model.Session.DisplayName
      text "sessionInitials" (Format.initials model.Session.DisplayName)
      text "storeTone" tone
      text "storeHeadline" headline
      text "storeDetail" detail
      flag "queueElsewhere" (match model.Store.Sync.Holder with HeldElsewhere _ -> true | _ -> false)
      flag "queueWaiting" (model.Store.Sync.Holder = HeldElsewhere true)
      // Unsent changes from an earlier version that name no one (Arca 0.4.0).
      flag "hasEarlierUnsent" (model.Store.Sync.Earlier > 0 && model.Earlier <> KeptEarlier)
      text
          "earlierUnsentText"
          (plural model.Store.Sync.Earlier "unsent change" "unsent changes"
           + " from an earlier version of Chrona "
           + (if model.Store.Sync.Earlier = 1 then "is" else "are")
           + " in this browser, and Chrona cannot tell whose. Nothing is sent or discarded until you decide.")
      flag "earlierConfirming" (model.Earlier = ConfirmingEarlierDiscard)
      text
          "earlierDiscardText"
          ("Discard " + plural model.Store.Sync.Earlier "change" "changes" + "? They will not be saved anywhere, and this cannot be undone.")
      flag "offlineInPage" (offlineInPage model.Store.Sync)
      flag "hasQueueNotice" model.Store.Sync.Notice.IsSome
      text "queueNotice" (model.Store.Sync.Notice |> Option.defaultValue "")
      text "queueMode" (queueMode model.Store.Kind model.Store.Sync)
      text "todayLong" (Format.longDate (Model.today model))
      flag "needsReferences" needsReferences
      yield! today model
      yield! timer model
      yield! completion model
      yield! manual model
      yield! more model
      yield! conflictView model
      flag "hasObligations" (not (obligations model).IsEmpty)
      items "obligations" (obligations model)
      yield! detailView model
      yield! reviewView model
      yield! monthView model
      yield! periodView model
      yield! reportView model ]
