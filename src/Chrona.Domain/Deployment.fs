/// A deployment's configuration (CHX-DATALOC-001, CHX-022): where its data
/// lives and how people sign in. Every value comes from the deployment's
/// configuration document; Chrona hard-codes no repository, owner, branch,
/// base path, sign-in host or client id.
///
/// ```json
/// {"environment":"production","environmentName":"production",
///  "location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments/prod"},
///  "identity":{"exchange":"https://fides.acme.example","application":"chrona-production",
///              "provider":"github","clientId":"Iv23li...","redirectUri":"https://chrona.acme.example/"},
///  "organizations":[
///    {"id":"org_acme","displayName":"Acme Consulting","slug":"acme","timeZone":"America/New_York"},
///    {"id":"org_eu","displayName":"Acme Europe","slug":"acme-eu","timeZone":"Europe/Berlin",
///     "location":{"owner":"acme-eu","repository":"chrona-eu","branch":"main","basePath":""}}]}
/// ```
///
/// `location`, `identity` and `organizations` are optional: a local
/// deployment configures neither storage nor sign-in and runs as a local
/// session in memory. A deployment that stores data on GitHub needs sign-in
/// (someone must write it) and names the organizations it serves; the first
/// is the default, and an organization may keep its data in its own
/// repository (2.5). The document is closed and every value validated.
/// Nothing in it is secret: the client id is public and the client secret
/// stays with the exchange.
///
/// Pure.
module Chrona.Domain.Deployment

open System
open Arca
open Chrona.Domain.Diagnostics

/// One configured data location, as deployment configuration states it.
type LocationConfig =
    { Owner: string
      Repository: string
      Branch: string
      /// The folder application namespaces live under; empty for the repository root.
      BasePath: string }

/// How people sign in (CHX-022): Fides' exchange and the registration of
/// this deployment with it.
type IdentityConfig =
    { /// The exchange's origin, for example `https://fides.acme.example`.
      Exchange: string
      /// The application's id as registered with the exchange.
      Application: string
      /// The identity provider, by id: `github`.
      Provider: string
      /// The provider's public OAuth client id for the exchange.
      ClientId: string
      /// The exact redirect URI registered for this deployment: the page the
      /// provider sends the person back to.
      RedirectUri: string }

/// The organization a deployment serves: its immutable id, the names people
/// see, and its business time zone (requirements expansion 2.5, 2.6).
type OrganizationConfig =
    { Id: string
      DisplayName: string
      Slug: string
      TimeZone: string
      /// Where this organization's data lives when not at the deployment's location.
      Location: LocationConfig option
      /// The GitHub numeric account ids who may set the organization up and
      /// be its first administrators. No one else ever becomes one by
      /// opening it.
      Administrators: string list }

/// A deployment's configuration.
/// What signing out does with this account's unsent changes on a device
/// others may use (WI-0058). Either way nothing is lost silently and
/// nothing is left behind unknowingly.
type SharedDevicePolicy =
    /// The person chooses: send them now, keep them on this device for this
    /// account, or discard them after a confirmation. The default.
    | Ask
    /// Nothing is kept on the device: the person sends them now or discards
    /// them after a confirmation.
    | DiscardOnSignOut

type DeploymentConfig =
    { Environment: EnvironmentKind
      /// A display name for the environment, for example "production".
      EnvironmentName: string
      /// Where the data lives; None for a deployment without storage.
      Location: LocationConfig option
      /// How people sign in; None for a local session.
      Identity: IdentityConfig option
      /// The organizations the deployment serves, the default first.
      Organizations: OrganizationConfig list
      /// What signing out does with unsent changes (`sharedDevicePolicy`).
      SharedDevice: SharedDevicePolicy }

/// The organization a session starts in: the deployment's first, or
/// `local` for a deployment that names none.
let organizationId (config: DeploymentConfig) =
    config.Organizations |> List.tryHead |> Option.map _.Id |> Option.defaultValue "local"

/// A configured organization, by id.
let organization (config: DeploymentConfig) (organizationId: string) =
    config.Organizations |> List.tryFind (fun found -> found.Id = organizationId)

