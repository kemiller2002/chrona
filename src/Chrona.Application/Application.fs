namespace Chrona.Application

open Chrona.Semantic
open Chrona.Engine
open Chrona.Application.Protocol

module Application =
    let private errorText draft error =
        match error with
        | InvalidBusinessDate -> "Choose a valid business date."
        | InvalidMinutes -> "Minutes must be between 1 and 1440."
        | DescriptionRequired -> "Describe the work in at least 3 characters."

    let eventToCommand event =
        match event.Name with
        | "dateChanged" -> EditDraft(BusinessDate, event.Value |> Option.defaultValue "")
        | "minutesChanged" -> EditDraft(Minutes, event.Value |> Option.defaultValue "")
        | "descriptionChanged" -> EditDraft(Description, event.Value |> Option.defaultValue "")
        | "recordActivity" -> RecordManualActivity
        | other -> failwithf "Unrecognized Chrona event '%s'." other

    let project state =
        let validation = Engine.validate state.Draft

        let activityItems =
            state.Activities
            |> List.rev
            |> List.map (fun activity ->
                let (ActivityId id) = activity.Id
                let (Revision revision) = activity.Revision
                Map.ofList
                    [ "id", VString id
                      "date", VString activity.BusinessDate
                      "minutes", VNumber activity.ExactMinutes
                      "description", VString activity.Description
                      "revision", VNumber revision ])

        let dateError =
            if state.Draft.BusinessDate = "" then ""
            else
                match Engine.validate { state.Draft with Minutes = "1"; Description = "abc" } with
                | Error InvalidBusinessDate -> "Choose a valid business date."
                | _ -> ""

        let minutesError =
            if state.Draft.Minutes = "" then ""
            else
                match System.Int32.TryParse state.Draft.Minutes with
                | true, parsed when parsed > 0 && parsed <= 1440 -> ""
                | _ -> "Minutes must be between 1 and 1440."

        let descriptionError =
            if state.Draft.Description = "" || state.Draft.Description.Trim().Length >= 3 then ""
            else "Describe the work in at least 3 characters."

        Map.ofList
            [ "date", VString state.Draft.BusinessDate
              "minutes", VString state.Draft.Minutes
              "description", VString state.Draft.Description
              "dateError", VString dateError
              "minutesError", VString minutesError
              "descriptionError", VString descriptionError
              "dateErrorVisible", VBool(dateError <> "")
              "minutesErrorVisible", VBool(minutesError <> "")
              "descriptionErrorVisible", VBool(descriptionError <> "")
              "recordDisabled", VBool(Result.isError validation)
              "notice", VString state.Notice
              "hasNotice", VBool(state.Notice <> "")
              "activities", VItems activityItems
              "hasActivities", VBool(not (List.isEmpty state.Activities))
              "isEmpty", VBool(List.isEmpty state.Activities)
              "summary", VString $"{state.Activities.Length} recorded activit{if state.Activities.Length = 1 then "y" else "ies"}" ]

    let apply state event =
        match Engine.transition state (eventToCommand event) with
        | Ok next -> next
        | Error error -> { state with Notice = errorText state.Draft error }
