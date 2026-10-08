/// Sign-in through Fides (WI-0029): CHX-022 (the identity comes from GitHub,
/// never typed) and CHX-023 (tokens stay out of everything Chrona shows,
/// stores or sends, and sign-out clears them). The deployment's identity
/// configuration, the engine's sign-in states, and the real Fides client
/// driven through the Limen boundary against a fake browser and a fake
/// exchange that speaks Fides' wire protocol (EXCHANGE-PROTOCOL.md).
module Chrona.Tests.SignInTests

open System
open System.Collections.Generic
open System.Text.Json.Nodes
open Xunit
open Aegis
open Chrona.Engine.View
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application

module Deployment = Chrona.Domain.Deployment
module Diagnostics = Chrona.Domain.Diagnostics

let private identitySection (exchange: string) (redirect: string) =
    $$"""{"exchange":"{{exchange}}","application":"chrona-test","provider":"github","clientId":"Iv23liTEST","redirectUri":"{{redirect}}"}"""

let private configured (identity: string) =
    $$"""{"environment":"test","environmentName":"test","identity":{{identity}}}"""

let private signInConfiguration =
    configured (identitySection "https://fides.test" "http://127.0.0.1:4321/web/index.html")

let private codeOf =
    function
    | Ok _ -> "ok"
    | Error diagnostic -> Diagnostics.code diagnostic

// ---- The deployment says how people sign in (CHX-022) --------------------------

[<Fact>]
let ``the sign-in host and client id come only from the deployment's configuration`` () =
    let config = Deployment.parse signInConfiguration |> Result.defaultWith (fun e -> failwith $"{e}")

    match config.Identity with
    | Some identity ->
        Assert.Equal("https://fides.test", identity.Exchange)
        Assert.Equal("chrona-test", identity.Application)
        Assert.Equal("github", identity.Provider)
        Assert.Equal("Iv23liTEST", identity.ClientId)
        Assert.Equal("http://127.0.0.1:4321/web/index.html", identity.RedirectUri)
    | None -> failwith "no identity"

    // Without an identity section the deployment runs as a local session.
    Assert.Equal(None, (Deployment.parse """{"environment":"local","environmentName":"local"}""" |> Result.map _.Identity |> Result.defaultValue None))

[<Fact>]
let ``an identity configuration that is not safe is refused`` () =
    let refused identity = configured identity |> Deployment.parse |> codeOf
    let invalid = "CHRONA.STORAGE.INVALID_CONFIGURATION"

    Assert.Equal(invalid, refused (identitySection "http://fides.example.com" "https://chrona.example.com/"))
    Assert.Equal(invalid, refused (identitySection "https://fides.test/v1" "https://chrona.example.com/"))
    Assert.Equal(invalid, refused (identitySection "https://user:pw@fides.test" "https://chrona.example.com/"))
    Assert.Equal(invalid, refused (identitySection "https://fides.test" "https://chrona.example.com/?next=x"))
    Assert.Equal(invalid, refused (identitySection "https://fides.test" "chrona.example.com"))
    Assert.Equal(invalid, refused ((identitySection "https://fides.test" "https://chrona.example.com/").Replace("github", "gitlab")))
    // A client secret belongs to the exchange; the page's configuration has no place for one.
    Assert.Equal(invalid, refused ((identitySection "https://fides.test" "https://chrona.example.com/").Replace("{", """{"clientSecret":"s3cret",""")))
    // http is accepted only on this machine, for development.
    Assert.Equal("ok", refused (identitySection "http://localhost:8787" "http://localhost:4321/"))

// ---- The engine's sign-in states ------------------------------------------------------

let private start = DateTimeOffset(2026, 10, 8, 14, 10, 0, TimeSpan.FromHours -4.0)
let private ids = ref 0

let private ctx =
    { Now = start
      NewId = fun prefix -> $"{prefix}-{Threading.Interlocked.Increment ids}" }

let private step msg (model: Model, _) = update ctx msg model

