/// Where Chrona keeps its data (CHX-DATALOC-001..005, requirements expansion
/// 2.4 to 2.7): the deployment's configured location, the namespace Chrona
/// owns inside it, one folder (dataset) per organization, initializing and
/// opening those folders, and the refusal to initialize production data in a
/// public repository. The organization manifest itself is `Organization`.
///
/// Pure. Every location comes from deployment configuration; nothing here
/// names a repository, owner, branch or base path. Storage itself is Arca's:
/// this module builds Arca operations and reads Arca objects, and an Arca
/// provider (in memory for tests, GitHub in the application) carries them out.
module Chrona.Domain.Storage

open System
open Arca
open Chrona.Domain.Diagnostics

/// Chrona's application identifier, which is also the folder it owns under
/// the configured base path (CHX-DATALOC-002). It is the application's name,
/// not a location.
let application =
    match AppId.create "chrona" with
    | Ok id -> id
    | Error error -> invalidOp ("internal: Chrona's application id is invalid: " + LocationError.describe error)

/// One configured data location, as deployment configuration states it.
type LocationConfig =
    { Owner: string
      Repository: string
      Branch: string
      /// The folder application namespaces live under; empty for the repository root.
      BasePath: string }

/// A deployment's storage configuration (CHX-DATALOC-001). An organization
/// may keep its data in another repository (requirements expansion 2.5), for
/// example to give it its own permissions (CHX-DATALOC-004).
type DeploymentConfig =
    { Location: LocationConfig
      Environment: EnvironmentKind
      /// A display name for the environment, for example "production".
      EnvironmentName: string
      /// Organizations whose data lives somewhere other than `Location`, by OrganizationId.
      OrganizationLocations: Map<string, LocationConfig> }

let private locationOf (config: LocationConfig) =
    DataLocation.create config.Owner config.Repository config.Branch config.BasePath
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// Chrona's storage binding for a deployment, or why its configuration is refused.
let binding (config: DeploymentConfig) : Result<ApplicationBinding, Diagnostic> =
    if String.IsNullOrWhiteSpace config.EnvironmentName then
        Error(MissingField "environmentName")
    else
        locationOf config.Location
        |> Result.map (fun location ->
            { Application = application
              Environment =
                { Kind = config.Environment
                  Name = config.EnvironmentName }
              Location = location })

/// Chrona's own namespace: `<base path>/chrona`. Chrona reads and writes
/// nothing outside it (CHX-DATALOC-002, CHX-DATALOC-003).
let applicationNamespace (binding: ApplicationBinding) : Result<Namespace, Diagnostic> =
    Namespace.ofApplication binding
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// One organization's folder: `<base path>/chrona/datasets/<OrganizationId>`,
/// in the organization's own repository when the deployment configures one.
/// The folder is named by the immutable id, never by the display name or slug,
/// so renaming an organization never moves its data (requirements expansion 2.5).
let organizationNamespace (config: DeploymentConfig) (binding: ApplicationBinding) (organizationId: string) : Result<Namespace, Diagnostic> =
    Organization.dataset organizationId
    |> Result.bind (fun dataset ->
        match Map.tryFind organizationId config.OrganizationLocations with
        | None -> Ok None
        | Some location -> locationOf location |> Result.map Some
        |> Result.bind (fun location ->
            Namespace.ofDataset binding dataset location
            |> Result.mapError (LocationError.describe >> InvalidDataLocation)))

/// Whether production data may be initialized in a repository of this
/// visibility (requirements expansion 2.7, ARCA-LOC-008). Only a production
/// deployment into a public repository is refused, unless someone has decided
/// otherwise and recorded why.
let permitsInitialization
    (environment: EnvironmentKind)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    : Result<unit, Diagnostic> =
    Visibility.permitsInitialization environment visibility overrideDecision
    |> Result.mapError (function
        | VisibilityRefusal.PublicProductionRepository -> PublicProductionRepository
        | VisibilityRefusal.OverrideWithoutReason -> OverrideWithoutReason)

// ---------------------------------------------------------------------------
// Opening and initializing namespaces
// ---------------------------------------------------------------------------

let private place (location: DataLocation) =
    $"{location.Repository} ({BranchName.value location.Branch}, '{RelativePath.render location.BasePath}')"

let private describeProblem =
    function
    | ManifestProblem.WrongApplication(found, expected) -> $"the folder belongs to '{found}', not '{expected}'"
    | ManifestProblem.WrongScope -> "the folder's manifest describes a different scope"
    | ManifestProblem.UnsupportedStorageSchema(found, supported) -> $"storage schema {found} is not {supported}"
    | ManifestProblem.UnsupportedProviderContract(found, supported) -> $"provider contract {found} is newer than {supported}"
    | ManifestProblem.Relocated relocation ->
        $"the data lives at {place relocation.Recorded}, not the configured {place relocation.Configured}; moving it is a migration"
    | ManifestProblem.MigrationInProgress state -> $"migration {state.MigrationId} is in progress"
    | ManifestProblem.Retired migrationId -> $"the data was moved by migration {migrationId} and this copy retired"

