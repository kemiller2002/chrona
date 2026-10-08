/// Chrona's places and their addresses (CHX-460, WI-0071): the route table,
/// the typed codec's round trip and canonical form, legacy addresses, return
/// targets, share links, the route inventory, and the interim copy of
/// Limen.Routing staying Limen's own.
module Chrona.Tests.PlacesTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open Limen.Routing
open Chrona.Engine.App.Places
open Chrona.Tests.Support

let private parseAll = parse Router.allowAll

let private ok (result: Result<'a, 'e>) =
    match result with
    | Ok value -> value
    | Error error -> failwith $"expected Ok, got %A{error}"

let private on = DateOnly(2026, 10, 8)

// ---- the round trip ----------------------------------------------------------------

/// Text a person might type into a filter: spaces, reserved characters,
/// non-ASCII and characters outside the basic plane.
let private awkward =
    [ "helix"; "Helix Note"; "a/b?c=d&e#f"; "100% sure"; "naïve café"; "日本語"; "emoji 🙂"; "plus+sign"; "comma,separated"; "~-._" ]

let private generated (seed: int) =
    let random = Random seed
    let pick (items: 'a list) = items[random.Next items.Length]
    let maybe (value: unit -> 'a) = if random.Next 2 = 0 then None else Some(value ())
    let date () = DateOnly(random.Next(1, 10000), random.Next(1, 13), 1).AddDays(random.Next 28)

    match random.Next 12 with
    | 0 -> Today
    | 1 -> Day(date (), maybe (fun () -> pick awkward))
    | 2 -> ThisMonth
    | 3 -> let d = date () in Month(d.Year, d.Month)
    | 4 -> Track
    | 5 -> Entry(pick [ "ACT-1"; "ACT-20261008-0001"; "id with space"; "ünïcode" ], maybe date)
    | 6 -> ReviewToday
    | 7 -> Review(date ())
    | 8 ->
        Reports
            { From = maybe date
              To = maybe date
              ProjectId = maybe (fun () -> pick awkward)
              ActivityTypeId = maybe (fun () -> pick awkward)
              Tag = maybe (fun () -> pick awkward)
              Method = maybe (fun () -> pick [ "manual"; "timer" ])
              Billability = maybe (fun () -> pick [ "billable"; "non-billable"; "pending" ])
              Text = maybe (fun () -> pick awkward)
              IncludeRemoved = random.Next 2 = 0
              Grouping = pick [ "project"; "activityType"; "tag"; "day" ]
              Format = pick [ "csv"; "json" ] }
    | 9 -> Settings None
    | 10 -> Settings(Some(pick [ References; PeriodSettings; Changes; Account; People; ActivityIndex ]))
    | _ -> SignIn(maybe (fun () -> pick [ "/"; "/day/2026-10-08?project=helix"; "/reports?q=a%20b" ]))

[<Fact>]
let ``every place has one canonical address, and that address opens the same place`` () =
    for seed in 1..2000 do
        let place = generated seed
        let address = ok (format place)
        Assert.Equal(place, ok (parseAll address))
        // Formatting what was parsed gives the same address: one canonical form.
        Assert.Equal(address, ok (format (ok (parseAll address))))

[<Fact>]
let ``addresses are relative fragment links in Limen's canonical form`` () =
    Assert.Equal("/", ok (format Today))
    Assert.Equal("#/day/2026-10-08", ok (href (Day(on, None))))
    // %20 for a space, uppercase hex, everything but A-Z a-z 0-9 - . _ ~ encoded.
    Assert.Equal("/day/2026-10-08?project=Helix%20Note", ok (format (Day(on, Some "Helix Note"))))
    Assert.Equal("/day/2026-10-08?project=a%2Fb%3Fc%3Dd%26e%23f", ok (format (Day(on, Some "a/b?c=d&e#f"))))
    Assert.Equal("/month/2026-10", ok (format (Month(2026, 10))))
    Assert.Equal("/entries/ACT-1?on=2026-10-08", ok (format (Entry("ACT-1", Some on))))
    Assert.Equal("/review/2026-10-08", ok (format (Review on)))
    Assert.Equal("/more/people", ok (format (Settings(Some People))))
    // Defaults are omitted; declared parameters appear in declaration order.
    Assert.Equal("/reports", ok (format (Reports allReports)))

    Assert.Equal(
        "/reports?from=2026-10-01&to=2026-10-31&method=timer&q=pairing%20review&removed=true&group=type&format=json",
        ok (
            format (
                Reports
                    { allReports with
                        Format = "json"
                        Grouping = "activityType"
                        IncludeRemoved = true
                        Text = Some "pairing review"
                        Method = Some "timer"
                        To = Some(DateOnly(2026, 10, 31))
                        From = Some(DateOnly(2026, 10, 1)) }
            )
        )
    )

[<Fact>]
let ``a non-canonical address opens its place and is told its canonical form`` () =
    // Out of order, a default spelled out, an undeclared parameter and lowercase hex.
    let given = "/reports?format=csv&q=a%2fb&utm_source=mail&from=2026-10-01"
    let place = ok (parseAll given)
    Assert.Equal(Reports { allReports with From = Some(DateOnly(2026, 10, 1)); Text = Some "a/b" }, place)
    Assert.Equal("/reports?from=2026-10-01&q=a%2Fb", ok (format place))

    let routes = RouteTable.routes table
    let _, _, effect = Navigation.adopt routes Router.allowAll Navigation.initial given
    Assert.Equal(Some(NavigationEffect.Replace "/reports?from=2026-10-01&q=a%2Fb"), effect)

[<Fact>]
let ``addresses Chrona used before still open the same place`` () =
    Assert.Equal(Today, ok (parseAll "/today"))
    Assert.Equal(Day(on, None), ok (parseAll "/today/2026-10-08"))
    Assert.Equal(Entry("ACT-1", None), ok (parseAll "/activity/ACT-1"))
    Assert.Equal(Track, ok (parseAll "/track"))
    Assert.Equal(Settings None, ok (parseAll "/more"))
    Assert.Equal(ThisMonth, ok (parseAll "/month"))
    Assert.Equal(ReviewToday, ok (parseAll "/review"))
    Assert.Equal(Review on, ok (parseAll "/review/2026-10-08"))
    // Opening one is answered with a replace to its current address, never a push.
    let _, _, effect = Navigation.adopt (RouteTable.routes table) Router.allowAll Navigation.initial "/today/2026-10-08"
    Assert.Equal(Some(NavigationEffect.Replace "/day/2026-10-08"), effect)

[<Fact>]
let ``an address that names nothing, or names it wrongly, is a route error and never another place`` () =
    let error address =
        match parseAll address with
        | Error error -> error
        | Ok place -> failwith $"{address} opened {place}"

    Assert.Equal(RouteError.NotFound, error "/nowhere")
    Assert.Equal(RouteError.NotFound, error "/day")
    // The first structural match decides: a wrong part of More is invalid, not another page.
    Assert.Equal(RouteError.Invalid(Names.Section, "section", "billing", "one of references|periods|changes|account"), error "/more/billing")
    Assert.Equal(RouteError.Invalid(Names.Day, "on", "2026-02-30", "date"), error "/day/2026-02-30")
    Assert.Equal(RouteError.Invalid(Names.Month, "period", "2026-13", "month"), error "/month/2026-13")
    Assert.Equal(RouteError.Invalid(Names.Reports, "group", "colour", "one of project|type|tag|day"), error "/reports?group=colour")
    Assert.Equal(RouteError.Invalid(Names.Reports, "removed", "yes", "bool"), error "/reports?removed=yes")
    Assert.Equal(RouteError.Invalid(Names.Reports, "q", "a,b", "a single value"), error "/reports?q=a&q=b")
    Assert.Equal(RouteError.Malformed "path", error "/day/%ZZ")
    Assert.Equal(RouteError.Malformed "length", error ("/reports?q=" + String('a', 9000)))

[<Fact>]
let ``guards decide what the interface shows: a refusal is not-permitted, a redirect is followed`` () =
    let deny name (_: Match) = if name = Guards.Administrator then GuardDecision.Deny else GuardDecision.Allow
    // The More screen's people and activity index are an administrator's; its other parts are anyone's.
    Assert.Equal(Error(RouteError.NotPermitted Names.People), parse deny "/more/people")
    Assert.Equal(Error(RouteError.NotPermitted Names.ActivityIndex), parse deny "/more/index")
    Assert.Equal(Ok(Settings(Some References)), parse deny "/more/references")
    Assert.Equal(Ok(Settings None), parse deny "/more")
    // Every place but sign-in is behind the signed-in guard, which may send to sign-in.
    let toSignIn name (candidate: Match) =
        if name = Guards.SignedIn then
            let target = Router.canonical (RouteTable.routes table) candidate |> Result.toOption |> Option.bind captureReturn
            GuardDecision.Redirect(Names.SignIn, Map.empty, target |> Option.map (fun t -> Map [ "returnTo", Value.Text t ]) |> Option.defaultValue Map.empty)
        else
            GuardDecision.Allow

    Assert.Equal(Ok(SignIn(Some "/day/2026-10-08?project=helix")), parse toSignIn "/day/2026-10-08?project=helix")
    Assert.Equal(Ok(SignIn None), parse toSignIn "/sign-in")

[<Fact>]
let ``a sign-in returns only to one of Chrona's own places, in its canonical form`` () =
    Assert.Equal(Some "/day/2026-10-08?project=helix", captureReturn "/day/2026-10-08?project=helix")
    Assert.Equal(Some "/day/2026-10-08", captureReturn "/today/2026-10-08")
    Assert.Equal(Some "/reports?q=a%2Fb", captureReturn "/reports?q=a%2fb&format=csv")
    // Never the sign-in or not-found page, another site, or something that is not an address.
    Assert.Equal(None, captureReturn "/sign-in?returnTo=%2Ftrack")
    Assert.Equal(None, captureReturn "/nowhere")
    Assert.Equal(None, captureReturn "//evil.example/day/2026-10-08")
    Assert.Equal(None, captureReturn "https://evil.example/")
    Assert.Equal(None, captureReturn "/day/2026-10-08\\..")
    Assert.Equal(None, captureReturn "day/2026-10-08")
    // Resuming re-checks the target; anything unusable is home.
    Assert.Equal("/day/2026-10-08", resume Router.allowAll (Some "/day/2026-10-08"))
    Assert.Equal("/", resume Router.allowAll None)
    Assert.Equal("/", resume Router.allowAll (Some "//evil.example/"))
    Assert.Equal("/", resume (fun _ _ -> GuardDecision.Deny) (Some "/more/people"))
    Assert.Equal("/sign-in?returnTo=%2Fday%2F2026-10-08", ok (format (SignIn(Some "/day/2026-10-08"))))

[<Fact>]
let ``a shared link opens the same view another day and never carries the page's query`` () =
    Assert.Equal(
        "https://chrona.echelonfoundry.com/web/index.html#/day/2026-10-08?project=helix",
        share "https://chrona.echelonfoundry.com" "/web/index.html" "/day/2026-10-08?project=helix"
    )
    // Today's ledger, this month and today's review are made explicit for a link.
    Assert.Equal(Day(on, None), explicit on Today)
    Assert.Equal(Month(2026, 10), explicit on ThisMonth)
    Assert.Equal(Review on, explicit on ReviewToday)
    Assert.Equal(Reports { allReports with From = Some(DateOnly(2026, 10, 1)); To = Some(DateOnly(2026, 10, 31)) }, explicit on (Reports allReports))
    Assert.Equal(Track, explicit on Track)
    Assert.Equal("/", ofFragment "")
    Assert.Equal("/day/2026-10-08", ofFragment "#/day/2026-10-08")

[<Fact>]
let ``no address can carry a token or other credential`` () =
    let names = [ "token"; "access_token"; "secret"; "client-secret"; "key"; "apiKey"; "session"; "auth"; "Authorization"; "code"; "password"; "credentials" ]

    for name in names do
        let route = { Route.create "x" "x" with Query = [ QueryParam.optional name ParamType.String ] }

        match RouteTable.define [ route ] [] { Home = "x"; SignIn = None; NotFound = None } with
        | Error errors -> Assert.Contains(DefinitionError.ReservedName("x", name), errors)
        | Ok _ -> failwith $"a parameter named {name} was accepted"

    // Chrona's own table declares none (it defines at all).
    let declared =
        RouteTable.destinations table
        |> List.collect (fun (_, route) -> route.Query |> List.map _.Name)

    Assert.DoesNotContain(declared, RouteTable.isReserved)

// ---- the route inventory --------------------------------------------------------------

[<Fact>]
let ``the route inventory is the table's own, byte for byte`` () =
    let rendered = inventory ()
    let path = repoFile ".echelon/routes.json"

    // CHRONA_WRITE_ROUTES=1 rewrites it after a change to the table.
    if Environment.GetEnvironmentVariable "CHRONA_WRITE_ROUTES" = "1" then
        File.WriteAllText(path, rendered)

    Assert.Equal(rendered, File.ReadAllText path)

    use document = JsonDocument.Parse rendered
    let root = document.RootElement
    Assert.Equal("echelon.routes/v1", root.GetProperty("schema").GetString())
    Assert.Equal("hash", root.GetProperty("mode").GetString())
    Assert.Equal(Names.Today, root.GetProperty("home").GetString())
    Assert.Equal(Names.SignIn, root.GetProperty("signIn").GetString())
    Assert.Equal(Names.NotFound, root.GetProperty("notFound").GetString())
    Assert.Equal(3, root.GetProperty("legacy").GetArrayLength())
    Assert.EndsWith("}\n", rendered)

// ---- the interim copy of Limen.Routing ----------------------------------------------------

[<Fact>]
let ``the interim Limen.Routing is Limen's own files, unchanged`` () =
    use lock = JsonDocument.Parse(readRepoFile "vendor/limen-routing/limen-routing.lock")
    let files = lock.RootElement.GetProperty("files").EnumerateObject() |> Seq.toList
    Assert.NotEmpty files

    for file in files do
        let bytes = File.ReadAllBytes(repoFile ("vendor/limen-routing/" + file.Name))
        let digest = "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
        Assert.Equal(file.Value.GetString(), digest)
