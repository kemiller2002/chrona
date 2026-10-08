/// Reference data (requirements expansion 4): the organization-scoped
/// clients, projects, engagements, activity types and tags that activities
/// are classified by.
///
/// Every item has a stable, immutable id and is Active or Archived. Archived
/// items stay valid on the records that already use them but are not offered
/// for new work. Chrona does not assume ownership of shared concepts: an item
/// another system owns (a client Summa owns, say) is a mirror, identified by
/// that system's stable id, and changes only when its owner says so.
///
/// Assignment follows the legacy rule (DOMAIN-REQUIREMENTS "Amending an
/// activity"): a reference newly assigned to an activity must exist and be
/// active; one the activity already carried remains valid after it is
/// archived. Deactivation blocks new assignment, not historical reference.
module Chrona.Domain.Reference

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Activity

type Kind =
    | Client
    | Project
    | Engagement
    | ActivityType
    | Tag

let kindName =
    function
    | Client -> "client"
    | Project -> "project"
    | Engagement -> "engagement"
    | ActivityType -> "activityType"
    | Tag -> "tag"

type Status =
    | Active
    | Archived

/// Who owns the concept.
type Ownership =
    | Owned
    /// Mirrored from the system that owns it, by that system's stable id.
    | MirroredFrom of system: string

type Item =
    { Kind: Kind
      Id: string
      Name: string
      Status: Status
      Ownership: Ownership
      Revision: int }

/// One organization's reference data.
type Catalogue =
    { OrganizationId: string
      Items: Map<Kind * string, Item> }

let empty (organizationId: string) =
    { OrganizationId = organizationId
      Items = Map.empty }

type Command =
    | Add of kind: Kind * id: string * name: string
    | Rename of kind: Kind * id: string * expectedRevision: int * name: string
    | Archive of kind: Kind * id: string * expectedRevision: int
    | Reactivate of kind: Kind * id: string * expectedRevision: int
    /// The owning system's current statement of a mirrored item: added the
    /// first time, then replaced wholesale.
    | Mirror of system: string * kind: Kind * id: string * name: string * status: Status

let private blank (text: string) = String.IsNullOrWhiteSpace text

let private find (catalogue: Catalogue) kind id expected =
    match catalogue.Items.TryFind(kind, id) with
    | None -> Error [ UnknownReference(kindName kind, id) ]
    | Some item when item.Revision <> expected -> Error [ RevisionConflict(expected, item.Revision) ]
    | Some { Ownership = MirroredFrom system } -> Error [ ReferenceOwnedElsewhere(kindName kind, id, system) ]
    | Some item -> Ok item

let private named (name: string) =
    if blank name then Error [ MissingField "name" ] else Ok(name.Trim())

let private put (catalogue: Catalogue) (item: Item) =
    { catalogue with Items = catalogue.Items.Add((item.Kind, item.Id), item) }

/// Applies one command: the next catalogue, or every reason it was refused.
let execute (catalogue: Catalogue) (command: Command) : Result<Catalogue, Diagnostic list> =
    let bumped (item: Item) = { item with Revision = item.Revision + 1 }

    match command with
    | Add(kind, id, name) ->
        let problems =
            [ if blank id then MissingField "id"
              if blank name then MissingField "name"
              if catalogue.Items.ContainsKey(kind, id) then DuplicateReference(kindName kind, id) ]

        if problems.IsEmpty then
            Ok(put catalogue { Kind = kind; Id = id; Name = name.Trim(); Status = Active; Ownership = Owned; Revision = 1 })
        else
            Error problems
    | Rename(kind, id, expected, name) ->
        find catalogue kind id expected
        |> Result.bind (fun item -> named name |> Result.map (fun name -> put catalogue { bumped item with Name = name }))
    | Archive(kind, id, expected) ->
        find catalogue kind id expected
        |> Result.bind (fun item ->
            match item.Status with
            | Active -> Ok(put catalogue { bumped item with Status = Archived })
            | Archived -> Error [ IllegalTransition("Archived", "archive") ])
    | Reactivate(kind, id, expected) ->
        find catalogue kind id expected
        |> Result.bind (fun item ->
            match item.Status with
            | Archived -> Ok(put catalogue { bumped item with Status = Active })
            | Active -> Error [ IllegalTransition("Active", "reactivate") ])
    | Mirror(system, kind, id, name, status) ->
        match catalogue.Items.TryFind(kind, id) with
        | Some { Ownership = Owned } -> Error [ ReferenceOwnedElsewhere(kindName kind, id, "Chrona") ]
        | existing ->
            named name
            |> Result.map (fun name ->
                let revision = existing |> Option.map (fun i -> i.Revision + 1) |> Option.defaultValue 1

                put catalogue { Kind = kind; Id = id; Name = name; Status = status; Ownership = MirroredFrom system; Revision = revision })

/// The items offered for new work: active only, by name.
let selectable (kind: Kind) (catalogue: Catalogue) =
    catalogue.Items
    |> Map.toList
    |> List.map snd
    |> List.filter (fun item -> item.Kind = kind && item.Status = Active)
    |> List.sortBy (fun item -> item.Name.ToLowerInvariant(), item.Id)

/// Every item of a kind, archived included (administration and history).
let all (kind: Kind) (catalogue: Catalogue) =
    catalogue.Items
    |> Map.toList
    |> List.map snd
    |> List.filter (fun item -> item.Kind = kind)
    |> List.sortBy (fun item -> item.Name.ToLowerInvariant(), item.Id)

let tryName (kind: Kind) (id: string) (catalogue: Catalogue) =
    catalogue.Items.TryFind(kind, id) |> Option.map _.Name

/// The references a classification names, by kind.
let private references (c: Classification) =
    [ yield Project, c.ProjectId
      yield ActivityType, c.ActivityTypeId
      match c.ClientId with
      | Some id -> yield Client, id
      | None -> ()
      match c.EngagementId with
      | Some id -> yield Engagement, id
      | None -> ()
      for tag in c.Tags do
          yield Tag, tag ]
    |> List.filter (snd >> blank >> not)

/// Every reference problem with assigning `next`, given what the activity
/// already carried (`previous`, or empty for new work): each newly assigned
/// reference must exist and be active. A blank required reference is a
/// classification problem, reported by `Activity.classificationProblems`.
let assignmentProblems (catalogue: Catalogue) (previous: Classification list) (next: Classification) : Diagnostic list =
    let carried = previous |> List.collect references |> Set.ofList

    references next
    |> List.distinct
    |> List.filter (fun reference -> not (carried.Contains reference))
    |> List.choose (fun (kind, id) ->
        match catalogue.Items.TryFind(kind, id) with
        | None -> Some(UnknownReference(kindName kind, id))
        | Some { Status = Archived } -> Some(ArchivedReference(kindName kind, id))
        | Some _ -> None)
