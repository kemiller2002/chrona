/// Data location, Chrona's namespace and organization manifests (WI-0028):
/// CHX-DATALOC-001..005 and requirements expansion 2.4 to 2.7, proven
/// against Arca's in-memory provider.
module Chrona.Tests.StorageTests

open System
open Xunit
open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Storage

module Organization = Chrona.Domain.Organization

let private ok =
    function
    | Ok value -> value
    | Error error -> failwith $"%A{error}"

let private codes (result: Result<'a, Diagnostic list>) =
    match result with
    | Ok _ -> []
    | Error diagnostics -> diagnostics |> List.map code

let private codeOf (result: Result<'a, Diagnostic>) =
    match result with
    | Ok _ -> "ok"
    | Error diagnostic -> code diagnostic

let private configText (owner: string) (repository: string) (basePath: string) =
    """{"environment":"production","environmentName":"production","location":{"owner":"OWNER","repository":"REPOSITORY","branch":"main","basePath":"BASE"}}"""
        .Replace("OWNER", owner)
        .Replace("REPOSITORY", repository)
        .Replace("BASE", basePath)

let private production =
    configText "acme" "chrona-data" "deployments/prod" |> parseDeploymentConfig |> ok

let private bindingOf config = binding config |> ok

let private manifestPath = Layout.manifestPath |> ok

let private at = DateTimeOffset(2026, 10, 8, 14, 30, 0, TimeSpan.Zero)

let private context (key: string) =
    { Actor =
        { Kind = ActorKind.Human
          Id = ActorId.create "github:octocat" |> ok }
      ProviderIdentity = Some "octocat"
      CorrelationId = CorrelationId.create "req-1" |> ok
      IdempotencyKey = IdempotencyKey.create $"op-{key}-0001" |> ok
      At = at }

let private acme = Organization.create "org_acme" "Acme Consulting" "acme" "America/New_York" at

/// Commits an operation to the in-memory provider.
let private commit (operation: Operation) (state: InMemoryState) =
    let result, next = InMemory.commit operation state
    result, next

let private committed operation state =
    match commit operation state with
    | Ok _, next -> next
    | Error failure, _ -> failwith $"%A{failure}"

let private read ns path state = InMemory.read ns path state |> fst |> ok

/// A deployment with Chrona's namespace and the Acme organization initialized.
let private initialized () =
    let binding = bindingOf production

    let state =
        InMemory.empty
        |> committed (initializeApplication binding RepositoryVisibility.Private None (context "app") |> ok)
        |> committed (initializeOrganization production binding RepositoryVisibility.Private None (context "org") acme |> ok)

    binding, state

// ---- CHX-DATALOC-001: the location is configuration --------------------------

[<Fact>]
let ``the data location comes from deployment configuration, never from code`` () =
    let first = bindingOf production
    let second = configText "other-owner" "time" "" |> parseDeploymentConfig |> ok |> bindingOf

    Assert.Equal("acme/chrona-data", string first.Location.Repository)
    Assert.Equal("deployments/prod/chrona", RelativePath.render (applicationNamespace first |> ok).Root)
    Assert.Equal("other-owner/time", string second.Location.Repository)
    // An empty base path puts Chrona's folder at the repository root, still its own folder.
    Assert.Equal("chrona", RelativePath.render (applicationNamespace second |> ok).Root)
    Assert.Equal(EnvironmentKind.Production, first.Environment.Kind)

[<Fact>]
let ``a configuration that names an unsafe or invalid location is refused`` () =
    let refused text = parseDeploymentConfig text |> codeOf

    Assert.Equal("CHRONA.STORAGE.INVALID_LOCATION", refused (configText "acme" "chrona-data" "../outside"))
    Assert.Equal("CHRONA.STORAGE.INVALID_LOCATION", refused (configText "acme" "chrona-data" ".github"))
    Assert.Equal("CHRONA.STORAGE.INVALID_LOCATION", refused (configText "-acme" "chrona-data" ""))
    Assert.Equal("CHRONA.STORAGE.INVALID_CONFIGURATION", refused "not json")
    Assert.Equal("CHRONA.STORAGE.INVALID_CONFIGURATION", refused """{"environment":"production","environmentName":"p"}""")

    Assert.Equal(
        "CHRONA.STORAGE.INVALID_CONFIGURATION",
        refused """{"environment":"prod","environmentName":"p","location":{"owner":"a","repository":"b","branch":"main","basePath":""}}"""
    )

    Assert.Equal(
        "CHRONA.STORAGE.INVALID_CONFIGURATION",
        refused """{"environment":"test","environmentName":"t","token":"x","location":{"owner":"a","repository":"b","branch":"main","basePath":""}}"""
    )

    Assert.Equal(
        "CHRONA.ENTRY.MISSING_FIELD",
        refused """{"environment":"test","environmentName":" ","location":{"owner":"a","repository":"b","branch":"main","basePath":""}}"""
    )

// ---- CHX-DATALOC-002/003, 2.4: Chrona's own folder in a shared repository -----

[<Fact>]
let ``Chrona keeps to its own folder and shares a repository with other applications`` () =
    let chrona = bindingOf production

    let summa =
        { chrona with
            Application = AppId.create "summa" |> ok }

    // Applications side by side under one base path do not overlap.
    let spaces = Deployment.namespaces [ chrona; summa ] |> ok
    Assert.Equal<string list>([ "deployments/prod/chrona"; "deployments/prod/summa" ], spaces |> List.map (fun ns -> RelativePath.render ns.Root))

    // No path Chrona resolves leaves its folder, and it never writes the folder itself.
    let ns = applicationNamespace chrona |> ok
    Assert.True(Namespace.resolveText ns "../summa/records/x.json" |> Result.isError)
    Assert.True(Namespace.resolveText ns "" |> Result.isError)
    Assert.Equal("deployments/prod/chrona/records/a.json", (Namespace.resolveText ns "records/a.json" |> ok).Path)

    // The other application's objects are outside Chrona's namespace.
    let summaObject =
        { Repository = chrona.Location.Repository
          Branch = chrona.Location.Branch
          Path = "deployments/prod/summa/records/invoice.json" }

    Assert.Equal(None, Namespace.relativeOf ns summaObject)

// ---- 2.5 and CHX-DATALOC-004: organizations, their folders and repositories ---

[<Fact>]
let ``each organization has its own folder named by its immutable id`` () =
    let binding = bindingOf production
    let acmeSpace = organizationNamespace production binding "org_acme" |> ok
    let globex = organizationNamespace production binding "org_globex" |> ok

    Assert.Equal("deployments/prod/chrona/datasets/org_acme", RelativePath.render acmeSpace.Root)
    Assert.Equal("deployments/prod/chrona/datasets/org_globex", RelativePath.render globex.Root)
    Assert.Equal("CHRONA.STORAGE.INVALID_ORGANIZATION_ID", organizationNamespace production binding "../org" |> codeOf)
    Assert.Equal("CHRONA.STORAGE.INVALID_ORGANIZATION_ID", organizationNamespace production binding "Acme Consulting" |> codeOf)

[<Fact>]
let ``an organization can keep its data in its own repository, for its own permissions`` () =
    let text =
        """{"environment":"production","environmentName":"production","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments/prod"},"organizations":{"org_eu":{"owner":"acme-eu","repository":"chrona-eu","branch":"main","basePath":""}}}"""

    let config = parseDeploymentConfig text |> ok
    let binding = bindingOf config
    let eu = organizationNamespace config binding "org_eu" |> ok
    let home = organizationNamespace config binding "org_acme" |> ok

    Assert.Equal("acme-eu/chrona-eu", string eu.Location.Repository)
    Assert.Equal("chrona/datasets/org_eu", RelativePath.render eu.Root)
    Assert.Equal("acme/chrona-data", string home.Location.Repository)

    // An organization override must name a valid organization and location.
    Assert.Equal(
        "CHRONA.STORAGE.INVALID_ORGANIZATION_ID",
        text.Replace("\"org_eu\"", "\"org eu\"") |> parseDeploymentConfig |> codeOf
    )

    Assert.Equal("CHRONA.STORAGE.INVALID_LOCATION", text.Replace("\"chrona-eu\"", "\"..\"") |> parseDeploymentConfig |> codeOf)

// ---- 2.6: the organization manifest -------------------------------------------

[<Fact>]
let ``initializing stores the organization manifest, which reads back exactly`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok

    Assert.Equal("records/chrona.organization/org_acme.json", RelativePath.render path)

    let manifest = read ns path state |> Organization.read "org_acme" |> ok
    Assert.Equal(acme, manifest)
    Assert.Equal("org_acme", manifest.OrganizationId)
    Assert.Equal("Acme Consulting", manifest.DisplayName)
    Assert.Equal("acme", manifest.Slug)
    Assert.Equal(Organization.StorageVersion, manifest.StorageVersion)
    Assert.Equal("America/New_York", manifest.DefaultTimeZone)
    Assert.Equal(DayOfWeek.Monday, manifest.WeekStart)
    Assert.Equal(6, manifest.DefaultBillingPolicy.IncrementMinutes)
    Assert.False(manifest.Approval.ApprovalRequired)
    Assert.Equal("records/chrona.reference", manifest.References)
    Assert.Equal("records/chrona.configuration", manifest.Configuration)
    Assert.Equal(at, manifest.CreatedAt)

    // The folders open cleanly: each Arca manifest matches the configured namespace.
    Assert.True(openNamespace ns (read ns manifestPath state) |> Result.isOk)
    let app = applicationNamespace binding |> ok
    Assert.True(openNamespace app (read app manifestPath state) |> Result.isOk)

[<Fact>]
let ``every manifest field survives a round trip`` () =
    for weekStart in Enum.GetValues<DayOfWeek>() do
        for rule in [ Chrona.Domain.Billing.Up; Chrona.Domain.Billing.Down; Chrona.Domain.Billing.Nearest; Chrona.Domain.Billing.Exact ] do
            let manifest =
                { acme with
                    WeekStart = weekStart
                    Approval =
                        { SubmissionExpected = true
                          ApprovalRequired = weekStart = DayOfWeek.Friday }
                    DefaultBillingPolicy =
                        { acme.DefaultBillingPolicy with
                            Rounding = rule
                            IncrementMinutes = 15
                            EffectiveFrom = DateOnly(2026, 1, 1) } }

            let stored =
                ReadOutcome.Found
                    { Path = Organization.path "org_acme" |> ok
                      Content = Organization.encode manifest |> ok
                      Revision = Revision "r1" }

            Assert.Equal(manifest, Organization.read "org_acme" stored |> ok)

[<Fact>]
let ``an invalid manifest is never stored`` () =
    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_SLUG" ], Organization.encode { acme with Slug = "Acme Co" } |> codes)
    Assert.Equal<string list>([ "CHRONA.ENTRY.MISSING_FIELD" ], Organization.encode { acme with DisplayName = " " } |> codes)

    Assert.Equal<string list>(
        [ "CHRONA.STORAGE.INVALID_ORGANIZATION_MANIFEST" ],
        Organization.encode
            { acme with
                DefaultBillingPolicy = Chrona.Domain.Billing.legacyDefault "org_other" }
        |> codes
    )

    Assert.Equal<string list>([ "CHRONA.STORAGE.UNSUPPORTED_VERSION" ], Organization.encode { acme with StorageVersion = 2 } |> codes)

[<Fact>]
let ``renaming changes the name and slug, never the id or the folder`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok

    let revision =
        match read ns path state with
        | ReadOutcome.Found found -> found.Revision
        | ReadOutcome.Absent -> failwith "absent"

    let renamed =
        renameOrganization ns (context "rename") revision "Acme Partners" "acme-partners" acme |> ok

    let after = committed renamed state
    let manifest = read ns path after |> Organization.read "org_acme" |> ok

    Assert.Equal("Acme Partners", manifest.DisplayName)
    Assert.Equal("acme-partners", manifest.Slug)
    Assert.Equal("org_acme", manifest.OrganizationId)
    Assert.Equal("deployments/prod/chrona/datasets/org_acme", RelativePath.render ns.Root)

    // A rename built on a manifest that has changed since is a conflict, not an overwrite.
    let stale =
        renameOrganization ns (context "stale") revision "Acme Again" "acme-again" acme |> ok

    match commit stale after with
    | Error(StorageFailure.Conflicted _), _ -> ()
    | other -> failwith $"expected a conflict, got %A{fst other}"

[<Fact>]
let ``initializing an organization twice is a conflict and overwrites nothing`` () =
    let binding, state = initialized ()

    let again =
        initializeOrganization production binding RepositoryVisibility.Private None (context "again") { acme with DisplayName = "Imposter" }
        |> ok

    match commit again state with
    | Error(StorageFailure.Conflicted _), unchanged ->
        let ns = organizationNamespace production binding "org_acme" |> ok
        let manifest = read ns (Organization.path "org_acme" |> ok) unchanged |> Organization.read "org_acme" |> ok
        Assert.Equal("Acme Consulting", manifest.DisplayName)
    | other -> failwith $"expected a conflict, got %A{fst other}"

// ---- 2.7: production repository safety ----------------------------------------

[<Fact>]
let ``production data is not initialized into a public repository without a recorded decision`` () =
    let binding = bindingOf production
    let initialize visibility decision = initializeOrganization production binding visibility decision (context "pub") acme

    Assert.Equal<string list>([ "CHRONA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY" ], initialize RepositoryVisibility.Public None |> codes)
    Assert.Equal<string list>([ "CHRONA.STORAGE.PUBLIC_PRODUCTION_REPOSITORY" ], initializeApplication binding RepositoryVisibility.Public None (context "pub") |> codes)
    Assert.Equal<string list>([ "CHRONA.STORAGE.OVERRIDE_WITHOUT_REASON" ], initialize RepositoryVisibility.Public (Some { Reason = " " }) |> codes)
    Assert.True(initialize RepositoryVisibility.Public (Some { Reason = "open-source demonstration data" }) |> Result.isOk)
    Assert.True(initialize RepositoryVisibility.Private None |> Result.isOk)
    Assert.True(initialize RepositoryVisibility.Internal None |> Result.isOk)

    // Only production is guarded: a staging deployment may use a public repository.
    Assert.Equal("ok", permitsInitialization EnvironmentKind.Staging RepositoryVisibility.Public None |> codeOf)

// ---- Opening: the configured namespace must be the one the data is in ---------

[<Fact>]
let ``a folder that is not initialized, belongs to another application, or was copied elsewhere is not opened`` () =
    let binding, state = initialized ()
    let app = applicationNamespace binding |> ok

    // Nothing there yet.
    let fresh = configText "acme" "chrona-data" "deployments/staging" |> parseDeploymentConfig |> ok |> bindingOf
    let freshSpace = applicationNamespace fresh |> ok
    Assert.Equal<string list>([ "CHRONA.STORAGE.NAMESPACE_NOT_INITIALIZED" ], openNamespace freshSpace (read freshSpace manifestPath state) |> codes)

    // Chrona's manifest copied by hand into another location: moving data is a
    // migration, never a configuration edit (ARCA-LOC-009).
    let content =
        match read app manifestPath state with
        | ReadOutcome.Found found -> found.Content
        | ReadOutcome.Absent -> failwith "absent"

    let copied = InMemory.writeExternally fresh.Location "deployments/staging/chrona/arca-manifest.json" (Some content) state
    Assert.Equal<string list>([ "CHRONA.STORAGE.NAMESPACE_UNUSABLE" ], openNamespace freshSpace (read freshSpace manifestPath copied) |> codes)

    // An organization folder's manifest is not an application manifest.
    let orgSpace = organizationNamespace production binding "org_acme" |> ok
    let orgManifest = read orgSpace manifestPath state

    let asApplication =
        match orgManifest with
        | ReadOutcome.Found found -> ReadOutcome.Found { found with Path = manifestPath }
        | absent -> absent

    Assert.Contains("CHRONA.STORAGE.NAMESPACE_UNUSABLE", openNamespace app asApplication |> codes)

[<Fact>]
let ``a manifest edited outside Chrona is reported, not trusted`` () =
    let binding, state = initialized ()
    let ns = organizationNamespace production binding "org_acme" |> ok
    let path = Organization.path "org_acme" |> ok
    let full = (Namespace.resolve ns path |> ok).Path
    let edit content = InMemory.writeExternally ns.Location full (Some content) state
    let readAfter content = read ns path (edit content) |> Organization.read "org_acme" |> codes

    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_RECORD" ], readAfter "{ not json")

    // Valid JSON, but not canonical (hand-formatted).
    let content =
        match read ns path state with
        | ReadOutcome.Found found -> found.Content
        | ReadOutcome.Absent -> failwith "absent"

    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_RECORD" ], readAfter (content.Replace(",", ", ")))

    // Another organization's manifest placed in this folder.
    let other = Organization.encode { acme with OrganizationId = "org_other"; DefaultBillingPolicy = Chrona.Domain.Billing.legacyDefault "org_other" } |> ok
    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_RECORD" ], readAfter other)

    // A field this version does not define.
    let extra = content.Replace("\"approval\":", "\"admin\":true,\"approval\":")
    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_ORGANIZATION_MANIFEST" ], readAfter extra)

    // A record from a newer Chrona.
    Assert.Equal<string list>([ "CHRONA.STORAGE.INVALID_RECORD" ], readAfter (content.Replace("\"schemaVersion\":1", "\"schemaVersion\":2")))

    // And a missing manifest says the organization is not initialized.
    Assert.Equal<string list>(
        [ "CHRONA.STORAGE.ORGANIZATION_NOT_INITIALIZED" ],
        Organization.read "org_acme" ReadOutcome.Absent |> codes
    )

[<Fact>]
let ``nothing that looks like a credential is written`` () =
    let binding = bindingOf production

    let leaked =
        initializeOrganization production binding RepositoryVisibility.Private None (context "leak") { acme with DisplayName = "ghp_" + String('a', 36) }

    Assert.Equal<string list>([ "CHRONA.STORAGE.OPERATION_REFUSED" ], leaked |> codes)

// ---- The provider these tests use is a conforming Arca provider ---------------

[<Fact>]
let ``the in-memory provider Chrona's storage tests use passes Arca's conformance suite`` () =
    let ns = applicationNamespace (bindingOf production) |> ok
    let results = Conformance.run (fun () -> Conformance.inMemory ns) |> Async.RunSynchronously

    Assert.NotEmpty(results)

    let failures =
        results
        |> List.choose (fun result ->
            match result.Outcome with
            | ConformanceOutcome.Passed -> None
            | outcome -> Some $"{result.Case} ({result.Requirement}): %A{outcome}")

    Assert.True(failures.IsEmpty, String.concat "\n" failures)
