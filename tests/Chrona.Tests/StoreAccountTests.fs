/// Whose unsent changes these are (Arca 0.4.0, WI-0073): every change names
/// its account, and one that names no one waits for the person.
[<Xunit.Collection("Store")>]
module Chrona.Tests.StoreAccountTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture
open Chrona.Tests.StoreDeviceFixture
open Chrona.Tests.StoreFixtures

// ---- Whose unsent changes (Arca 0.4.0, WI-0073) ---------------------------------------

/// The text of the queue this browser keeps in IndexedDB.
let private queueText (browser: Browser) =
    match queueRecord browser with
    | Some { Queue = Some text } -> text
    | other -> failwith $"%A{other}"

/// The queue as an earlier Chrona kept it, before entries named their
/// account: in localStorage, without `accountId`.
let private beforeAccounts (text: string) =
    let rec strip (node: Text.Json.Nodes.JsonNode) =
        match node with
        | :? Text.Json.Nodes.JsonObject as fields ->
            fields.Remove "accountId" |> ignore
            for field in List.ofSeq fields do
                if not (isNull field.Value) then strip field.Value
        | :? Text.Json.Nodes.JsonArray as items ->
            for item in items do
                if not (isNull item) then strip item
        | _ -> ()

    let root = Text.Json.Nodes.JsonNode.Parse text
    strip root
    root.ToJsonString()

let private accounts (browser: Browser) =
    queued browser |> List.map _.Operation.AccountId

[<Fact>]
let ``every change queued now names its account by a stable id, never a display name`` () =
    let browser = Browser()
    let _, device = unsentOn browser (configuration "production")
    Assert.Equal<string option list>([ Some "actor:github:583231" ], accounts browser)
    Assert.Contains("\"accountId\":\"actor:github:583231\"", queueText browser)