let private flagOf key (model: Model) =
    match Project.project model |> Map.ofList |> Map.find key with
    | Value(Flag f) -> f
    | other -> failwith $"{key} is {other}"

let private textOf key (model: Model) =
    match Project.project model |> Map.ofList |> Map.find key with
    | Value(Text t) -> t
    | other -> failwith $"{key} is {other}"

let private octocat =
    { ActorId = "github:583231"
      OrganizationId = "local"
      DisplayName = "octocat"
      Kind = SignedIn "github" }

let private opened (query: (string * string) list) =
    (Model.initial noOne InMemory start, [])
    |> step (Started("#/today", query))
    |> step (EnvironmentDescribed "America/New_York")
    |> step (ConfigurationRead(Some signInConfiguration))

[<Fact>]
let ``a configured deployment asks for sign-in and does nothing for anyone before it`` () =
    let model, effects = opened []

    match effects with
    | [ BeginIdentity(identity, []) ] -> Assert.Equal("https://fides.test", identity.Exchange)
    | other -> failwith $"{other}"

    Assert.True(flagOf "screenSignIn" model)
    Assert.False(flagOf "shellVisible" model)

    // The provider's answer arrives: no one is signed in.
    let model, _ = (model, []) |> step (IdentityChanged(SignedOutWith None))
    Assert.False(flagOf "signInBusy" model)

    // Work events are ignored until someone signs in.
    let model, effects = (model, []) |> step (Ui("newProjectName", None, "Secret project", None)) |> step (Ui("addProject", None, "", None))
    Assert.Empty(effects)
    Assert.True(model.References.Items.IsEmpty)

    // Session-only retention is the default; the person may keep it for the tab.
    Assert.True(flagOf "retentionPage" model)
    let model, _ = (model, []) |> step (Ui("signInRetention", None, "tab", None))
    Assert.True(flagOf "retentionTab" model)
    let model, effects = (model, []) |> step (Ui("signIn", None, "", None))
    Assert.Equal<Effect list>([ SignIn ThisTab ], effects)
    Assert.True(flagOf "signInBusy" model)
    Assert.Equal("Signing in…", textOf "signInButton" model)

    // A second press while the first is under way does nothing.
    let _, effects = (model, []) |> step (Ui("signIn", None, "", None))
    Assert.Empty(effects)

[<Fact>]
let ``the provider's callback is completed with the configuration, and the identity is the provider's`` () =
    let callback = [ "code", "abc"; "state", "xyz" ]
    let model, effects = opened callback

    match effects with
    | [ BeginIdentity(_, query) ] -> Assert.Equal<(string * string) list>(callback, query)
    | other -> failwith $"{other}"

    let model, _ = (model, []) |> step (IdentityChanged(SignedInAs octocat))
    Assert.True(flagOf "shellVisible" model)
    Assert.Equal("github:583231", model.Session.ActorId)
    Assert.Equal("octocat", textOf "accountLogin" model)
    Assert.Equal("Signed in as octocat.", model.Announcement)
    Assert.True(flagOf "accountSignedIn" model)

[<Fact>]
let ``signing out leaves nothing of the person in the page`` () =
    let model, _ =
        opened []
        |> step (IdentityChanged(SignedInAs octocat))
        |> step (Ui("newProjectName", None, "HelixNote", None))
        |> step (Ui("addProject", None, "", None))

    Assert.False(model.References.Items.IsEmpty)
    let model, effects = (model, []) |> step (Ui("signOut", None, "", None))
    Assert.Equal<Effect list>([ SignOut ], effects)

    let model, _ = (model, []) |> step (IdentityChanged(SignedOutWith(Some "signed_out")))
    Assert.True(model.References.Items.IsEmpty)
    Assert.Equal("", model.Session.DisplayName)
    Assert.True(flagOf "screenSignIn" model)
    Assert.Equal("signed_out", textOf "signInNoticeCode" model)
    Assert.Contains("no longer holds your GitHub token", textOf "signInNotice" model)