/// The identity providers Chrona signs in with.
let providers = [ "github" ]

/// A location's Arca form, or why it is not a valid location.
let dataLocation (config: LocationConfig) =
    DataLocation.create config.Owner config.Repository config.Branch config.BasePath

let private invalid detail = Error(InvalidDeploymentConfig detail)

let private text name value =
    match Json.field name value with
    | Some(Json.String found) -> Ok found
    | Some _ -> invalid $"'{name}' is not text"
    | None -> invalid $"'{name}' is missing"

let private closed (names: string list) value =
    match value with
    | Json.Object members ->
        match members |> List.tryFind (fun (key, _) -> not (List.contains key names)) with
        | Some(key, _) -> invalid $"'{key}' is not a configuration field"
        | None -> Ok members
    | _ -> invalid "expected an object"

let private locationOf value =
    closed [ "basePath"; "branch"; "owner"; "repository" ] value
    |> Result.bind (fun _ ->
        match text "owner" value, text "repository" value, text "branch" value, text "basePath" value with
        | Ok owner, Ok repository, Ok branch, Ok basePath ->
            let config =
                { Owner = owner
                  Repository = repository
                  Branch = branch
                  BasePath = basePath }

            dataLocation config
            |> Result.mapError (LocationError.describe >> InvalidDataLocation)
            |> Result.map (fun _ -> config)
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
    | other -> invalid $"'{other}' is not local, test, staging or production"

let private loopback (uri: Uri) =
    uri.IsLoopback && (uri.Host = "localhost" || uri.Host = "127.0.0.1" || uri.Host = "[::1]")

/// An absolute https address (http only on this machine, for development),
/// with no user information; `originOnly` also refuses a path, query or fragment.
let private address (name: string) (originOnly: bool) (value: string) =
    let parsed =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, found -> Option.ofObj found
        | _ -> None

    match parsed with
    | Some uri ->
        let secure = uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && loopback uri)

        if not secure then
            invalid $"'{name}' must be an https address"
        elif uri.UserInfo <> "" then
            invalid $"'{name}' must not hold user information"
        elif originOnly && (uri.AbsolutePath <> "/" || uri.Query <> "" || uri.Fragment <> "" || value.EndsWith "/") then
            invalid $"'{name}' must be an origin, with no path"
        elif not originOnly && (uri.Query <> "" || uri.Fragment <> "") then
            invalid $"'{name}' must not have a query or fragment"
        else
            Ok value
    | None -> invalid $"'{name}' is not an absolute address"

