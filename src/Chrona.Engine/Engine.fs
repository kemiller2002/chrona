namespace Chrona.Engine

open System
open Chrona.Semantic

type Draft =
    { BusinessDate: string
      Minutes: string
      Description: string }

type State =
    { Draft: Draft
      Activities: Activity list
      Notice: string
      Sequence: int }

type DraftField =
    | BusinessDate
    | Minutes
    | Description

type Command =
    | EditDraft of field: DraftField * value: string
    | RecordManualActivity

type Error =
    | InvalidBusinessDate
    | InvalidMinutes
    | DescriptionRequired

module Engine =
    let emptyDraft =
        { BusinessDate = ""
          Minutes = ""
          Description = "" }

    let initial =
        { Draft = emptyDraft
          Activities = []
          Notice = ""
          Sequence = 0 }

    let private validDate value =
        match DateOnly.TryParse value with
        | true, _ -> true
        | _ -> false

    let private minutes value =
        match Int32.TryParse value with
        | true, parsed when parsed > 0 && parsed <= 1440 -> Some parsed
        | _ -> None

    let validate draft =
        if not (validDate draft.BusinessDate) then Error InvalidBusinessDate
        else
            match minutes draft.Minutes with
            | None -> Error InvalidMinutes
            | Some _ when draft.Description.Trim().Length < 3 -> Error DescriptionRequired
            | Some parsed -> Ok parsed

    let transition state command =
        match command with
        | EditDraft(field, value) ->
            let nextDraft =
                match field with
                | BusinessDate -> { state.Draft with BusinessDate = value }
                | Minutes -> { state.Draft with Minutes = value }
                | Description -> { state.Draft with Description = value }

            Ok { state with Draft = nextDraft; Notice = "" }

        | RecordManualActivity ->
            match validate state.Draft with
            | Error error -> Error error
            | Ok exactMinutes ->
                let sequence = state.Sequence + 1
                let activity =
                    { Id = ActivityId $"activity-{sequence}"
                      BusinessDate = state.Draft.BusinessDate
                      ExactMinutes = exactMinutes
                      Description = state.Draft.Description.Trim()
                      Revision = Revision 1 }

                Ok
                    { Draft = emptyDraft
                      Activities = activity :: state.Activities
                      Notice = "Activity recorded locally. Shared persistence is the next explicit effect boundary."
                      Sequence = sequence }
