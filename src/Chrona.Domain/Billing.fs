/// Exact time versus billable time (requirements expansion 7, 8).
///
/// Billing is a projection: it never changes an activity's exact minutes.
/// Policies are explicit and versioned, scoped to an organization, client,
/// engagement or project, and effective from a date. The legacy default is
/// six-minute upward rounding. The policy identity and version travel with
/// every projection, so published billable time names the rule that made it.
module Chrona.Domain.Billing

open System
open Chrona.Domain.Diagnostics
open Chrona.Domain.Activity

type Rounding =
    | Up
    | Down
    /// Half or more of an increment rounds up.
    | Nearest
    | Exact

type Scope =
    | OrganizationScope of string
    | ClientScope of string
    | EngagementScope of string
    | ProjectScope of string

type BillingPolicy =
    { PolicyId: string
      Version: int
      Scope: Scope
      IncrementMinutes: int
      Rounding: Rounding
      EffectiveFrom: DateOnly }

/// The legacy default (7): six-minute increments, rounded up.
let legacyDefault (organizationId: string) =
    { PolicyId = "legacy-six-minute-up"
      Version = 1
      Scope = OrganizationScope organizationId
      IncrementMinutes = 6
      Rounding = Up
      EffectiveFrom = DateOnly.MinValue }

let private specificity =
    function
    | ProjectScope _ -> 4
    | EngagementScope _ -> 3
    | ClientScope _ -> 2
    | OrganizationScope _ -> 1

let private applies (activity: Activity) =
    function
    | OrganizationScope id -> id = activity.OrganizationId
    | ClientScope id -> activity.Classification.ClientId = Some id
    | EngagementScope id -> activity.Classification.EngagementId = Some id
    | ProjectScope id -> activity.Classification.ProjectId = id

/// The policy for an activity: the most specific applicable scope, then the
/// latest version effective on the activity's business date.
let resolve (policies: BillingPolicy list) (activity: Activity) : Result<BillingPolicy, Diagnostic> =
    policies
    |> List.filter (fun p -> applies activity p.Scope && p.EffectiveFrom <= activity.Occurrence.LocalDate)
    |> List.sortByDescending (fun p -> specificity p.Scope, p.EffectiveFrom, p.Version)
    |> List.tryHead
    |> function
        | Some policy -> Ok policy
        | None -> Error BillingPolicyNotFound

/// Billable minutes under a policy. Exact minutes are an input, never an
/// output: the activity is not changed.
let billableMinutes (policy: BillingPolicy) (exact: int) =
    let increment = max 1 policy.IncrementMinutes
    let whole = exact / increment
    let remainder = exact % increment

    match policy.Rounding with
    | Exact -> exact
    | Down -> whole * increment
    | Up -> (if remainder = 0 then whole else whole + 1) * increment
    | Nearest -> (if 2 * remainder >= increment then whole + 1 else whole) * increment

/// One activity's billing projection, naming the policy and the revision.
type Projection =
    { ActivityId: string
      Revision: int
      ExactMinutes: int
      BillableMinutes: int
      PolicyId: string
      PolicyVersion: int }

let project (policies: BillingPolicy list) (activity: Activity) : Result<Projection, Diagnostic> =
    resolve policies activity
    |> Result.map (fun policy ->
        { ActivityId = activity.ActivityId
          Revision = activity.Revision
          ExactMinutes = activity.Minutes
          BillableMinutes = billableMinutes policy activity.Minutes
          PolicyId = policy.PolicyId
          PolicyVersion = policy.Version })
