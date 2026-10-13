/// The organization's members (3): admitting people, changing their access
/// and removing them, stored as one commit each.
module Chrona.Engine.App.UpdateMembers

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

/// The capability set an access level grants a principal of this kind.
let accessGrant (access: string) =
    match access with
    | "administrator" -> Some Access.Grants.administrator
    | "reviewer" -> Some Access.Grants.reviewer
    | "ownTime" -> Some Access.Grants.ownTime
    | _ -> None

/// Stores a roster change made by the session's person: the changed and
/// removed memberships go to the store as one commit.
let internal rosterChange (ctx: Ctx) (commands: Access.RosterCommand list) (announcement: string) (model: Model) =
    match Access.executeAll model.Session.ActorId commands model.Roster with
    | Error problems -> withProblems MemberForm problems model, []
    | Ok roster ->
        // One command is one change of each member it touches: the next
        // revision of what was stored, however many capabilities moved.
        let changed =
            roster.Members
            |> Map.toList
            |> List.map snd
            |> List.filter (fun m -> model.Roster.Members.TryFind m.Principal.PrincipalId <> Some m)
            |> List.map (fun m ->
                match model.Roster.Members.TryFind m.Principal.PrincipalId with
                | Some before -> { m with Revision = before.Revision + 1 }
                | None -> { m with Revision = 1 })

        let roster =
            { roster with
                Members = changed |> List.fold (fun members m -> Map.add m.Principal.PrincipalId m members) roster.Members }

        let removed =
            model.Roster.Members |> Map.keys |> Seq.filter (roster.Members.ContainsKey >> not) |> List.ofSeq

        let request =
            { emptyRequest (ctx.NewId "COMMIT") with
                Members = changed
                RemovedMembers = removed }

        { clear MemberForm model with
            Roster = roster
            Announcement = announcement
            Store = { model.Store with Pending = model.Store.Pending @ [ request ] } },
        [ Store request ]

let internal admitMember (ctx: Ctx) (model: Model) =
    let draft = model.MemberDraft
    let id = draft.Id.Trim()
    let name = draft.Name.Trim()

    let problems =
        [ if not (id <> "" && id |> Seq.forall Char.IsAsciiDigit) then
              MissingField "memberId"
          if name = "" then MissingField "memberName" ]

    match problems, accessGrant draft.Access with
    | [], Some grant ->
        let principal: Access.Principal =
            { PrincipalId = $"github:{id}"
              Kind = Access.Human
              DisplayName = name }

        let admitted, effects = rosterChange ctx [ Access.Admit(principal, Access.Grants.forKind principal.Kind grant) ] $"{name} can now work here." model
        (if admitted.Problems.ContainsKey MemberForm then admitted else { admitted with MemberDraft = emptyMember }), effects
    | problems, _ -> withProblems MemberForm problems model, []

let internal changeMemberAccess (ctx: Ctx) (principalId: string) (access: string) (model: Model) =
    match model.Roster.Members.TryFind principalId, accessGrant access with
    | Some membership, Some grant ->
        let wanted = Access.Grants.forKind membership.Principal.Kind grant
        rosterChange ctx (Access.changesTo principalId membership.Capabilities wanted) $"{membership.Principal.DisplayName}'s access changed." model
    | None, _ -> withProblems MemberForm [ NotAMember(principalId, model.Roster.OrganizationId) ] model, []
    | _, None -> model, []

let internal removeMember (ctx: Ctx) (principalId: string) (model: Model) =
    let name = model.Roster.Members.TryFind principalId |> Option.map _.Principal.DisplayName |> Option.defaultValue principalId
    rosterChange ctx [ Access.Remove principalId ] $"{name} was removed." model