[<Fact>]
let ``a change from before Arca 0.4.0 is attributed to the actor it records, so another account on the device never sends or discards it`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    record owner "08:00" "08:30" "Setup"
    let browser = Browser()
    let queuing = Device(github, RepositoryVisibility.Private, "production", browser)
    queuing.Open()
    queuing.Offline <- true
    record queuing "09:00" "10:00" "Pairing"
    let earlierText = beforeAccounts (queueText browser)
    Assert.DoesNotContain("accountId", earlierText)
    let commits = github.State.History.Length

    // A device whose earlier Chrona left octocat's change, naming no account.
    let shared = Browser()
    shared.Storage[queueKey] <- earlierText

    // Hubot signs in there: octocat's change is octocat's (the actor it
    // records), so it is neither sent with hubot's credential nor counted
    // as hubot's, and hubot discarding their own changes leaves it.
    let other = Device(github, RepositoryVisibility.Private, "production", hubot, config, shared)
    other.Open()
    Assert.Contains("Another account", other.Model.Store.Sync.Note.Value)
    Assert.Equal(0, other.Model.Store.Sync.Earlier)
    Assert.Equal(commits, github.State.History.Length)
    other.Ui("signOut", "")
    Assert.True(signedOut other)
    Assert.Equal(1, (queued shared).Length)

    // Octocat signs in on that device: their change is sent, once.
    other.Close()
    let back = Device(github, RepositoryVisibility.Private, "production", shared)
    back.Open()
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued shared)
    Assert.Contains("Pairing", descriptions back)

/// A queue from an earlier version whose entry records no usable actor.
let private unattributed () =
    let github = InMemoryStore()
    let browser = Browser()
    let device = Device(github, RepositoryVisibility.Private, "production", browser)
    device.Open()
    record device "08:00" "08:30" "Setup"
    device.Offline <- true
    record device "09:00" "10:00" "Pairing"
    let text = (beforeAccounts (queueText browser)).Replace("\"actorId\":\"github:583231\"", "\"actorId\":\"?\"")
    Assert.Contains("\"actorId\":\"?\"", text)
    let shared = Browser()
    shared.Storage[queueKey] <- text
    github, shared

[<Fact>]
let ``a change from an earlier version that names no one waits for the person, who may keep it`` () =
    let github, shared = unattributed ()
    let commits = github.State.History.Length
    let device = Device(github, RepositoryVisibility.Private, "production", shared)
    device.Open()

    Assert.Equal(1, device.Model.Store.Sync.Earlier)
    Assert.True(viewFlag "hasEarlierUnsent" device.Model)
    Assert.Equal(commits, github.State.History.Length)

    // Signing out never discards it on a guess.
    device.Ui("keepEarlier", "")
    Assert.False(viewFlag "hasEarlierUnsent" device.Model)
    device.Ui("signOut", "")
    device.Close()
    Assert.Equal(1, (queued shared).Length)
    Assert.Equal(commits, github.State.History.Length)

[<Fact>]
let ``a change from an earlier version that names no one is sent as the person's only when they say it is theirs`` () =
    let github, shared = unattributed ()
    let commits = github.State.History.Length
    let device = Device(github, RepositoryVisibility.Private, "production", shared)
    device.Open()
    device.Ui("sendEarlier", "")
    Assert.Equal(0, device.Model.Store.Sync.Earlier)
    Assert.Equal(commits + 1, github.State.History.Length)
    Assert.Empty(queued shared)

[<Fact>]
let ``a change from an earlier version that names no one is discarded only after a confirmation`` () =
    let github, shared = unattributed ()
    let commits = github.State.History.Length
    let device = Device(github, RepositoryVisibility.Private, "production", shared)
    device.Open()
    device.Ui("discardEarlier", "")
    Assert.True(viewFlag "earlierConfirming" device.Model)
    Assert.Equal(1, (queued shared).Length)
    device.Ui("discardEarlierConfirmed", "")
    Assert.Equal(0, device.Model.Store.Sync.Earlier)
    Assert.Empty(queued shared)
    Assert.Equal(commits, github.State.History.Length)
    // The queue is this account's again: what it records now is sent.
    record device "11:00" "11:30" "After"
    Assert.Equal(commits + 1, github.State.History.Length)

[<Fact>]
let ``a queued change decided again keeps its account id (Arca 0.4.1's revise)`` () =
    let browser = Browser()
    let github, device = unsentOn browser (configuration "production")

    // Back online, something commits inside Chrona's folder first, so the
    // change is decided again and revised; GitHub is then out of reach, so
    // the revised change stays queued.
    device.Offline <- false
    device.BeatenInside 1
    device.UnreachableAfter 2
    device.Return()
    let kept = queued browser
    Assert.Equal(1, kept.Length)
    Assert.True(kept.Head.Operation.ExpectedNamespaceToken.IsSome)
    Assert.Equal<string option list>([ Some "actor:github:583231" ], accounts browser)

[<Fact>]
let ``a change revised under Arca 0.4.0, which lost its account id, is attributed to the actor it records and is its person's to discard`` () =
    let github = InMemoryStore()
    let browser = Browser()
    let queuing = Device(github, RepositoryVisibility.Private, "production", browser)
    queuing.Open()
    record queuing "08:00" "08:30" "Setup"
    queuing.Offline <- true
    record queuing "09:00" "10:00" "Pairing"
    // As Arca 0.4.0's revise left it: pending, with no account id.
    let revised = beforeAccounts (queueText browser)
    Assert.DoesNotContain("accountId", revised)
    let commits = github.State.History.Length

    let shared = Browser()
    shared.Storage[queueKey] <- revised
    let device = Device(github, RepositoryVisibility.Private, "production", shared)
    // The records open, and sending it finds GitHub out of reach: it stays.
    device.UnreachableAfter 1
    device.Open()
    Assert.Equal(0, device.Model.Store.Sync.Earlier)
    Assert.Equal<string option list>([ Some "actor:github:583231" ], accounts shared)

    // Its person discards it at sign-out, after the confirmation.
    device.Ui("signOut", "")
    device.Ui("signOutDiscard", "")
    device.Ui("signOutDiscardConfirmed", "")
    Assert.True(signedOut device)
    Assert.Empty(queued shared)
    Assert.Equal(commits, github.State.History.Length)
