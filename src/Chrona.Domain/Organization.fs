/// The organization manifest (requirements expansion 2.6): who an
/// organization is and how its time is kept, stored as one Arca record in the
/// organization's own folder, which is named by its immutable OrganizationId
/// (requirements expansion 2.5).
///
/// Pure. Stored content is untrusted input: decoding checks the envelope, the
/// record type, the schema version, the organization it belongs to, and every
/// field.
module Chrona.Domain.Organization

open System
open System.Globalization
open Arca
open Chrona.Domain.Diagnostics

/// The organization's immutable id as an Arca dataset id.
let dataset (organizationId: string) : Result<DatasetId, Diagnostic> =
    DatasetId.create organizationId
    |> Result.mapError (fun _ -> InvalidOrganizationId organizationId)

/// Whether an organization's time is submitted, and approved, at period end.
type ApprovalPolicy =
    { SubmissionExpected: bool
      ApprovalRequired: bool }

/// An organization's manifest: who it is and how its time is kept.
type OrganizationManifest =
    { /// Immutable. It names the organization's folder.
      OrganizationId: string
      DisplayName: string
      /// A short, lower-case name for URLs and display; mutable.
      Slug: string
      /// The version of Chrona's storage layout the organization's data uses.
      StorageVersion: int
      /// The organization's business time zone (IANA id).
      DefaultTimeZone: string
      WeekStart: DayOfWeek
      /// The billing policy that applies when nothing more specific does.
      DefaultBillingPolicy: Billing.BillingPolicy
      Approval: ApprovalPolicy
      /// Where, inside the organization's folder, its reference records live.
      References: string
      /// Where, inside the organization's folder, its configuration records live.
      Configuration: string
      CreatedAt: DateTimeOffset }

/// The storage layout version this Chrona writes.
[<Literal>]
let StorageVersion = 1

let private recordType text =
    match RecordType.create text with
    | Ok recordType -> recordType
    | Error _ -> invalidOp ("internal: not a record type: " + text)

/// The record type of an organization manifest.
let manifestType = recordType "chrona.organization"

/// The record type of reference data (clients, projects, activity types, tags).
let referenceType = recordType "chrona.reference"

/// The record type of organization configuration (periods, billing policies).
let configurationType = recordType "chrona.configuration"

/// The organization manifest's schema versions this Chrona reads and writes.
let schema =
    { Type = manifestType
      OldestReadable = 1
      Current = 1 }

let private folderOf recordType =
    String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ]

/// A new organization's manifest with the legacy defaults: weekly periods
/// starting Monday, no submission or approval, and six-minute billing rounded up.
let create (organizationId: string) (displayName: string) (slug: string) (zoneId: string) (createdAt: DateTimeOffset) =
    { OrganizationId = organizationId
      DisplayName = displayName
      Slug = slug
      StorageVersion = StorageVersion
      DefaultTimeZone = zoneId
      WeekStart = DayOfWeek.Monday
      DefaultBillingPolicy = Billing.legacyDefault organizationId
      Approval =
        { SubmissionExpected = false
          ApprovalRequired = false }
      References = folderOf referenceType
      Configuration = folderOf configurationType
      CreatedAt = createdAt }

