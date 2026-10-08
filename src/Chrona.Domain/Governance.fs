/// Who may set an organization up and administer it first (requirements
/// expansion 3; WI-0053).
///
/// The deployment's configuration is the root of trust: each organization
/// lists its bootstrap administrators by GitHub numeric account id. Only a
/// listed account initializes an organization or becomes its first
/// administrator; opening an organization first grants nothing. An
/// organization whose roster has no administrator who is also listed (one
/// set up before this rule) is held until a listed account confirms it.
/// First-opener founding remains only in an explicitly local environment
/// that lists no one.
///
/// Pure.
module Chrona.Domain.Governance

open Arca

/// What opening an organization may do, for the person opening it.
type Founding =
    /// The roster has a listed administrator: work as the roster says.
    | Proceed
    /// Set the organization up (when new) and make this person its first
    /// administrator.
    | Found
    /// The roster has no listed administrator. Nothing is granted; a listed
    /// account may confirm itself as administrator.
    | NeedsConfirmation of canConfirm: bool
    /// The organization cannot be set up by this person, and why.
    | Refused of reason: string

/// Whether this deployment keeps first-opener founding: only an explicitly
/// local environment that lists no administrators.
let firstOpener (environment: EnvironmentKind) (organization: Deployment.OrganizationConfig) =
    environment = EnvironmentKind.Local && organization.Administrators.IsEmpty

/// The roster's administrators who are also listed in the configuration.
let listedAdministrators (organization: Deployment.OrganizationConfig) (roster: Access.Roster) =
    roster.Members
    |> Map.toList
    |> List.filter (fun (id, membership) ->
        membership.Capabilities.Contains Access.ManageOrganizationSettings
        && Deployment.isBootstrapAdministrator organization id)
    |> List.map fst

/// What opening the organization may do. `isNew` is whether its folder has
/// not been set up yet.
let decide
    (environment: EnvironmentKind)
    (organization: Deployment.OrganizationConfig)
    (isNew: bool)
    (roster: Access.Roster)
    (actorId: string)
    : Founding =
    let listed = Deployment.isBootstrapAdministrator organization actorId

    match isNew with
    | true when listed || firstOpener environment organization -> Found
    | true when organization.Administrators.IsEmpty && environment = EnvironmentKind.Production ->
        Refused
            $"{organization.DisplayName} has no administrators in this deployment's configuration, so it cannot be set up. Production organizations are set up only by the GitHub accounts the configuration lists."
    | true when organization.Administrators.IsEmpty ->
        Refused $"{organization.DisplayName} has no administrators in this deployment's configuration, so it cannot be set up."
    | true -> Refused $"Only {organization.DisplayName}'s listed administrators can set it up."
    | false when firstOpener environment organization -> if roster.Members.IsEmpty then Found else Proceed
    | false when not (listedAdministrators organization roster).IsEmpty -> Proceed
    | false -> NeedsConfirmation listed

/// The membership a confirmed or founding administrator holds: everything
/// an administrator may hold, at the next revision of what was stored.
let administrator (principal: Access.Principal) (roster: Access.Roster) : Access.Membership =
    { Principal = principal
      Capabilities = Access.Grants.forKind principal.Kind Access.Grants.administrator
      Revision =
        roster.Members.TryFind principal.PrincipalId
        |> Option.map (fun found -> found.Revision + 1)
        |> Option.defaultValue 1 }
