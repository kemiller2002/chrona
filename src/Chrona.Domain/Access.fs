/// Principals, organization membership and capability-based authorization
/// (requirements expansion 3, 26).
///
/// Authentication says who someone is (Fides: GitHub's identity). This
/// module says what they may do in an organization, and it is separate:
/// every check is "does this principal hold this capability in this
/// organization", never a role name. Roles are only templates that produce
/// capability sets (`Grants`). A person may belong to several
/// organizations, each with its own roster.
///
/// Pure.
module Chrona.Domain.Access

open Chrona.Domain.Diagnostics

/// What kind of principal acts (3). An agent is never recorded as a person.
type PrincipalKind =
    | Human
    | Agent
    | Service
    | Integration

type Principal =
    { PrincipalId: string
      Kind: PrincipalKind
      DisplayName: string }

/// What a principal may do in an organization (3).
type Capability =
    | RecordOwnTime
    | AmendOwnTime
    | SubmitOwnTime
    | AttestOwnDay
    | ViewOwnTime
    | ViewOrganizationTime
    | ApproveTime
    | RejectTime
    | ReopenTime
    | ManageProjects
    | ManageActivityTypes
    | ManageTags
    | ManageOrganizationSettings
    | ExportTime
    | PublishBillableTime

let allCapabilities =
    [ RecordOwnTime
      AmendOwnTime
      SubmitOwnTime
      AttestOwnDay
      ViewOwnTime
      ViewOrganizationTime
      ApproveTime
      RejectTime
      ReopenTime
      ManageProjects
      ManageActivityTypes
      ManageTags
      ManageOrganizationSettings
      ExportTime
      PublishBillableTime ]

/// The capability's stable name, as diagnostics and records state it.
let capabilityName (capability: Capability) =
    match capability with
    | RecordOwnTime -> "RecordOwnTime"
    | AmendOwnTime -> "AmendOwnTime"
    | SubmitOwnTime -> "SubmitOwnTime"
    | AttestOwnDay -> "AttestOwnDay"
    | ViewOwnTime -> "ViewOwnTime"
    | ViewOrganizationTime -> "ViewOrganizationTime"
    | ApproveTime -> "ApproveTime"
    | RejectTime -> "RejectTime"
    | ReopenTime -> "ReopenTime"
    | ManageProjects -> "ManageProjects"
    | ManageActivityTypes -> "ManageActivityTypes"
    | ManageTags -> "ManageTags"
    | ManageOrganizationSettings -> "ManageOrganizationSettings"
    | ExportTime -> "ExportTime"
    | PublishBillableTime -> "PublishBillableTime"

let kindName =
    function
    | Human -> "Human"
    | Agent -> "Agent"
    | Service -> "Service"
    | Integration -> "Integration"

/// Statements only a person can make: attesting their own day, and
/// approving or rejecting someone's time. An agent, service or integration
/// may record and publish, but never vouch.
let personOnly = set [ AttestOwnDay; ApproveTime; RejectTime ]

/// Whether a principal of this kind may hold the capability at all.
let permitsKind (kind: PrincipalKind) (capability: Capability) =
    kind = Human || not (personOnly.Contains capability)

/// Templates that produce capability sets. They are conveniences for
/// granting, never checked by name.
module Grants =
    /// Keeping one's own time.
    let ownTime = set [ RecordOwnTime; AmendOwnTime; SubmitOwnTime; AttestOwnDay; ViewOwnTime; ExportTime ]

    /// Reviewing the organization's time.
    let reviewer = ownTime + set [ ViewOrganizationTime; ApproveTime; RejectTime; ReopenTime ]

    /// Everything, including the organization's shared reference data and settings.
    let administrator = Set.ofList allCapabilities

    /// What the template allows a principal of this kind.
    let forKind (kind: PrincipalKind) (grant: Set<Capability>) = grant |> Set.filter (permitsKind kind)

/// One principal's membership of one organization.
type Membership =
    { Principal: Principal
      Capabilities: Set<Capability>
      /// Optimistic-concurrency revision, starting at 1.
      Revision: int }

/// An organization's members, by principal id.
type Roster =
    { OrganizationId: string
      Members: Map<string, Membership> }

/// The roster of an organization someone has just founded: they administer
/// it, so it is never without someone who can manage it.
let founded (organizationId: string) (founder: Principal) =
    { OrganizationId = organizationId
      Members =
        Map.ofList
            [ founder.PrincipalId,
              { Principal = founder
                Capabilities = Grants.forKind founder.Kind Grants.administrator
                Revision = 1 } ] }

/// What the principal may do in the roster's organization; empty for a non-member.
let capabilitiesOf (roster: Roster) (principalId: string) =
    roster.Members.TryFind principalId
    |> Option.map _.Capabilities
    |> Option.defaultValue Set.empty

