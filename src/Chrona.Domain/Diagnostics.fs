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
    /// The running timer overlaps time recorded since it started, for
    /// example on another device (10.5).
    | ConcurrentTimer of activityId: string
    // Review, billing and publication (WI-0023).
    | ApprovalNotEnabled
    | StaleApproval of activityId: string
    /// Time in a period that is submitted (awaiting approval) or closed is
    /// not changed until the period is reopened (26, WI-0036).
    | SubmittedPeriodRestriction of periodStart: System.DateOnly
    /// A period with no recorded time of the person's to submit.
    | NoTimeToSubmit of periodStart: System.DateOnly
    /// No submission with this id is known.
    | UnknownSubmission of submissionId: string
    | NotBillableActivity of activityId: string
    | AlreadyPublished of activityId: string
    | NotApproved of activityId: string
    | BillingPolicyNotFound
    // Evidence (WI-0047).
    | InvalidEvidenceUrl of url: string
    // Reference data (WI-0045).
    | UnknownReference of kind: string * id: string
    | ArchivedReference of kind: string * id: string
    | DuplicateReference of kind: string * id: string
    | ReferenceOwnedElsewhere of kind: string * id: string * owner: string
    // Legacy compatibility (WI-0027).
    | UnknownLegacySource of name: string
    | UnknownLegacyFormat of schemaName: string * schemaVersion: string
    // Storage location, namespaces and organizations (WI-0028).
    | InvalidDeploymentConfig of detail: string
    | InvalidDataLocation of detail: string
    | InvalidOrganizationId of id: string
    | InvalidSlug of slug: string
    | InvalidOrganizationManifest of detail: string
    | UnsupportedStorageVersion of found: int * supported: int
    | PublicProductionRepository
    | OverrideWithoutReason
    | NamespaceNotInitialized of root: string
    | OrganizationNotInitialized of id: string
    | NamespaceUnusable of root: string * detail: string
    | InvalidStoredRecord of path: string * detail: string
    /// A problem recorded with a stored record, kept by its stable code
    /// (an observation's, WI-0038).
    | RecordedProblem of code: string
    | StorageOperationRefused of detail: string
    | UnstorableActivity of id: string * detail: string
    | UnstorableRecord of id: string * detail: string
    // Integrity of stored activities (WI-0051, expansion 39 and 41).
    | MisplacedRecord of path: string
    | ImpossibleRevision of id: string
    | DuplicateActivityId of id: string
    | StoredOverlap of id: string * other: string
    | InvalidLineage of id: string * other: string
    | IncompleteRead of folder: string
    | ExternalEdit of path: string
    /// An outside edit claims a state only Chrona's own transitions give.
    | ExternalStateClaim of activityId: string * state: string
    // Concurrency (WI-0035, expansion 21 and 26).
    /// The same record was changed elsewhere after this change was decided:
    /// an unresolved semantic conflict, for the person to resolve.
    | SemanticConflict of kind: string * id: string
    /// The repository moved on every attempt; nothing was found wrong.
    | StoreKeptChanging
    // Membership and authorization (WI-0030).
    | UnauthorizedCapability of capability: string
    | NotAMember of principalId: string * organizationId: string
    | AlreadyAMember of principalId: string * organizationId: string
    | LastAdministrator of organizationId: string
    | CapabilityNotForKind of capability: string * kind: string

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
    | ConcurrentTimer _ -> "CHRONA.TIMER.CONCURRENT_CONFLICT"
    | ApprovalNotEnabled -> "CHRONA.REVIEW.APPROVAL_NOT_ENABLED"
    | StaleApproval _ -> "CHRONA.REVIEW.STALE_APPROVAL"
    | SubmittedPeriodRestriction _ -> "CHRONA.REVIEW.SUBMITTED_PERIOD"
    | NoTimeToSubmit _ -> "CHRONA.REVIEW.NOTHING_TO_SUBMIT"
    | UnknownSubmission _ -> "CHRONA.REVIEW.UNKNOWN_SUBMISSION"
    | NotBillableActivity _ -> "CHRONA.PUBLICATION.NOT_BILLABLE"
    | AlreadyPublished _ -> "CHRONA.PUBLICATION.DUPLICATE"
    | NotApproved _ -> "CHRONA.PUBLICATION.NOT_APPROVED"
    | BillingPolicyNotFound -> "CHRONA.BILLING.POLICY_NOT_FOUND"
    | InvalidEvidenceUrl _ -> "CHRONA.EVIDENCE.INVALID_URL"
    | UnknownReference _ -> "CHRONA.REFERENCE.UNKNOWN"
    | ArchivedReference _ -> "CHRONA.REFERENCE.ARCHIVED"
    | DuplicateReference _ -> "CHRONA.REFERENCE.DUPLICATE"
    | ReferenceOwnedElsewhere _ -> "CHRONA.REFERENCE.OWNED_ELSEWHERE"
    | UnknownLegacySource _ -> "CHRONA.LEGACY.UNKNOWN_SOURCE"
    | UnknownLegacyFormat _ -> "CHRONA.LEGACY.UNKNOWN_FORMAT"
    | InvalidDeploymentConfig _ -> "CHRONA.STORAGE.INVALID_CONFIGURATION"
    | InvalidDataLocation _ -> "CHRONA.STORAGE.INVALID_LOCATION"
    | InvalidOrganizationId _ -> "CHRONA.STORAGE.INVALID_ORGANIZATION_ID"
    | InvalidSlug _ -> "CHRONA.STORAGE.INVALID_SLUG"
    | InvalidOrganizationManifest _ -> "CHRONA.STORAGE.INVALID_ORGANIZATION_MANIFEST"
    | UnsupportedStorageVersion _ -> "CHRONA.STORAGE.UNSUPPORTED_VERSION"
    | PublicProductionRepository -> "CHRONA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY"
    | OverrideWithoutReason -> "CHRONA.STORAGE.OVERRIDE_WITHOUT_REASON"
    | NamespaceNotInitialized _ -> "CHRONA.STORAGE.NAMESPACE_NOT_INITIALIZED"
    | OrganizationNotInitialized _ -> "CHRONA.STORAGE.ORGANIZATION_NOT_INITIALIZED"
    | NamespaceUnusable _ -> "CHRONA.STORAGE.NAMESPACE_UNUSABLE"
    | InvalidStoredRecord _ -> "CHRONA.STORAGE.INVALID_RECORD"
    | RecordedProblem recorded -> recorded
    | StorageOperationRefused _ -> "CHRONA.STORAGE.OPERATION_REFUSED"
    | UnstorableActivity _ -> "CHRONA.STORAGE.UNSTORABLE_ACTIVITY"
    | UnstorableRecord _ -> "CHRONA.STORAGE.UNSTORABLE_RECORD"
    | MisplacedRecord _ -> "CHRONA.INTEGRITY.MISPLACED_RECORD"
    | ImpossibleRevision _ -> "CHRONA.INTEGRITY.IMPOSSIBLE_REVISION"
    | DuplicateActivityId _ -> "CHRONA.INTEGRITY.DUPLICATE_ID"
    | StoredOverlap _ -> "CHRONA.INTEGRITY.OVERLAPPING_TIME"
    | InvalidLineage _ -> "CHRONA.INTEGRITY.INVALID_LINEAGE"
    | IncompleteRead _ -> "CHRONA.INTEGRITY.INCOMPLETE_READ"
    | ExternalEdit _ -> "CHRONA.INTEGRITY.EXTERNAL_EDIT"
    | ExternalStateClaim _ -> "CHRONA.INTEGRITY.EXTERNAL_STATE_CLAIM"
    | SemanticConflict _ -> "CHRONA.CONCURRENCY.SEMANTIC_CONFLICT"
    | StoreKeptChanging -> "CHRONA.CONCURRENCY.KEPT_CHANGING"
    | UnauthorizedCapability _ -> "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY"
    | NotAMember _ -> "CHRONA.AUTH.NOT_A_MEMBER"
    | AlreadyAMember _ -> "CHRONA.AUTH.ALREADY_A_MEMBER"
    | LastAdministrator _ -> "CHRONA.AUTH.LAST_ADMINISTRATOR"
    | CapabilityNotForKind _ -> "CHRONA.AUTH.CAPABILITY_NOT_FOR_KIND"