/// A namespace's Arca manifest, checked against the namespace Chrona is
/// configured for before any record in it is read or written (ARCA-REC-005,
/// ARCA-LOC-006, ARCA-LOC-009). A namespace with no manifest is not
/// initialized; one at another location must be migrated, never re-pointed.
let openNamespace (ns: Namespace) (stored: ReadOutcome) : Result<Manifest, Diagnostic list> =
    let root = RelativePath.render ns.Root

    match stored with
    | ReadOutcome.Absent -> Error [ NamespaceNotInitialized root ]
    | ReadOutcome.Found found ->
        Manifest.decode found.Content
        |> Result.mapError (fun error -> [ InvalidStoredRecord(RelativePath.render found.Path, Organization.describeDecode error) ])
        |> Result.bind (fun manifest ->
            match Manifest.check ns manifest with
            | [] -> Ok manifest
            | problems -> Error(problems |> List.map (fun problem -> NamespaceUnusable(root, describeProblem problem))))

/// Who is initializing or changing storage, and the identifiers of the
/// operation. Supplied by the caller: this module reads no clock and draws no
/// randomness.
type OperationContext =
    { Actor: Actor
      ProviderIdentity: string option
      CorrelationId: CorrelationId
      IdempotencyKey: IdempotencyKey
      At: DateTimeOffset }

let private metadata (context: OperationContext) (summary: string) : OperationMetadata =
    { Summary = summary
      Actor = context.Actor
      ProviderIdentity = context.ProviderIdentity
      ExecutionId = None
      CorrelationId = context.CorrelationId
      IdempotencyKey = context.IdempotencyKey }

/// One Arca operation in `ns`: the changes become one commit, carrying the
/// context's actor, correlation and idempotency key, or every reason Arca
/// refuses to send it.
let operation (ns: Namespace) (context: OperationContext) (summary: string) (changes: Change list) : Result<Operation, Diagnostic list> =
    Operation.create ns (metadata context summary) changes
    |> Result.mapError (fun error ->
        [ StorageOperationRefused(
              match error with
              | OperationError.NoChanges -> "nothing to write"
              | OperationError.DuplicatePath path -> $"'{path}' is written twice"
              | OperationError.InvalidPath error -> LocationError.describe error
              | OperationError.InvalidSummary _ -> "the summary is not one short line"
              | OperationError.CredentialInContent field -> $"'{field}' looks like a credential"
              | OperationError.InvalidMetadata field -> $"'{field}' is not a single-line identifier"
          ) ])

let private arcaManifest (ns: Namespace) (context: OperationContext) =
    { Scope =
        match ns.Dataset with
        | Some dataset -> ManifestScope.Dataset dataset
        | None -> ManifestScope.Application
      Application = ns.Application
      StorageSchema = Manifest.StorageSchema
      ProviderContract = StorageContract.Version
      RecordSchemas =
        match ns.Dataset with
        | Some _ -> Map.ofList [ RecordType.value Organization.manifestType, Organization.schema.Current ]
        | None -> Map.empty
      CreatedBy = context.Actor
      CreatedAt = context.At
      Location = ns.Location
      Migration = None }

let private manifestPath () =
    Layout.manifestPath |> Result.mapError (LocationError.describe >> InvalidDataLocation >> List.singleton)

/// Initializes Chrona's own namespace: one commit creating its Arca manifest,
/// which marks the folder as Chrona's. It fails as a conflict, never
/// overwrites, when the folder is already initialized.
let initializeApplication
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    : Result<Operation, Diagnostic list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> applicationNamespace binding |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        manifestPath ()
        |> Result.bind (fun path ->
            operation ns context "initialize Chrona storage" [ Change.Create(path, Manifest.encode (arcaManifest ns context)) ]))

