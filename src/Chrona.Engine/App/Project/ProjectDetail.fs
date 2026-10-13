/// The activity detail: its record, history, corrections and evidence.
module Chrona.Engine.App.ProjectDetail

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

let internal evidenceKinds =
    [ "url", "Link"
      "github-commit", "GitHub commit"
      "pull-request", "Pull request"
      "issue", "Issue"
      "document", "Document"
      "calendar-event", "Calendar event"
      "screenshot", "Screenshot"
      "linkedin-post", "LinkedIn post"
      "other", "Other" ]

let internal kindLabel (kind: string) =
    evidenceKinds |> List.tryFind (fst >> (=) kind) |> Option.map snd |> Option.defaultValue kind

let internal commandLabel =
    function
    | "create" -> "Recorded"
    | "amend" -> "Amended"
    | "void" -> "Removed from totals"
    | "restore" -> "Restored"
    | "split" -> "Split"
    | "merge" -> "Merged"
    | "evidence-link" -> "Evidence linked"
    | "evidence-unlink" -> "Evidence unlinked"
    | "accept-outside-edit" -> "Outside change accepted"
    | other -> other

let internal detailView (model: Model) =
    let found =
        model.Detail
        |> Option.bind (fun d -> model.Ledger.Activities.TryFind d.ActivityId |> Option.map (fun a -> d, a))

    let blank =
        [ "detailTitle"; "detailClassification"; "detailTimes"; "detailDate"; "detailDuration"; "detailBilled"; "detailMethod"; "detailState"
          "detailRevision"; "detailPurpose"; "detailReason"; "detailTags"; "detailLineage"; "detailReplacedBy"; "amendDescription"; "amendPurpose"
          "amendReason"; "voidReason"; "splitFirst"; "splitSecond"; "splitTotal"; "evidenceUrl"; "evidenceLabel"; "evidenceSource"; "evidenceNotes" ]

    match found with
    | None ->
        [ flag "detailFound" false
          // Its record may still arrive (the records are opening, or its month is
          // being read). A record that does not exist is the not-found page.
          flag "detailLoading" (match model.Place with Places.Entry _ -> model.RouteProblem.IsNone | _ -> false)
          flag "detailRecorded" false
          flag "detailVoided" false
          flag "detailSuperseded" false
          flag "detailCanSplit" false
          flag "detailHasEvidence" false
          yield! blank |> List.map (fun key -> text key "")
          items "amendTypeOptions" []
          items "amendProjectOptions" []
          items "detailEvidence" []
          items "splitEvidence" []
          items "evidenceKindOptions" []
          items "detailHistory" []
          text "detailHistoryNote" ""
          flag "hasAmendProblems" false
          items "amendProblems" []
          flag "hasVoidProblems" false
          items "voidProblems" []
          flag "hasSplitProblems" false
          items "splitProblems" []
          flag "hasEvidenceProblems" false
          items "evidenceProblems" [] ]
    | Some(d, a) ->
        let detailMissing = flag "detailMissing" false

        let recorded, voided, superseded =
            match a.Record with
            | Recorded -> true, false, false
            | Voided _ -> false, true, false
            | Superseded _ -> false, false, true

        let times =
            match interval a with
            | Some(start, finish) -> $"{Format.clock start} – {Format.clock finish}"
            | None -> "No clock times"

        let lineage =
            match a.Lineage with
            | [] -> ""
            | [ source ] when source.StartsWith "TMR" -> "From a timer."
            | sources ->
                let names =
                    sources
                    |> List.map (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun s -> quoted s.Classification.Description) |> Option.defaultValue id)

                $"From {commaList names}."

        let replacedBy =
            match a.Record with
            | Superseded children ->
                let names =
                    children
                    |> List.map (fun id -> model.Ledger.Activities.TryFind id |> Option.map (fun c -> $"{quoted c.Classification.Description} ({Format.minutes c.Minutes})") |> Option.defaultValue id)

                $"Replaced by {commaList names}. It no longer counts toward totals."
            | _ -> ""

        let history =
            // Newest first, in the order the record's revisions were made:
            // the same whether the trail was kept in this page or read back.
            let revisionOf (entry: Ledger.AuditEntry) =
                entry.ResultingRevisions |> List.tryFind (fst >> (=) a.ActivityId) |> Option.map snd |> Option.defaultValue 0

            model.Ledger.Audit
            |> List.filter (fun entry -> List.contains a.ActivityId entry.ActivityIds)
            |> List.sortByDescending (fun entry -> entry.At, revisionOf entry)
            |> List.map (fun entry ->
                let index = $"{entry.At.UtcTicks}-{entry.Command}-{revisionOf entry}"
                let revisions =
                    match entry.PriorRevisions |> List.tryFind (fst >> (=) a.ActivityId), entry.ResultingRevisions |> List.tryFind (fst >> (=) a.ActivityId) with
                    | Some(_, before), Some(_, after) -> $"Revision {before} → {after}"
                    | None, Some(_, after) -> $"Revision {after}"
                    | _ -> ""

                [ t "id" $"{index}"
                  t "action" (commandLabel entry.Command)
                  t "at" (localStamp model entry.At)
                  t "revisions" revisions
                  t "reason" (entry.Reason |> Option.filter (String.IsNullOrWhiteSpace >> not) |> Option.map (fun r -> $"Reason: {r}") |> Option.defaultValue "") ])

        [ flag "detailFound" true
          detailMissing
          flag "detailRecorded" recorded
          flag "detailVoided" voided
          flag "detailSuperseded" superseded
          flag "detailCanSplit" (recorded && a.Minutes >= 2)
          flag "detailHasEvidence" (not a.Evidence.IsEmpty)
          text "detailTitle" a.Classification.Description
          text "detailClassification" $"{referenceName model Reference.ActivityType a.Classification.ActivityTypeId} · {referenceName model Reference.Project a.Classification.ProjectId}"
          text "detailTimes" times
          text "detailDate" (Format.longDate a.Occurrence.LocalDate)
          text "detailDuration" (Format.minutes a.Minutes)
          text "detailBilled" $"{Format.minutes (Billing.billableMinutes (billing model) a.Minutes)} billed"
          text "detailMethod" (methodLabel a.EntryMethod)
          text "detailState" (match a.Record with Recorded -> "Recorded" | Voided _ -> "Removed from totals" | Superseded _ -> "Superseded")
          text "detailRevision" (string a.Revision)
          text "detailPurpose" a.Classification.BusinessPurpose
          text "detailReason" (a.Reason |> Option.defaultValue "None")
          text "detailTags" (match a.Classification.Tags with [] -> "None" | tags -> tags |> List.map (referenceName model Reference.Tag) |> String.concat ", ")
          text "detailLineage" lineage
          text "detailReplacedBy" replacedBy
          items "amendTypeOptions" (options model Reference.ActivityType d.Amend.ActivityTypeId @ (if Reference.selectable Reference.ActivityType model.References |> List.exists (fun i -> i.Id = d.Amend.ActivityTypeId) then [] else [ [ t "id" d.Amend.ActivityTypeId; t "name" $"{referenceName model Reference.ActivityType d.Amend.ActivityTypeId} (archived)"; f "selected" true ] ]))
          items "amendProjectOptions" (options model Reference.Project d.Amend.ProjectId @ (if Reference.selectable Reference.Project model.References |> List.exists (fun i -> i.Id = d.Amend.ProjectId) then [] else [ [ t "id" d.Amend.ProjectId; t "name" $"{referenceName model Reference.Project d.Amend.ProjectId} (archived)"; f "selected" true ] ]))
          text "amendDescription" d.Amend.Description
          text "amendPurpose" d.Amend.BusinessPurpose
          text "amendReason" d.AmendReason
          text "voidReason" d.VoidReason
          text "splitFirst" d.SplitFirst
          text "splitSecond" d.SplitSecond
          text "splitTotal" $"{Format.minutes a.Minutes} to share: the parts must add up to exactly {a.Minutes} minutes."
          items
              "detailEvidence"
              (a.Evidence
               |> List.map (fun e ->
                   [ t "id" e.Id
                     t "label" e.Label
                     t "kind" (kindLabel e.Kind)
                     t "url" e.Url
                     f "hasUrl" (e.Url <> "")
                     t "captured" (localStamp model e.CapturedAt)
                     t "source" (e.Source |> Option.map (fun source -> $"Source: {source}") |> Option.defaultValue "")
                     t "notes" (e.Notes |> Option.defaultValue "") ]))
          items
              "splitEvidence"
              (a.Evidence
               |> List.map (fun e ->
                   let part = d.SplitEvidence.TryFind e.Id |> Option.defaultValue 0
                   [ t "id" e.Id; t "label" e.Label; f "toFirst" (part = 1); f "toSecond" (part = 2); f "toNeither" (part = 0) ]))
          items "evidenceKindOptions" (evidenceKinds |> List.map (fun (id, name) -> [ t "id" id; t "name" name; f "selected" (id = d.EvidenceKind) ]))
          text "evidenceUrl" d.EvidenceUrl
          text "evidenceLabel" d.EvidenceLabel
          text "evidenceSource" d.EvidenceSource
          text "evidenceNotes" d.EvidenceNotes
          items "detailHistory" history
          // Revisions made before the audit trail was stored have no entry (WI-0056).
          text
              "detailHistoryNote"
              (let covered =
                  model.Ledger.Audit
                  |> List.collect _.ResultingRevisions
                  |> List.filter (fst >> (=) a.ActivityId)
                  |> List.map snd
                  |> Set.ofList

               if [ 1 .. a.Revision ] |> List.forall covered.Contains then
                   ""
               else
                   "Some earlier revisions were made before Chrona kept its history with the records; Git history is the only account of them.")
          flag "hasAmendProblems" (model.Problems.ContainsKey AmendForm)
          items "amendProblems" (problemItems model AmendForm)
          flag "hasVoidProblems" (model.Problems.ContainsKey VoidForm)
          items "voidProblems" (problemItems model VoidForm)
          flag "hasSplitProblems" (model.Problems.ContainsKey SplitForm)
          items "splitProblems" (problemItems model SplitForm)
          flag "hasEvidenceProblems" (model.Problems.ContainsKey EvidenceForm)
          items "evidenceProblems" (problemItems model EvidenceForm) ]
