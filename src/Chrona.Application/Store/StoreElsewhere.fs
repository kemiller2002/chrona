/// The deployment's other organizations, read for a report across
/// organizations (WI-0075).
module internal Chrona.Application.StoreElsewhere

open System
open System.Collections.Generic
open Arca
open Arca.GitHub
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Application.Bridge
open Chrona.Application.Store
open Chrona.Application.StoreState
open Chrona.Application.StoreUnsentQueue
open Chrona.Application.StoreIndex
open Chrona.Application.StorePage
open Chrona.Application.StoreReads

/// One of the deployment's other organizations, for a report across
/// organizations (WI-0075): its own folder, read at the state observed,
/// as this person may see it. Nothing of it is read unless they are one
/// of its members; everyone's time only where its roster lets them see
/// the organization's, and otherwise only their own. Every month of the
/// range is read for each of its members and for anyone its index
/// records time for, so nothing stored is left out.
let readElsewhere (page: Page) (config: Deployment.DeploymentConfig) (session: Session) (organizationId: string) (everyone: bool) (dates: DateOnly list) =
    async {
        let displayName =
            Deployment.organization config organizationId |> Option.map _.DisplayName |> Option.defaultValue organizationId

        let months = dates |> List.map monthOf |> List.distinct |> List.sort

        let empty: Elsewhere =
            { OrganizationId = organizationId
              DisplayName = displayName
              Member = false
              SeesOrganization = false
              Activities = []
              References = []
              Members = []
              Everyone = everyone
              Months = months
              Failure = None }

        let failed (reason: string) = { empty with Failure = Some reason }

        match Storage.binding config with
        | Error diagnostic -> return failed (code diagnostic)
        | Ok binding ->
            match Storage.organizationNamespace config binding organizationId with
            | Error diagnostic -> return failed (code diagnostic)
            | Ok folder ->
                let connection = page.Backend.Connect folder.Location

                match! connection.Provider.NamespaceState folder with
                | Error failure -> return failed (describeFailure failure)
                | Ok observed ->
                    let pinned = pinnedAt connection observed.RepositoryToken

                    match! readFolders page.Bridge pinned folder (MemberRecord.folder () |> Result.map List.singleton) with
                    | Error reason -> return failed (reasonOf reason)
                    | Ok(folders, _) ->
                        let roster: Access.Roster =
                            { OrganizationId = organizationId
                              Members =
                                (Stored.load (folders |> List.collect snd)).Members
                                |> Map.toList
                                |> List.map (fun (_, found) -> found.Membership.Principal.PrincipalId, found.Membership)
                                |> Map.ofList }

                        if not (roster.Members.ContainsKey session.ActorId) then
                            return empty
                        else
                            let sees = Access.permits roster session.ActorId Access.ViewOrganizationTime

                            let! indexed =
                                async {
                                    match! Derived.read pinned folder ActivityIndex.definition with
                                    | Ok(Some(index, _)) -> return ActivityIndex.totals index |> List.map _.ActorId
                                    | _ -> return []
                                }

                            let others =
                                if everyone && sees then
                                    (roster.Members |> Map.toList |> List.map fst) @ indexed
                                    |> List.distinct
                                    |> List.filter ((<>) session.ActorId)
                                    |> List.collect (fun other -> months |> List.map (fun month -> other, month))
                                    |> Set.ofList
                                else
                                    Set.empty

                            match! load page connection folder session.ActorId (Set.ofList months) others Map.empty with
                            | Error reason -> return failed (reasonOf reason)
                            | Ok(_, stored, _, _) ->
                                let activities =
                                    stored.Activities.Activities
                                    |> Map.toList
                                    |> List.map (fun (_, found) -> found.Activity)
                                    |> List.filter (fun activity -> (everyone && sees) || activity.ActorId = session.ActorId)

                                return
                                    { empty with
                                        Member = true
                                        SeesOrganization = sees
                                        Activities = activities
                                        References = stored.References |> Map.toList |> List.map (fun (_, found) -> found.Item)
                                        Members = roster.Members |> Map.toList |> List.map snd }
    }

/// Reads the other organizations a report across organizations asked
/// for, one after another (WI-0075).
let readElsewhereJob (page: Page) (wanted: (string * bool * DateOnly list) list) () =
    async {
        match page.LastOpen, page.Opened with
        | Some(config, _, _), Some state ->
            let mutable read = []

            for organizationId, everyone, dates in wanted do
                let! one = readElsewhere page config state.Session organizationId everyone dates
                read <- read @ [ one ]

            return [ Update.ElsewhereRead read ]
        | _ -> return []
    }