let private validSlug (slug: string) =
    not (String.IsNullOrEmpty slug)
    && slug.Length <= 64
    && Char.IsAsciiLetterLower slug[0]
    && slug |> Seq.forall (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-')
    && not (slug.EndsWith '-')

/// Every reason a manifest cannot be stored; empty when it can.
let problems (manifest: OrganizationManifest) =
    [ match dataset manifest.OrganizationId with
      | Error diagnostic -> diagnostic
      | Ok _ -> ()
      if String.IsNullOrWhiteSpace manifest.DisplayName then
          MissingField "displayName"
      if not (validSlug manifest.Slug) then
          InvalidSlug manifest.Slug
      if String.IsNullOrWhiteSpace manifest.DefaultTimeZone then
          MissingField "defaultTimeZone"
      if manifest.StorageVersion <> StorageVersion then
          UnsupportedStorageVersion(manifest.StorageVersion, StorageVersion)
      match manifest.DefaultBillingPolicy.Scope with
      | Billing.OrganizationScope id when id = manifest.OrganizationId -> ()
      | _ -> InvalidOrganizationManifest "the default billing policy applies to another scope"
      if manifest.DefaultBillingPolicy.IncrementMinutes < 1 then
          InvalidOrganizationManifest "the default billing increment must be at least one minute" ]

let private timestamp (at: DateTimeOffset) =
    at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

let private rounding =
    function
    | Billing.Up -> "up"
    | Billing.Down -> "down"
    | Billing.Nearest -> "nearest"
    | Billing.Exact -> "exact"

let private dayName (day: DayOfWeek) = day.ToString().ToLowerInvariant()

/// The manifest's record body.
let body (manifest: OrganizationManifest) =
    let policy = manifest.DefaultBillingPolicy

    Json.objectOf
        [ "organizationId", Json.String manifest.OrganizationId
          "displayName", Json.String manifest.DisplayName
          "slug", Json.String manifest.Slug
          "storageVersion", Json.Number(decimal manifest.StorageVersion)
          "defaultTimeZone", Json.String manifest.DefaultTimeZone
          "weekStart", Json.String(dayName manifest.WeekStart)
          "defaultBillingPolicy",
          Json.objectOf
              [ "policyId", Json.String policy.PolicyId
                "version", Json.Number(decimal policy.Version)
                "incrementMinutes", Json.Number(decimal policy.IncrementMinutes)
                "rounding", Json.String(rounding policy.Rounding)
                "effectiveFrom", Json.String(policy.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) ]
          "approval",
          Json.objectOf
              [ "submissionExpected", Json.Bool manifest.Approval.SubmissionExpected
                "approvalRequired", Json.Bool manifest.Approval.ApprovalRequired ]
          "locations",
          Json.objectOf
              [ "references", Json.String manifest.References
                "configuration", Json.String manifest.Configuration ]
          "createdAt", Json.String(timestamp manifest.CreatedAt) ]

/// The manifest's key: `records/chrona.organization/<OrganizationId>.json`
/// inside the organization's folder.
let key (organizationId: string) : Result<RecordKey, Diagnostic> =
    match RecordId.create organizationId with
    | Ok id ->
        Ok
            { Type = manifestType
              Partition = []
              Id = id }
    | Error _ -> Error(InvalidOrganizationId organizationId)

/// The manifest's path inside the organization's folder.
let path (organizationId: string) : Result<RelativePath, Diagnostic> =
    key organizationId
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The manifest's canonical stored text, or every reason it cannot be stored.
let encode (manifest: OrganizationManifest) : Result<string, Diagnostic list> =
    match problems manifest with
    | _ :: _ as problems -> Error problems
    | [] ->
        key manifest.OrganizationId
        |> Result.mapError List.singleton
        |> Result.bind (fun key ->
            { Id = key.Id
              Type = manifestType
              SchemaVersion = schema.Current
              Mutability = Mutability.Mutable
              Body = body manifest }
            |> Record.encode Record.DefaultMaxBytes
            |> Result.mapError (fun error ->
                [ InvalidOrganizationManifest(
                      match error with
                      | EncodeError.TooLarge(bytes, limit) -> $"the manifest is {bytes} bytes, over the {limit}-byte limit"
                      | EncodeError.InvalidSchemaVersion version -> $"schema version {version} is not valid"
                  ) ]))

// --- Decoding: stored content is untrusted input (ARCA-INT-001) -----------

let private invalid detail = Error(InvalidOrganizationManifest detail)

let private field name value =
    match Json.field name value with
    | Some found -> Ok found
    | None -> invalid $"'{name}' is missing"

let private text name value =
    field name value
    |> Result.bind (function
        | Json.String found -> Ok found
        | _ -> invalid $"'{name}' is not text")

let private integer name value =
    field name value
    |> Result.bind (function
        | Json.Number number when number = Math.Floor number && abs number <= 1000000m -> Ok(int number)
        | _ -> invalid $"'{name}' is not a whole number")

let private flag name value =
    field name value
    |> Result.bind (function
        | Json.Bool found -> Ok found
        | _ -> invalid $"'{name}' is not true or false")

let private closed (names: string list) value =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> invalid $"'{key}' is not a manifest field"
        | None -> Ok()
    | _ -> invalid "expected an object"

let private ofRounding =
    function
    | "up" -> Ok Billing.Up
    | "down" -> Ok Billing.Down
    | "nearest" -> Ok Billing.Nearest
    | "exact" -> Ok Billing.Exact
    | other -> invalid $"'{other}' is not a rounding rule"

let private ofDayName (name: string) =
    Enum.GetValues<DayOfWeek>()
    |> Array.tryFind (fun day -> dayName day = name)
    |> function
        | Some day -> Ok day
        | None -> invalid $"'{name}' is not a day of the week"

let private ofDate (name: string) (value: string) =
    match DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
    | true, date -> Ok date
    | _ -> invalid $"'{name}' is not a yyyy-MM-dd date"

let private ofTimestamp (value: string) =
    match DateTimeOffset.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
    | true, at -> Ok(at.ToUniversalTime())
    | _ -> invalid "'createdAt' is not a yyyy-MM-ddTHH:mm:ss.fffZ timestamp"

let private policyOf organizationId value =
    field "defaultBillingPolicy" value
    |> Result.bind (fun policy ->
        closed [ "effectiveFrom"; "incrementMinutes"; "policyId"; "rounding"; "version" ] policy
        |> Result.bind (fun () -> text "policyId" policy)
        |> Result.bind (fun policyId ->
            integer "version" policy
            |> Result.bind (fun version ->
                integer "incrementMinutes" policy
                |> Result.bind (fun increment ->
                    text "rounding" policy
                    |> Result.bind ofRounding
                    |> Result.bind (fun rule ->
                        text "effectiveFrom" policy
                        |> Result.bind (ofDate "effectiveFrom")
                        |> Result.map (fun effectiveFrom ->
                            ({ PolicyId = policyId
                               Version = version
                               Scope = Billing.OrganizationScope organizationId
                               IncrementMinutes = increment
                               Rounding = rule
                               EffectiveFrom = effectiveFrom }
                            : Billing.BillingPolicy)))))))

let private approvalOf value =
    field "approval" value
    |> Result.bind (fun approval ->
        closed [ "approvalRequired"; "submissionExpected" ] approval
        |> Result.bind (fun () -> flag "submissionExpected" approval)
        |> Result.bind (fun submission ->
            flag "approvalRequired" approval
            |> Result.map (fun required ->
                { SubmissionExpected = submission
                  ApprovalRequired = required })))

let private locationsOf value =
    field "locations" value
    |> Result.bind (fun locations ->
        closed [ "configuration"; "references" ] locations
        |> Result.bind (fun () -> text "references" locations)
        |> Result.bind (fun references -> text "configuration" locations |> Result.map (fun configuration -> references, configuration)))
    |> Result.bind (fun (references, configuration) ->
        match RelativePath.parse references, RelativePath.parse configuration with
        | Ok _, Ok _ -> Ok(references, configuration)
        | _ -> invalid "a manifest location is not a relative path")

/// A manifest from its record body.
let ofBody (body: Json) : Result<OrganizationManifest, Diagnostic> =
    closed
        [ "approval"
          "createdAt"
          "defaultBillingPolicy"
          "defaultTimeZone"
          "displayName"
          "locations"
          "organizationId"
          "slug"
          "storageVersion"
          "weekStart" ]
        body
    |> Result.bind (fun () -> text "organizationId" body)
    |> Result.bind (fun organizationId ->
        match
            text "displayName" body,
            text "slug" body,
            integer "storageVersion" body,
            text "defaultTimeZone" body,
            text "weekStart" body |> Result.bind ofDayName
        with
        | Ok displayName, Ok slug, Ok storageVersion, Ok zone, Ok weekStart ->
            match policyOf organizationId body, approvalOf body, locationsOf body, text "createdAt" body |> Result.bind ofTimestamp with
            | Ok policy, Ok approval, Ok(references, configuration), Ok createdAt ->
                Ok
                    { OrganizationId = organizationId
                      DisplayName = displayName
                      Slug = slug
                      StorageVersion = storageVersion
                      DefaultTimeZone = zone
                      WeekStart = weekStart
                      DefaultBillingPolicy = policy
                      Approval = approval
                      References = references
                      Configuration = configuration
                      CreatedAt = createdAt }
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)

/// One sentence for why stored bytes are not a valid record or manifest.
let describeDecode =
    function
    | DecodeError.InvalidJson error -> JsonError.describe error
    | DecodeError.NotCanonical -> "the stored text is not canonical"
    | DecodeError.MissingField name -> $"'{name}' is missing"
    | DecodeError.InvalidField(name, detail) -> $"'{name}': {detail}"
    | DecodeError.UnknownField name -> $"'{name}' is not a field this version reads"
    | DecodeError.UnsupportedFormat(found, supported) -> $"format {found} is newer than {supported}"
    | DecodeError.TooLarge(bytes, limit) -> $"{bytes} bytes is over the {limit}-byte limit"

/// The organization manifest stored at `path` in the organization's folder.
/// Stored content is untrusted: it must be a valid Chrona record of the
/// expected type, at a schema version this Chrona reads, for the organization
/// whose folder it was found in, and itself valid.
let read (organizationId: string) (stored: ReadOutcome) : Result<OrganizationManifest, Diagnostic list> =
    match stored with
    | ReadOutcome.Absent -> Error [ OrganizationNotInitialized organizationId ]
    | ReadOutcome.Found found ->
        let path = RelativePath.render found.Path

        Record.decode Record.DefaultMaxBytes found.Content
        |> Result.mapError (fun error -> [ InvalidStoredRecord(path, describeDecode error) ])
        |> Result.bind (fun record ->
            if record.Type <> manifestType then
                Error [ InvalidStoredRecord(path, $"a {RecordType.value record.Type} record where an organization manifest belongs") ]
            elif RecordId.value record.Id <> organizationId then
                Error [ InvalidStoredRecord(path, $"the manifest of '{RecordId.value record.Id}' in the folder of '{organizationId}'") ]
            else
                match SchemaSupport.canRead schema record.SchemaVersion with
                | Error _ ->
                    Error [ InvalidStoredRecord(path, $"schema version {record.SchemaVersion} is not one this Chrona reads") ]
                | Ok() ->
                    ofBody record.Body
                    |> Result.mapError List.singleton
                    |> Result.bind (fun manifest ->
                        if manifest.OrganizationId <> organizationId then
                            Error [ InvalidStoredRecord(path, "the body names another organization") ]
                        else
                            match problems manifest with
                            | [] -> Ok manifest
                            | problems -> Error problems))