[<Fact>]
let ``sign-in outcomes are told by their stable codes`` () =
    for code in [ "state_invalid"; "state_expired"; "provider_denied"; "expired"; "revoked"; "repository_access_denied" ] do
        let model, _ = opened [] |> step (IdentityChanged(SignedOutWith(Some code)))
        Assert.True(flagOf "hasSignInNotice" model)
        Assert.Equal(code, textOf "signInNoticeCode" model)
        Assert.Equal(Project.noticeText code, textOf "signInNotice" model)

    // While signed in, an unreachable provider is a notice, and the session is kept.
    let model, _ = opened [] |> step (IdentityChanged(SignedInAs octocat)) |> step (IdentityChanged ProviderUnavailable)
    Assert.True(flagOf "hasSessionNotice" model)
    Assert.True(flagOf "shellVisible" model)

[<Fact>]
let ``a deployment whose configuration cannot be used says so and runs nothing`` () =
    let model, effects =
        (Model.initial noOne InMemory start, [])
        |> step (Started("", []))
        |> step (ConfigurationRead(Some(configured (identitySection "http://fides.example.com" "https://x.example.com/"))))

    Assert.Empty(effects)
    Assert.True(flagOf "screenMisconfigured" model)
    Assert.False(flagOf "shellVisible" model)
    Assert.Contains("CHRONA.STORAGE.INVALID_CONFIGURATION", textOf "misconfiguredDetail" model)

    let model, _ = (Model.initial noOne InMemory start, []) |> step (Started("", [])) |> step (ConfigurationRead None)
    Assert.True(flagOf "screenMisconfigured" model)

// ---- The real Fides client through the Limen boundary ---------------------------------

/// An access token no one could mistake for anything else, to look for.
let private accessToken = "gho_CHRONATESTACCESSTOKEN0123456789"
let private refreshToken = "ghr_CHRONATESTREFRESHTOKEN0123456789"

/// A browser tab and the exchange behind it: answers every request Chrona's
/// application makes, recording what it was asked.
type private Browser(clock: DateTimeOffset ref) =
    member val Tab = Dictionary<string, string>()
    member val Device = Dictionary<string, string>()
    member val Posts = List<string * string>()
    member val Left = List<string>()
    member val Replaced = List<string>()
    member val Broadcasts = List<string>()
    member val Configuration = signInConfiguration with get, set

    /// Fides' exchange (EXCHANGE-PROTOCOL.md section 2), for any code.
    member this.Exchange (url: string) (body: string) =
        this.Posts.Add(url, body)
        let at (offset: TimeSpan) = clock.Value.UtcDateTime.Add(offset).ToString("yyyy-MM-ddTHH:mm:ssZ")

        match url with
        | "https://fides.test/v1/token" when body.Contains "\"code\":\"good-code\"" ->
            200,
            String.concat
                ""
                [ "{\"accessToken\":\""
                  accessToken
                  "\",\"accessTokenExpiresAt\":\""
                  at (TimeSpan.FromHours 8.0)
                  "\",\"refreshToken\":\""
                  refreshToken
                  "\",\"refreshTokenExpiresAt\":\""
                  at (TimeSpan.FromDays 180.0)
                  "\",\"identity\":{\"provider\":\"github\",\"subject\":\"583231\",\"login\":\"octocat\",\"name\":\"The Octocat\"}}" ]
        | "https://fides.test/v1/token" -> 400, """{"error":"code_rejected"}"""
        | "https://fides.test/v1/revoke" -> 204, ""
        | _ -> 404, """{"error":"not_found"}"""

let private collector () =
    let sink = Sinks.Collector()
    sink, { Boundary.configure [ sink.Sink() ] with Persistence = Blocking }

