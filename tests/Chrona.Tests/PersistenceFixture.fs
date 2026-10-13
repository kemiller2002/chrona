/// What the persistence tests share (WI-0051): the domain modules they use,
/// an organization's folder on Arca's in-memory provider, activities to
/// store, and reading months back.
module Chrona.Tests.PersistenceFixture

open System
open Xunit
open Arca
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Domain.Ledger

module Storage = Chrona.Domain.Storage
module ActivityRecord = Chrona.Domain.ActivityRecord
module ActivityRecordReading = Chrona.Domain.ActivityRecordReading
module Persistence = Chrona.Domain.Persistence
let ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let zone = tryZone "America/New_York" |> ok

let actor = "github:octocat"

/// 09:00 local (EDT) on a day of 2026.
let at (month: int) (day: int) (h: int) (m: int) = DateTimeOffset(2026, month, day, h + 4, m, 0, TimeSpan.Zero)

let context =
    { Performer = actor
      At = at 10 7 18 0
      Source = "chrona-web"
      Zone = zone
      References = references
      CorrelationId = Some "corr-1" }

let activity id (month, day) (startH, startM) minutes =
    let start = at month day startH startM

    { ActivityId = id
      OrganizationId = "ORG-1"
      ActorId = actor
      Occurrence = occurrence zone start
      Timing = Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification =
        { ProjectId = "PRJ-1"
          ClientId = None
          EngagementId = None
          ActivityTypeId = "ACT-DEV"
          Tags = []
          Description = "Work"
          BusinessPurpose = "Delivery" }
      EntryMethod = Manual
      Billability = Billable
      BillingReference = noBillingReference
      Record = Recorded
      Review = Unsubmitted
      Publication = Unpublished
      Revision = 1
      CreatedAt = at month day 8 0
      LastChangedAt = at month day 8 0
      Reason = None
      WorkItemRef = None
      ExternalRef = None
      Evidence = []
      Lineage = []
      Source = None }

let config =
    """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York"}]}"""
    |> Chrona.Domain.Deployment.parse
    |> ok

let ns = Storage.organizationNamespace config (Storage.binding config |> ok) "ORG-1" |> ok

let mutable sequence = 0

let operationContext () : Storage.OperationContext =
    sequence <- sequence + 1

    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:octocat" |> ok }
      ProviderIdentity = Some "octocat"
      CorrelationId = CorrelationId.create "corr-1" |> ok
      IdempotencyKey = IdempotencyKey.create $"op-persistence-{sequence:D6}" |> ok
      At = at 10 7 18 0 }

/// Reads the actor's month folders for these dates, as the application does.
let readMonths (dates: DateOnly list) (state: InMemoryState) =
    let folders = ActivityRecord.monthFolders actor dates |> ok

    let objects, problems =
        folders
        |> List.map (fun folder ->
            let listing = InMemory.list ns folder state |> fst

            match listing with
            | Error failure -> failwith $"%A{failure}"
            | Ok listing ->
                let files, problems = Persistence.recordFiles folder listing

                files
                |> List.choose (fun path ->
                    match InMemory.read ns path state |> fst |> ok with
                    | ReadOutcome.Found found -> Some found
                    | ReadOutcome.Absent
                    | ReadOutcome.Erased _ -> None),
                problems)
        |> List.unzip

    let snapshot = Persistence.load (List.concat objects)

    { snapshot with
        Problems = List.concat problems @ snapshot.Problems }

/// Executes a command, stores its result as one commit, and returns the next snapshot and store.
let store (command: Command) (snapshot: Persistence.Snapshot, state: InMemoryState) =
    let before = Persistence.ledgerOf snapshot
    let after = execute context before command |> ok
    let changed = Persistence.changedBetween before after
    let operation = Persistence.operation ns (operationContext ()) "record time" snapshot changed |> ok

    match InMemory.commit operation state with
    | Ok receipt, next -> Persistence.committed changed receipt snapshot, next
    | Error failure, _ -> failwith $"%A{failure}"

let october = DateOnly(2026, 10, 7)

let september = DateOnly(2026, 9, 30)

let storedContent path (state: InMemoryState) =
    match InMemory.read ns path state |> fst |> ok with
    | ReadOutcome.Found found -> found
    | ReadOutcome.Absent
    | ReadOutcome.Erased _ -> failwith "absent"

let codes (diagnostics: Diagnostic list) = diagnostics |> List.map code |> List.sort

module Access = Chrona.Domain.Access
module Reconcile = Chrona.Domain.Reconcile
module Stored = Chrona.Domain.Stored
module StoredChanges = Chrona.Domain.StoredChanges
module StoredOverlay = Chrona.Domain.StoredOverlay
module MemberRecord = Chrona.Domain.MemberRecord
module ActivityIndex = Chrona.Domain.ActivityIndex
module AuditRecord = Chrona.Domain.AuditRecord