/// Ok when the principal holds the capability in the organization; otherwise
/// the stable reason (26): another organization's roster, not a member, or
/// the capability is not held.
let authorize (roster: Roster) (organizationId: string) (principalId: string) (capability: Capability) : Result<unit, Diagnostic> =
    if roster.OrganizationId <> organizationId then
        Error OrganizationMismatch
    else
        match roster.Members.TryFind principalId with
        | None -> Error(NotAMember(principalId, organizationId))
        | Some membership when membership.Capabilities.Contains capability -> Ok()
        | Some _ -> Error(UnauthorizedCapability(capabilityName capability))

/// Whether the principal holds the capability.
let permits (roster: Roster) (principalId: string) (capability: Capability) =
    authorize roster roster.OrganizationId principalId capability |> Result.isOk

/// The organizations a principal belongs to, across rosters (3).
let organizationsOf (rosters: Roster list) (principalId: string) =
    rosters
    |> List.filter (fun roster -> roster.Members.ContainsKey principalId)
    |> List.map _.OrganizationId

/// The capability for its stable name.
let capabilityOf (name: string) =
    allCapabilities |> List.tryFind (fun capability -> capabilityName capability = name)

let kindOf (name: string) =
    [ Human; Agent; Service; Integration ] |> List.tryFind (fun kind -> kindName kind = name)

/// Changes to a roster. Each needs ManageOrganizationSettings.
type RosterCommand =
    | Admit of Principal * Set<Capability>
    | Grant of principalId: string * Capability
    | Revoke of principalId: string * Capability
    | Remove of principalId: string

let private administrators (roster: Roster) =
    roster.Members |> Map.filter (fun _ m -> m.Capabilities.Contains ManageOrganizationSettings) |> Map.count

let private kindProblems (principal: Principal) (capabilities: Set<Capability>) =
    capabilities
    |> Set.toList
    |> List.filter (permitsKind principal.Kind >> not)
    |> List.map (fun capability -> CapabilityNotForKind(capabilityName capability, kindName principal.Kind))

let private member' (roster: Roster) (principalId: string) =
    match roster.Members.TryFind principalId with
    | Some found -> Ok found
    | None -> Error [ NotAMember(principalId, roster.OrganizationId) ]

/// Applies a roster change made by `performer`: the next roster, or every
/// reason it is refused. The organization always keeps someone who can
/// manage it, and person-only capabilities go only to people.
let execute (performer: string) (command: RosterCommand) (roster: Roster) : Result<Roster, Diagnostic list> =
    let keepsAnAdministrator (next: Roster) =
        if administrators next = 0 then Error [ LastAdministrator roster.OrganizationId ] else Ok next

    authorize roster roster.OrganizationId performer ManageOrganizationSettings
    |> Result.mapError List.singleton
    |> Result.bind (fun () ->
        match command with
        | Admit(principal, capabilities) ->
            if roster.Members.ContainsKey principal.PrincipalId then
                Error [ AlreadyAMember(principal.PrincipalId, roster.OrganizationId) ]
            else
                match kindProblems principal capabilities with
                | [] ->
                    Ok
                        { roster with
                            Members =
                                roster.Members.Add(
                                    principal.PrincipalId,
                                    { Principal = principal
                                      Capabilities = capabilities
                                      Revision = 1 }
                                ) }
                | problems -> Error problems
        | Grant(principalId, capability) ->
            member' roster principalId
            |> Result.bind (fun found ->
                match kindProblems found.Principal (set [ capability ]) with
                | [] ->
                    Ok
                        { roster with
                            Members =
                                roster.Members.Add(
                                    principalId,
                                    { found with
                                        Capabilities = found.Capabilities.Add capability
                                        Revision = found.Revision + 1 }
                                ) }
                | problems -> Error problems)
        | Revoke(principalId, capability) ->
            member' roster principalId
            |> Result.map (fun found ->
                { roster with
                    Members =
                        roster.Members.Add(
                            principalId,
                            { found with
                                Capabilities = found.Capabilities.Remove capability
                                Revision = found.Revision + 1 }
                        ) })
            |> Result.bind keepsAnAdministrator
        | Remove principalId ->
            member' roster principalId
            |> Result.map (fun _ -> { roster with Members = roster.Members.Remove principalId })
            |> Result.bind keepsAnAdministrator)

/// The roster changes that take a member from `held` to `wanted`, grants first.
let changesTo (principalId: string) (held: Set<Capability>) (wanted: Set<Capability>) =
    (Set.difference wanted held |> Set.toList |> List.map (fun capability -> Grant(principalId, capability)))
    @ (Set.difference held wanted |> Set.toList |> List.map (fun capability -> Revoke(principalId, capability)))

/// Applies roster changes in order, all or none.
let executeAll (performer: string) (commands: RosterCommand list) (roster: Roster) =
    commands |> List.fold (fun state command -> state |> Result.bind (execute performer command)) (Ok roster)