let private offer =
    let pack (o: AppProtocol.CapabilityOffer) = $"""{{"id":"{o.Id}","version":{o.Version},"fingerprint":"{o.Fingerprint}"}}"""
    $"""{{"protocol":{{"major":1,"minor":4}},"contract":{{"unit":"limen.core","version":1,"fingerprint":"{Limen.core.Fingerprint}"}},"capabilities":[{pack AppProtocol.environment},{pack AppProtocol.host}]}}"""

let private initialize (query: string) =
    $"""{{"kind":"Initialize","protocolVersion":1,"capabilities":["Http","Storage","Clipboard","Navigation"],"location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"{query}","hash":""}},"handshake":{offer}}}"""

let private json (value: string) = Text.Json.JsonSerializer.Serialize value

let private result (id: string) (kind: string) (outcome: string) =
    $"""{{"kind":"EffectResult","result":{{"kind":"{kind}","correlationId":"{id}","outcome":{outcome}}}}}"""

let private hostResult (id: string) (value: string option) =
    let result =
        match value with
        | Some v -> $"""{{"kind":"Value","value":{json v}}}"""
        | None -> """{"kind":"Done"}"""

    $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"chrona.host","version":1,"outcome":{{"kind":"Completed","result":{result}}}}}}}"""

/// Everything the kernel was asked and the page showed, across a pump.
[<NoComparison; NoEquality>]
type private Pumped =
    { State: App.State
      Replies: string list }

/// Sends a message, then answers every request the replies make, as the
/// browser and the exchange would, until nothing is left to answer.
let private pump aegis (env: App.Env) (browser: Browser) (state: App.State) (message: string) =
    let rec loop (state: App.State) (queue: string list) (replies: string list) =
        match queue with
        | [] -> { State = state; Replies = replies }
        | message :: rest ->
            let state, text = App.handle aegis env state message
            let answers =
                (JsonNode.Parse text).["effects"].AsArray()
                |> Seq.choose (fun effect ->
                    let id = effect.["correlationId"].GetValue<string>()
                    let field (name: string) = effect.["request"].[name].GetValue<string>()

                    match effect.["kind"].GetValue<string>() with
                    | "Http" when effect.["method"].GetValue<string>() = "GET" ->
                        Some(result id "HttpResult" $"""{{"kind":"Success","status":200,"body":{json browser.Configuration}}}""")
                    | "Http" ->
                        let status, body = browser.Exchange (effect.["url"].GetValue<string>()) (effect.["body"].GetValue<string>())
                        Some(result id "HttpResult" $"""{{"kind":"Success","status":{status},"body":{json body}}}""")
                    | "Storage" ->
                        let key = effect.["key"].GetValue<string>()

                        match effect.["operation"].GetValue<string>() with
                        | "get" ->
                            match browser.Device.TryGetValue key with
                            | true, v -> Some(result id "StorageResult" $"""{{"kind":"Success","value":{json v}}}""")
                            | _ -> Some(result id "StorageResult" """{"kind":"Success","value":null}""")
                        | "set" ->
                            browser.Device[key] <- effect.["value"].GetValue<string>()
                            Some(result id "StorageResult" """{"kind":"Success","value":null}""")
                        | _ ->
                            browser.Device.Remove key |> ignore
                            Some(result id "StorageResult" """{"kind":"Success","value":null}""")
                    | "Capability" when effect.["capability"].GetValue<string>() = "chrona.host" ->
                        match field "operation" with
                        | "tabGet" ->
                            match browser.Tab.TryGetValue(field "key") with
                            | true, v -> Some(hostResult id (Some v))
                            | _ -> Some(hostResult id None)
                        | "tabSet" ->
                            browser.Tab[field "key"] <- field "value"
                            Some(hostResult id None)
                        | "tabRemove" ->
                            browser.Tab.Remove(field "key") |> ignore
                            Some(hostResult id None)
                        | "leave" ->
                            browser.Left.Add(field "url")
                            Some(hostResult id None)
                        | "replaceAddress" ->
                            browser.Replaced.Add(field "url")
                            Some(hostResult id None)
                        | _ ->
                            browser.Broadcasts.Add(field "message")
                            Some(hostResult id None)
                    | "Capability" ->
                        Some(
                            $"""{{"kind":"EffectResult","result":{{"kind":"CapabilityResult","correlationId":"{id}","capability":"limen.environment","version":1,"outcome":{{"kind":"Completed","result":{{"kind":"Described","environment":{{"locale":"en-US","languages":["en-US"],"timeZone":"America/New_York","direction":"ltr","preferences":[]}}}}}}}}}}"""
                        )
                    | _ -> None)
                |> List.ofSeq

            loop state (rest @ answers) (replies @ [ text ])

    loop state [ message ] []

let private view (pumped: Pumped) = (JsonNode.Parse(List.last pumped.Replies)).["view"]
let private viewFlag (key: string) (pumped: Pumped) = (view pumped).[key].GetValue<bool>()
let private viewText (key: string) (pumped: Pumped) = (view pumped).[key].GetValue<string>()

let private event (name: string) (value: string) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","value":"{value}"}}}}"""

