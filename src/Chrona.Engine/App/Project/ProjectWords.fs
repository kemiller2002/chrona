/// The words of the projection: the view's scalars, names, and every
/// diagnostic and capability said for a person.
module Chrona.Engine.App.ProjectWords

open System
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Activity
open Chrona.Engine.View
open Chrona.Engine.App.Model

let internal text key (value: string) = key, Value(Text value)
let internal flag key (value: bool) = key, Value(Flag value)
let internal items key (rows: (string * Scalar) list list) = key, Items rows
let internal n key (value: float) = key, Number value
let internal t key (value: string) = key, Text value
let internal f key (value: bool) = key, Flag value

let internal plural (n: int) (one: string) (many: string) = if n = 1 then $"1 {one}" else $"{n} {many}"
let internal quoted (text: string) = "\"" + text + "\""
let internal commaList (texts: string list) = String.Join(", ", texts)
let internal activities (n: int) = plural n "activity" "activities"

let internal referenceName (model: Model) (kind: Reference.Kind) (id: string) =
    Reference.tryName kind id model.References |> Option.defaultValue id

let internal fieldName =
    function
    | "activityType" -> "an activity type"
    | "project" -> "a project"
    | "description" -> "what you did"
    | "businessPurpose" -> "the business purpose: why this work mattered to the business"
    | "start" -> "a start date and time"
    | "end" -> "an end date and time"
    | "name" -> "a name"
    | "evidenceLabel" -> "a label saying what the evidence is"
    | "evidenceKind" -> "the kind of evidence"
    | "memberId" -> "the person's GitHub account number"
    | "memberName" -> "the person's name"
    | "statement" -> "an attestation statement"
    | other -> other

/// A diagnostic in words, with what to do next. The stable code stays on the
/// item for support and tests.
/// What a capability lets someone do, in words.
let capabilityText =
    function
    | "RecordOwnTime" -> "record your time"
    | "AmendOwnTime" -> "correct your time"
    | "SubmitOwnTime" -> "submit your time"
    | "AttestOwnDay" -> "attest your days"
    | "ViewOwnTime" -> "see your time"
    | "ViewOrganizationTime" -> "see the organization's time"
    | "ApproveTime" -> "approve time"
    | "RejectTime" -> "reject time"
    | "ReopenTime" -> "reopen time"
    | "ManageProjects" -> "manage projects"
    | "ManageActivityTypes" -> "manage activity types"
    | "ManageTags" -> "manage tags"
    | "ManageOrganizationSettings" -> "change the organization's settings"
    | "ExportTime" -> "export time"
    | "PublishBillableTime" -> "publish billable time"
    | other -> other

let describe (model: Model) (diagnostic: Diagnostic) =
    match diagnostic with
    | MissingField field -> $"Add {fieldName field}."
    | DurationNotPositive
    | EndBeforeStart -> "The end must be after the start."
    | DurationNotWholeMinutes -> "Times are recorded in whole minutes."
    | CrossesBusinessDay -> "An activity cannot cross midnight. Record each day separately."
    | FutureTime -> "That time has not happened yet. Only actual time can be recorded."
    | ReasonRequiredForHistoricalEntry -> "Say why you are entering this after the fact: it is for an earlier day."
    | AmbiguousLocalTime -> "That time happens twice on this date (the clocks go back). Choose a time outside the repeated hour."
    | InvalidLocalTime -> "That time does not exist on this date (the clocks go forward). Choose another time."
    | InvalidTimeZone _ -> "Chrona does not know your time zone yet. Try again in a moment."
    | OverlapsActivity id ->
        match model.Ledger.Activities.TryFind id with
        | Some other -> $"This overlaps \"{other.Classification.Description}\". Adjust the times so they do not overlap."
        | None -> "This overlaps another activity. Adjust the times so they do not overlap."
    | DailyCapacityExceeded(date, minutes) -> $"That would record {Format.minutes minutes} on {date}, more than the day holds."
    | TimerAlreadyActive -> "A timer is already running. Stop it before starting another."
    | NoActiveTimer -> "No timer is running."
    | TimerNotPaused -> "The timer is not paused."
    | TimerPaused -> "The timer is already paused."
    | LongRunningTimerNeedsReview minutes -> $"This timer ran {Format.minutes minutes}. Confirm all of it was worked before saving."
    | UnknownReference(kind, id) -> $"Choose {fieldName kind}: \"{id}\" is not one Chrona knows."
    | ArchivedReference(kind, id) ->
        let name =
            [ Reference.Project; Reference.ActivityType; Reference.Tag; Reference.Client; Reference.Engagement ]
            |> List.tryFind (fun k -> Reference.kindName k = kind)
            |> Option.bind (fun k -> Reference.tryName k id model.References)
            |> Option.defaultValue id

        $"\"{name}\" is archived. Choose an active one, or reactivate it under More."
    | DuplicateReference _ -> "That already exists."
    | ReferenceOwnedElsewhere(_, _, owner) -> $"This is managed in {owner}."
    | RevisionConflict _ -> "This changed since you opened it. Review the current version and try again."
    | InvalidEvidenceUrl _ -> "A link must be a web address starting with https:// or http://."
    | SplitDurationMismatch(expected, actual) -> $"The parts add up to {actual} minutes; they must add up to exactly {expected}."
    | EvidenceAssignmentInvalid -> "Each piece of evidence can support one part at most."
    | IncompatibleMergeSources reason -> $"These cannot be merged: {reason}."
    | PublicationStateConflict reason -> $"These cannot be merged: {reason}."
    | IllegalTransition(from, command) -> $"A record that is {from.ToLowerInvariant()} cannot be changed that way ({command})."
    | OrganizationMismatch
    | ActorMismatch -> "These belong to different people or organizations."
    | InvalidStoredRecord(path, detail) -> $"{path} is not a valid record ({detail}). It was left as it is."
    | MisplacedRecord path -> $"{path} is not where its contents say it belongs. It was left as it is."
    | DuplicateActivityId id -> $"Activity {id} is stored twice. Neither copy is shown until one is removed."
    | ImpossibleRevision id -> $"Activity {id} has an impossible revision. It was left as it is."
    | StoredOverlap(first, second) -> $"Activities {first} and {second} overlap in what is stored."
    | InvalidLineage(first, second) -> $"Activities {first} and {second} disagree about a split or merge."
    | IncompleteRead folder -> $"Not everything in {folder} could be read."
    | ExternalEdit path -> $"{path} was changed outside Chrona. It is held until it is reviewed."
    | ExternalStateClaim(_, state) ->
        $"It claims to be {state.ToLowerInvariant()}, which only Chrona's own review and publication steps give. It stays held; repair it in the repository."
    | SemanticConflict _ -> "This was changed elsewhere first. Keep what is stored, or redo your change on it."
    | StoreKeptChanging -> "The records kept changing elsewhere while this was being saved."
    | UnauthorizedCapability capability -> $"You do not have permission to {capabilityText capability} in this organization."
    | NotAMember _ -> "That person is not a member of this organization."
    | AlreadyAMember _ -> "That person is already a member."
    | LastAdministrator _ -> "Someone else must be able to manage the organization first."
    | CapabilityNotForKind(capability, _) -> $"Only a person can {capabilityText capability}."
    | other -> $"Chrona could not do that ({code other})."

let internal problemItems (model: Model) (form: Form) =
    model.Problems.TryFind form
    |> Option.defaultValue []
    |> List.mapi (fun index d -> [ t "id" $"{index}"; t "code" (code d); t "text" (describe model d) ])
