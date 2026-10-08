/// A deployment's configuration (CHX-DATALOC-001, CHX-022): where its data
/// lives and how people sign in. Every value comes from the deployment's
/// configuration document; Chrona hard-codes no repository, owner, branch,
/// base path, sign-in host or client id.
///
/// ```json
/// {"environment":"production","environmentName":"production",
///  "location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":"deployments/prod"},
///  "organizations":{"org_2":{"owner":"acme-eu","repository":"chrona-eu","branch":"main","basePath":""}},
///  "identity":{"exchange":"https://fides.acme.example","application":"chrona-production",
///              "provider":"github","clientId":"Iv23li...","redirectUri":"https://chrona.acme.example/"}}
/// ```
///
/// `location`, `organizations` and `identity` are optional: a local
/// deployment configures neither storage nor sign-in and runs as a local
/// session in memory. The document is closed and every value validated.
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

/// A deployment's configuration.
type DeploymentConfig =
    { Environment: EnvironmentKind
      /// A display name for the environment, for example "production".
      EnvironmentName: string
      /// Where the data lives; None for a deployment without storage.
      Location: LocationConfig option
      /// Organizations whose data lives somewhere other than `Location`, by OrganizationId.
      OrganizationLocations: Map<string, LocationConfig>
      /// How people sign in; None for a local session.
      Identity: IdentityConfig option }

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
                    |> Result.bind (fun _ -> locationOf entry)
                    |> Result.map (fun entry -> Map.add organizationId entry found)))
            (Ok Map.empty)
    | Some _ -> invalid "'organizations' is not an object"

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

/// A deployment's configuration from its JSON text, every value validated.
let parse (text': string) : Result<DeploymentConfig, Diagnostic> =
    match Json.parse text' with
    | Error error -> invalid (JsonError.describe error)
    | Ok value ->
        closed [ "environment"; "environmentName"; "identity"; "location"; "organizations" ] value
        |> Result.bind (fun _ ->
            match text "environment" value |> Result.bind environmentOf, text "environmentName" value with
            | Ok _, Ok name when String.IsNullOrWhiteSpace name -> Error(MissingField "environmentName")
            | Ok environment, Ok environmentName ->
                let location =
                    match Json.field "location" value with
                    | None -> Ok None
                    | Some found -> locationOf found |> Result.map Some

                match location, organizationsOf value, identityOf value with
                | Ok location, Ok organizations, Ok identity ->
                    if location.IsNone && not organizations.IsEmpty then
                        invalid "'organizations' needs a 'location'"
                    else
                        Ok
                            { Environment = environment
                              EnvironmentName = environmentName
                              Location = location
                              OrganizationLocations = organizations
                              Identity = identity }
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
            | Error e, _
            | _, Error e -> Error e)