let private envFor (clock: DateTimeOffset ref) : App.Env =
    let counter = ref 0uy
    let bridge = Bridge.Bridge()

    { Now = fun () -> clock.Value
      NewId = fun prefix -> $"{prefix}-{Guid.NewGuid():N}"
      Session = App.localSession
      Bridge = bridge
      // Deterministic, distinct bytes: the test's stand-in for crypto.getRandomValues.
      Identity =
        Identity.create
            bridge
            (fun () -> clock.Value)
            (fun count ->
                Array.init count (fun _ ->
                    counter.Value <- counter.Value + 1uy
                    counter.Value))
      Store = Store.inMemory bridge }

/// Signs in end to end: the page leaves for GitHub, GitHub sends it back with
/// a code, a new page load completes the callback. Returns the signed-in page.
let private signIn aegis (clock: DateTimeOffset ref) (browser: Browser) (retention: string) =
    let first = envFor clock
    let opened = pump aegis first browser App.initial (initialize "")
    Assert.True(viewFlag "screenSignIn" opened)
    let chosen = pump aegis first browser opened.State (event "signInRetention" retention)
    let leaving = pump aegis first browser chosen.State (event "signIn" "")

    let authorize = Uri(Seq.exactlyOne browser.Left)
    Assert.Equal("github.com", authorize.Host)
    let query = AppProtocol.queryPairs authorize.Query |> Map.ofList
    Assert.Equal("Iv23liTEST", query["client_id"])
    Assert.Equal("S256", query["code_challenge_method"])
    Assert.False(viewFlag "shellVisible" leaving)

    // GitHub redirects back; this is a new page load in the same tab.
    let state = Uri.EscapeDataString query["state"]
    let back = $"?code=good-code&state={state}"
    let second = envFor clock
    pump aegis second browser App.initial (initialize back), second

[<Fact>]
let ``a person signs in with GitHub, and Chrona works as the identity GitHub resolved`` () =
    let _, aegis = collector ()
    let clock = ref start
    let browser = Browser(clock)
    let page, env = signIn aegis clock browser "page"

    Assert.True(viewFlag "shellVisible" page)
    Assert.Equal("octocat", viewText "accountLogin" page)
    Assert.Equal("octocat", viewText "sessionName" page)
    Assert.Equal("github:583231", page.State.Model.Value.Session.ActorId)
    // The code was exchanged once, through the configured exchange only.
    Assert.Equal<string list>([ "https://fides.test/v1/token" ], browser.Posts |> Seq.map fst |> List.ofSeq)
    // The callback's code and state were removed from the address bar.
    Assert.Equal<string list>([ "http://127.0.0.1:4321/web/index.html" ], List.ofSeq browser.Replaced)

    // Arca gets a token provider, not a token.
    match env.Identity.TokenProvider() with
    | Some provider ->
        match provider () |> Async.RunSynchronously with
        | Ok token -> Assert.Equal(("Authorization", $"Bearer {accessToken}"), Arca.AccessToken.authorization token)
        | Error reason -> failwith $"{reason}"
    | None -> failwith "no token provider"

