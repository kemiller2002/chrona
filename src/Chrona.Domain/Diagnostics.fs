/// Stable diagnostics (requirements expansion 26): every rejected command
/// names a machine-readable code that does not change when wording does.
module Chrona.Domain.Diagnostics

/// Why a command was refused. These are domain outcomes, never operational
/// faults: Aegis is for unexpected failures at boundaries, not for these.
type Diagnostic =
    // Entry and timing.
    | DurationNotPositive
    | DurationNotWholeMinutes
    | EndBeforeStart
    | CrossesBusinessDay
    | FutureTime
    | ReasonRequiredForHistoricalEntry
    | AmbiguousLocalTime
    | InvalidLocalTime
    | InvalidTimeZone of zone: string
    // Classification and identity.
    | MissingField of field: string
    | OrganizationMismatch
    | ActorMismatch
    // Overlap and capacity.
    | OverlapsActivity of activityId: string
    | DailyCapacityExceeded of localDate: string * minutes: int
    // Lifecycle (WI-0021 onward).
    | RevisionConflict of expected: int * actual: int
    | IllegalTransition of from: string * command: string
    | UnknownActivity of activityId: string
    | SplitDurationMismatch of expected: int * actual: int
    | EvidenceAssignmentInvalid
    | IncompatibleMergeSources of reason: string
    | PublicationStateConflict of reason: string
    // Timer (WI-0022).
    | TimerAlreadyActive
    | NoActiveTimer
    | TimerNotPaused
    | TimerPaused
    | LongRunningTimerNeedsReview of minutes: int
    // Review, billing and publication (WI-0023).
    | ApprovalNotEnabled
    | StaleApproval of activityId: string
    | NotBillableActivity of activityId: string
    | AlreadyPublished of activityId: string
    | NotApproved of activityId: string
    | BillingPolicyNotFound
    // Legacy compatibility (WI-0027).
    | UnknownLegacySource of name: string
    | UnknownLegacyFormat of schemaName: string * schemaVersion: string

/// The stable code: `CHRONA.<AREA>.<NAME>`.
let code =
    function
    | DurationNotPositive -> "CHRONA.ENTRY.DURATION_NOT_POSITIVE"
    | DurationNotWholeMinutes -> "CHRONA.ENTRY.DURATION_NOT_WHOLE_MINUTES"
    | EndBeforeStart -> "CHRONA.ENTRY.END_BEFORE_START"
    | CrossesBusinessDay -> "CHRONA.ENTRY.CROSSES_BUSINESS_DAY"
    | FutureTime -> "CHRONA.ENTRY.FUTURE_TIME"
    | ReasonRequiredForHistoricalEntry -> "CHRONA.ENTRY.REASON_REQUIRED"
    | AmbiguousLocalTime -> "CHRONA.TIME.AMBIGUOUS_LOCAL_TIME"
    | InvalidLocalTime -> "CHRONA.TIME.INVALID_LOCAL_TIME"
    | InvalidTimeZone _ -> "CHRONA.TIME.INVALID_TIME_ZONE"
    | MissingField _ -> "CHRONA.ENTRY.MISSING_FIELD"
    | OrganizationMismatch -> "CHRONA.AUTH.ORGANIZATION_MISMATCH"
    | ActorMismatch -> "CHRONA.AUTH.ACTOR_MISMATCH"
    | OverlapsActivity _ -> "CHRONA.OVERLAP.OVERLAPS_ACTIVITY"
    | DailyCapacityExceeded _ -> "CHRONA.OVERLAP.DAILY_CAPACITY_EXCEEDED"
    | RevisionConflict _ -> "CHRONA.CONCURRENCY.REVISION_CONFLICT"
    | IllegalTransition _ -> "CHRONA.LIFECYCLE.ILLEGAL_TRANSITION"
    | UnknownActivity _ -> "CHRONA.LIFECYCLE.UNKNOWN_ACTIVITY"
    | SplitDurationMismatch _ -> "CHRONA.SPLIT.DURATION_MISMATCH"
    | EvidenceAssignmentInvalid -> "CHRONA.SPLIT.EVIDENCE_ASSIGNMENT"
    | IncompatibleMergeSources _ -> "CHRONA.MERGE.INCOMPATIBLE_SOURCES"
    | PublicationStateConflict _ -> "CHRONA.PUBLICATION.STATE_CONFLICT"
    | TimerAlreadyActive -> "CHRONA.TIMER.ALREADY_ACTIVE"
    | NoActiveTimer -> "CHRONA.TIMER.NONE_ACTIVE"
    | TimerNotPaused -> "CHRONA.TIMER.NOT_PAUSED"
    | TimerPaused -> "CHRONA.TIMER.PAUSED"
    | LongRunningTimerNeedsReview _ -> "CHRONA.TIMER.LONG_RUNNING_REVIEW"
    | ApprovalNotEnabled -> "CHRONA.REVIEW.APPROVAL_NOT_ENABLED"
    | StaleApproval _ -> "CHRONA.REVIEW.STALE_APPROVAL"
    | NotBillableActivity _ -> "CHRONA.PUBLICATION.NOT_BILLABLE"
    | AlreadyPublished _ -> "CHRONA.PUBLICATION.DUPLICATE"
    | NotApproved _ -> "CHRONA.PUBLICATION.NOT_APPROVED"
    | BillingPolicyNotFound -> "CHRONA.BILLING.POLICY_NOT_FOUND"
    | UnknownLegacySource _ -> "CHRONA.LEGACY.UNKNOWN_SOURCE"
    | UnknownLegacyFormat _ -> "CHRONA.LEGACY.UNKNOWN_FORMAT"