/// Initializes an organization's folder: one commit creating its Arca
/// manifest and its organization manifest. `visibility` is that of the
/// repository the organization's data goes to, which may be its own
/// (requirements expansion 2.5, 2.7). Nothing is overwritten: an existing
/// organization is a conflict.
let initializeOrganization
    (config: DeploymentConfig)
    (binding: ApplicationBinding)
    (visibility: RepositoryVisibility)
    (overrideDecision: PublicProductionOverride option)
    (context: OperationContext)
    (manifest: Organization.OrganizationManifest)
    : Result<Operation, Diagnostic list> =
    permitsInitialization binding.Environment.Kind visibility overrideDecision
    |> Result.mapError List.singleton
    |> Result.bind (fun () -> organizationNamespace config binding manifest.OrganizationId |> Result.mapError List.singleton)
    |> Result.bind (fun ns ->
        Organization.encode manifest
        |> Result.bind (fun content ->
            manifestPath ()
            |> Result.bind (fun arcaPath ->
                Organization.path manifest.OrganizationId
                |> Result.mapError List.singleton
                |> Result.bind (fun path ->
                    operation
                        ns
                        context
                        $"initialize organization {manifest.OrganizationId}"
                        [ Change.Create(arcaPath, Manifest.encode (arcaManifest ns context))
                          Change.Create(path, content) ]))))

/// Renames an organization: a new display name and slug, the same id and the
/// same folder. `revision` is the manifest's revision as last read; a manifest
/// changed since is a conflict the caller reloads and decides.
let renameOrganization
    (ns: Namespace)
    (context: OperationContext)
    (revision: Revision)
    (displayName: string)
    (slug: string)
    (manifest: Organization.OrganizationManifest)
    : Result<Operation, Diagnostic list> =
    let renamed =
        { manifest with
            DisplayName = displayName
            Slug = slug }

    Organization.encode renamed
    |> Result.bind (fun content ->
        Organization.path manifest.OrganizationId
        |> Result.mapError List.singleton
        |> Result.bind (fun path ->
            operation ns context $"rename organization {manifest.OrganizationId}" [ Change.Update(path, content, revision) ]))

// ---------------------------------------------------------------------------
// Deployment configuration text (CHX-DATALOC-001)
// ---------------------------------------------------------------------------

let private configInvalid detail = Error(InvalidDeploymentConfig detail)

let private configText name value =
    match Json.field name value with
    | Some(Json.String found) -> Ok found
    | Some _ -> configInvalid $"'{name}' is not text"
    | None -> configInvalid $"'{name}' is missing"

let private configClosed (names: string list) value =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> configInvalid $"'{key}' is not a configuration field"
        | None -> Ok members
    | _ -> configInvalid "expected an object"

let private locationConfigOf value =
    configClosed [ "basePath"; "branch"; "owner"; "repository" ] value
    |> Result.bind (fun _ ->
        match configText "owner" value, configText "repository" value, configText "branch" value, configText "basePath" value with
        | Ok owner, Ok repository, Ok branch, Ok basePath ->
            let config =
                { Owner = owner
                  Repository = repository
                  Branch = branch
                  BasePath = basePath }

            locationOf config |> Result.map (fun _ -> config)
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let private environmentOf =
    function
    | "local" -> Ok EnvironmentKind.Local
    | "test" -> Ok EnvironmentKind.Test
    | "staging" -> Ok EnvironmentKind.Staging
    | "production" -> Ok EnvironmentKind.Production
    | other -> configInvalid $"'{other}' is not local, test, staging or production"

let private organizationsOf value =
    match Json.field "organizations" value with
    | None -> Ok Map.empty
    | Some(Json.Object members) ->
        members
        |> List.fold
            (fun state (organizationId, entry) ->
                state
                |> Result.bind (fun found ->
                    Organization.dataset organizationId
                    |> Result.bind (fun _ -> locationConfigOf entry)
                    |> Result.map (fun entry -> Map.add organizationId entry found)))
            (Ok Map.empty)
    | Some _ -> configInvalid "'organizations' is not an object"

/// A deployment's storage configuration from its JSON text:
///
/// ```json
/// {"environment":"production","environmentName":"production",
///  "location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments/prod"},
///  "organizations":{"org_2":{"owner":"acme-eu","repository":"chrona-eu","branch":"main","basePath":""}}}
/// ```
///
/// Every location is validated; `organizations` is optional.
let parseDeploymentConfig (text: string) : Result<DeploymentConfig, Diagnostic> =
    match Json.parse text with
    | Error error -> configInvalid (JsonError.describe error)
    | Ok value ->
        configClosed [ "environment"; "environmentName"; "location"; "organizations" ] value
        |> Result.bind (fun _ ->
            match configText "environment" value |> Result.bind environmentOf, configText "environmentName" value with
            | Ok environment, Ok environmentName ->
                match Json.field "location" value with
                | None -> configInvalid "'location' is missing"
                | Some location ->
                    locationConfigOf location
                    |> Result.bind (fun location ->
                        organizationsOf value
                        |> Result.map (fun organizations ->
                            { Location = location
                              Environment = environment
                              EnvironmentName = environmentName
                              OrganizationLocations = organizations }))
            | Error e, _
            | _, Error e -> Error e)
        |> Result.bind (fun config -> binding config |> Result.map (fun _ -> config))