[<Fact>]
let ``the token is never shown, and with session-only retention it is never stored`` () =
    let _, aegis = collector ()
    let clock = ref start
    let browser = Browser(clock)
    let page, _ = signIn aegis clock browser "page"

    for reply in page.Replies do
        let view = (JsonNode.Parse reply).["view"].ToJsonString()
        Assert.DoesNotContain(accessToken, view)
        Assert.DoesNotContain(refreshToken, view)

    let stored = Seq.append browser.Tab.Values browser.Device.Values |> String.concat "\n"
    Assert.DoesNotContain(accessToken, stored)
    Assert.DoesNotContain(refreshToken, stored)
    Assert.All(browser.Broadcasts, fun message -> Assert.DoesNotContain("gho_", message))

    // The person works: nothing they record carries the token either.
    let worked = pump aegis (envFor clock) browser page.State (event "newProjectName" "HelixNote")
    Assert.DoesNotContain(accessToken, List.last worked.Replies)

[<Fact>]
let ``signing out clears the token from the tab and revokes it at GitHub`` () =
    let _, aegis = collector ()
    let clock = ref start
    let browser = Browser(clock)
    let page, env = signIn aegis clock browser "tab"

    // Kept for the tab, as chosen.
    Assert.Contains(browser.Tab.Values, fun value -> value.Contains accessToken)

    let out = pump aegis env browser page.State (event "signOut" "")
    Assert.True(viewFlag "screenSignIn" out)
    Assert.Equal("signed_out", viewText "signInNoticeCode" out)
    Assert.Equal("https://fides.test/v1/revoke", fst (Seq.last browser.Posts))
    Assert.DoesNotContain(browser.Tab.Values, fun value -> value.Contains accessToken)

    match env.Identity.TokenProvider() with
    | Some provider -> Assert.True(provider () |> Async.RunSynchronously |> Result.isError)
    | None -> failwith "no token provider"

[<Fact>]
let ``a session kept for the tab is restored on reload; a refused code is a notice`` () =
    let _, aegis = collector ()
    let clock = ref start
    let browser = Browser(clock)
    signIn aegis clock browser "tab" |> ignore

    let reloaded = pump aegis (envFor clock) browser App.initial (initialize "")
    Assert.True(viewFlag "shellVisible" reloaded)
    Assert.Equal("octocat", viewText "accountLogin" reloaded)

    // A fresh tab, and a callback whose code the provider refuses.
    let other = Browser(clock)
    let otherEnv = envFor clock
    let opened = pump aegis otherEnv other App.initial (initialize "")
    pump aegis otherEnv other opened.State (event "signIn" "") |> ignore
    let state = (AppProtocol.queryPairs (Uri(Seq.exactlyOne other.Left)).Query |> Map.ofList)["state"]
    let refused = pump aegis (envFor clock) other App.initial (initialize $"?code=bad-code&state={state}")
    Assert.True(viewFlag "screenSignIn" refused)
    Assert.Equal("code_rejected", viewText "signInNoticeCode" refused)

    // A callback that did not start in this tab is refused without calling the exchange.
    let posts = other.Posts.Count
    let forged = pump aegis (envFor clock) other App.initial (initialize "?code=good-code&state=forged")
    Assert.Equal("state_invalid", viewText "signInNoticeCode" forged)
    Assert.Equal(posts, other.Posts.Count)

[<Fact>]
let ``a local deployment needs no sign-in`` () =
    let _, aegis = collector ()
    let clock = ref start
    let browser = Browser(clock)
    browser.Configuration <- """{"environment":"local","environmentName":"local"}"""
    let page = pump aegis (envFor clock) browser App.initial (initialize "")
    Assert.True(viewFlag "shellVisible" page)
    Assert.True(viewFlag "accountLocal" page)
    Assert.Empty(browser.Posts)