let private identifier (name: string) (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length > 200 || value |> Seq.exists (fun c -> Char.IsWhiteSpace c || Char.IsControl c) then
        invalid $"'{name}' is not an identifier"
    else
        Ok value

let private identityOf value =
    match Json.field "identity" value with
    | None -> Ok None
    | Some identity ->
        closed [ "application"; "clientId"; "exchange"; "provider"; "redirectUri" ] identity
        |> Result.bind (fun _ ->
            match
                text "exchange" identity |> Result.bind (address "exchange" true),
                text "application" identity |> Result.bind (identifier "application"),
                text "provider" identity,
                text "clientId" identity |> Result.bind (identifier "clientId"),
                text "redirectUri" identity |> Result.bind (address "redirectUri" false)
            with
            | Ok exchange, Ok application, Ok provider, Ok clientId, Ok redirectUri ->
                if List.contains provider providers then
                    Ok(
                        Some
                            { Exchange = exchange
                              Application = application
                              Provider = provider
                              ClientId = clientId
                              RedirectUri = redirectUri }
                    )
                else
                    invalid $"'{provider}' is not an identity provider Chrona signs in with"
            | Error e, _, _, _, _
            | _, Error e, _, _, _
            | _, _, Error e, _, _
            | _, _, _, Error e, _
            | _, _, _, _, Error e -> Error e)

/// GitHub numeric account ids: digits only, each once.
let private administratorsOf (value: Json) =
    match Json.field "administrators" value with
    | None -> Ok []
    | Some(Json.Array items) ->
        let ids =
            items
            |> List.map (function
                | Json.String id when id <> "" && id.Length <= 20 && id |> Seq.forall Char.IsAsciiDigit -> Ok id
                | _ -> invalid "'administrators' holds something other than a GitHub account number")

        match ids |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
        | Some error -> Error error
        | None ->
            let found = ids |> List.choose Result.toOption

            if (List.distinct found).Length <> found.Length then
                invalid "'administrators' names an account twice"
            else
                Ok found
    | Some _ -> invalid "'administrators' is not a list"

/// Whether a session's actor (`github:<id>`) is one of the organization's
/// bootstrap administrators.
let isBootstrapAdministrator (organization: OrganizationConfig) (actorId: string) =
    match actorId.Split(':', 2) with
    | [| "github"; id |] -> List.contains id organization.Administrators
    | _ -> false

let private organizationOf (value: Json) =
    closed [ "administrators"; "displayName"; "id"; "location"; "slug"; "timeZone" ] value
    |> Result.bind (fun _ ->
        match text "id" value, text "displayName" value, text "slug" value, text "timeZone" value with
        | Ok id, Ok displayName, Ok slug, Ok zone ->
            let manifest = Organization.create id displayName slug zone DateTimeOffset.UnixEpoch

            match Organization.problems manifest with
            | problem :: _ -> Error problem
            | [] ->
                match Json.field "location" value with
                | None -> Ok None
                | Some found -> locationOf found |> Result.map Some
                |> Result.bind (fun location ->
                    administratorsOf value
                    |> Result.map (fun administrators ->
                        { Id = id
                          DisplayName = displayName
                          Slug = slug
                          TimeZone = zone
                          Location = location
                          Administrators = administrators }))
        | Error e, _, _, _
        | _, Error e, _, _
        | _, _, Error e, _
        | _, _, _, Error e -> Error e)

let private organizationsOf value =
    match Json.field "organizations" value with
    | None -> Ok []
    | Some(Json.Array items) ->
        items
        |> List.fold (fun state item -> state |> Result.bind (fun found -> organizationOf item |> Result.map (fun next -> found @ [ next ]))) (Ok [])
        |> Result.bind (fun organizations ->
            match organizations |> List.countBy _.Id |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(id, _) -> invalid $"'{id}' is configured twice"
            | None -> Ok organizations)
    | Some _ -> invalid "'organizations' is not a list"

/// A deployment's configuration from its JSON text, every value validated.
let parse (text': string) : Result<DeploymentConfig, Diagnostic> =
    match Json.parse text' with
    | Error error -> invalid (JsonError.describe error)
    | Ok value ->
        closed [ "environment"; "environmentName"; "identity"; "location"; "organizations"; "sharedDevicePolicy" ] value
        |> Result.bind (fun _ ->
            match text "environment" value |> Result.bind environmentOf, text "environmentName" value with
            | Ok _, Ok name when String.IsNullOrWhiteSpace name -> Error(MissingField "environmentName")
            | Ok environment, Ok environmentName ->
                let location =
                    match Json.field "location" value with
                    | None -> Ok None
                    | Some found -> locationOf found |> Result.map Some

                let sharedDevice =
                    match Json.field "sharedDevicePolicy" value with
                    | None -> Ok Ask
                    | Some(Json.String "ask") -> Ok Ask
                    | Some(Json.String "discardOnSignOut") -> Ok DiscardOnSignOut
                    | Some _ -> invalid "'sharedDevicePolicy' is 'ask' or 'discardOnSignOut'"

                match location, organizationsOf value, identityOf value, sharedDevice with
                | Ok location, Ok organizations, Ok identity, Ok sharedDevice ->
                    if location.IsNone && not organizations.IsEmpty then
                        invalid "'organizations' needs a 'location'"
                    elif location.IsSome && (identity.IsNone || organizations.IsEmpty) then
                        // Data on GitHub needs someone signed in to write it,
                        // and an organization to keep it under.
                        invalid "a 'location' needs 'identity' and 'organizations'"
                    else
                        Ok
                            { Environment = environment
                              EnvironmentName = environmentName
                              Location = location
                              Identity = identity
                              Organizations = organizations
                              SharedDevice = sharedDevice }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e
            | Error e, _
            | _, Error e -> Error e)
