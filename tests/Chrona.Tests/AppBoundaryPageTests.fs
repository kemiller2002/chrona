/// The page and the engine agree: the page binds only what the engine
/// projects, every conditional part is one element, and the clipboard and
/// print effects.
[<Xunit.Collection("AppBoundary")>]
module Chrona.Tests.AppBoundaryPageTests

open System
open System.Text.RegularExpressions
open System.Text.Json.Nodes
open Xunit
open Aegis
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Application
open Chrona.Tests.Support
open Chrona.Tests.AppBoundaryFixture

// ---- the page and the engine agree --------------------------------------------------

let private attributeValues (attribute: string) (html: string) =
    Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

let private boundKeys (html: string) =
    let bindings =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    [ "data-text"; "data-if"; "data-each"; "data-key" ]
    |> List.map (fun attribute -> attributeValues attribute html)
    |> Set.unionMany
    |> Set.union bindings

let private locationChanged (hash: string) =
    $"""{{"kind":"LocationChanged","location":{{"origin":"http://127.0.0.1:4321","path":"/web/index.html","query":"","hash":"{hash}"}}}}"""

let private toggled (name: string) (key: string) (on: bool) =
    $"""{{"kind":"Event","event":{{"kind":"Event","name":"{name}","key":"{key}","value":"","checked":{(if on then "true" else "false")}}}}}"""

/// Views of every screen in states where every list the page repeats is
/// non-empty somewhere: records, a merge selection, reference data, problems
/// on every form, a held timer, an attested and then changed day, an
/// activity with evidence and history, and a month.
let private richViews () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let started = handle aegis env AppState.initial initialize
    let send (state: AppState.State) message = fst (handle aegis env state message)
    let view (state: AppState.State) = Chrona.Engine.App.Project.project state.Model.Value
    let state = send (fst started) (describedAs "America/New_York" started)

    let state =
        [ event "newProjectName" None "HelixNote"
          event "addProject" None ""
          event "newActivityTypeName" None "Research"
          event "addActivityType" None ""
          event "newTagName" None "Backend"
          event "addTag" None ""
          event "addTag" None "" ]
        |> List.fold send state

    let model = state.Model.Value
    let project = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.Project model.References).Head.Id
    let activityType = (Chrona.Domain.Reference.selectable Chrona.Domain.Reference.ActivityType model.References).Head.Id

    let manual (start: string) (finish: string) (description: string) =
        [ event "manualActivityType" None activityType
          event "manualProject" None project
          event "manualStartDate" None "2026-10-08"
          event "manualStartTime" None start
          event "manualEndTime" None finish
          event "manualDescription" None description
          event "manualPurpose" None "Delivery"
          event "saveManual" None "" ]

    let state = manual "09:00" "10:00" "First" @ manual "10:00" "10:30" "Second" @ manual "11:00" "11:30" "Third" |> List.fold send state
    let idOf (description: string) (state: AppState.State) =
        state.Model.Value.Ledger.Activities |> Map.toList |> List.map snd |> List.find (fun a -> a.Classification.Description = description) |> _.ActivityId

    let first, third = idOf "First" state, idOf "Third" state

    // Problems on every Track and More form, a merge refused as not
    // contiguous, an attested day, and a timer held for completion.
    let state =
        [ event "saveManual" None ""
          event "pauseTimer" None ""
          toggled "mergeSelect" first true
          toggled "mergeSelect" third true
          event "saveMerge" None ""
          locationChanged "#/review/2026-10-08"
          event "attestDay" None ""
          event "attestStatement" None "Complete and accurate."
          event "attestDay" None ""
          event "attestDay" None ""
          event "timerActivityType" None activityType
          event "timerProject" None project
          event "startTimer" None "" ]
        |> List.fold send state

    clock.Value <- now.AddMinutes 30.0
    let state = [ event "stopTimer" None ""; event "saveCompletion" None ""; event "pauseTimer" None "" ] |> List.fold send state

    // An activity with evidence, history and a refusal on each of its forms;
    // the correction also makes the attested day stale.
    let state =
        [ locationChanged $"#/activity/{first}"
          event "evidenceLabel" None "Commit"
          event "evidenceUrl" None "https://example.test/commit/1"
          event "attachEvidence" None ""
          event "evidenceLabel" None "Bad"
          event "evidenceUrl" None "not a link"
          event "attachEvidence" None ""
          event "amendDescription" None "First, corrected"
          event "saveAmend" None ""
          event "amendPurpose" None ""
          event "saveAmend" None ""
          event "restoreActivity" None ""
          event "splitFirst" None "10"
          event "splitSecond" None "10"
          event "saveSplit" None "" ]
        |> List.fold send state

    let activity = view state
    let review = view (send state (locationChanged "#/review/2026-10-08"))
    let month = view (send state (locationChanged "#/month/2026-10"))
    let today = view (send state (locationChanged "#/today/2026-10-08"))

    // A project's page, with the entries recorded on it (CHX-460).
    let project =
        let projectId = state.Model.Value.Ledger.Activities[first].Classification.ProjectId
        view (send state (locationChanged $"#/projects/{projectId}"))

    // A member who may keep their own time but not change settings or
    // export: the refusals are shown where the commands were made.
    let restricted =
        let model = state.Model.Value
        let roster = model.Roster
        let actor = model.Session.ActorId

        let limited =
            { roster with
                Members =
                    roster.Members.Add(actor, { roster.Members[actor] with Capabilities = set [ Chrona.Domain.Access.ViewOwnTime ] }) }

        let limitedState = { state with Model = Some { model with Roster = limited } }
        [ event "periodCadence" None "monthly"; event "copyExport" None "" ] |> List.fold send limitedState

    // Records read from storage that need attention.
    // Records read from storage that need attention: one edited outside
    // Chrona, held for review, and a change not stored because the records
    // moved, each with a refusal shown where it was resolved.
    let troubled =
        let model = state.Model.Value
        let held = model.Ledger.Activities[first]
        let request = { emptyRequest "COMMIT-refused" with Activities = [ held ] }

        { state with
            Model =
                Some
                    { model with
                        Problems =
                            model.Problems
                                .Add(ConflictForm, [ Chrona.Domain.Diagnostics.UnauthorizedCapability "AmendOwnTime" ])
                                .Add(OutsideEditForm, [ Chrona.Domain.Diagnostics.ExternalStateClaim(held.ActivityId, "Approved") ])
                                .Add(IndexForm, [ Chrona.Domain.Diagnostics.UnauthorizedCapability "ManageOrganizationSettings" ])
                        Store =
                            { model.Store with
                                Integrity = [ Chrona.Domain.Diagnostics.ExternalEdit "records/chrona.activity/a/2026/10/A-1.json" ]
                                Held = [ held ]
                                History =
                                    [ { ActorId = model.Session.ActorId
                                        Year = 2026
                                        Month = 9
                                        Activities = 3
                                        Minutes = 180
                                        ApprovedMinutes = 60
                                        PublishedMinutes = 0 } ]
                                Index = "Kept with every change: 3 records in 1 months of time."
                                Conflicts =
                                    [ { Id = "COMMIT-refused"
                                        Request = request
                                        Divergences = [ Chrona.Domain.Reconcile.ActivityChanged(held, Some held) ] } ] } } }

    // Signed in to a deployment of two organizations, as their administrator,
    // with a refused attempt to add a member.
    let administering =
        let model = state.Model.Value

        let config =
            """{"environment":"test","environmentName":"test","location":{"owner":"acme","repository":"chrona-data","branch":"main","basePath":""},"identity":{"exchange":"https://fides.test","application":"chrona-test","provider":"github","clientId":"Iv23li","redirectUri":"https://chrona.test/"},"organizations":[{"id":"org_a","displayName":"A","slug":"a","timeZone":"UTC"},{"id":"org_b","displayName":"B","slug":"b","timeZone":"UTC"}]}"""
            |> Chrona.Domain.Deployment.parse
            |> function
                | Ok config -> config
                | Error error -> failwith $"{error}"

        let signedIn =
            { model with
                Deployment = Some config
                Identity = { model.Identity with Mode = SignedInMode } }

        send { state with Model = Some signedIn } (event "admitMember" None "")

    // The period page of someone who approves, with another person's
    // submission waiting and a refusal shown where it was made (WI-0036).
    let reviewing =
        let model = state.Model.Value
        let theirs = { model.Ledger.Activities[first] with ActorId = "github:42"; ActivityId = "ACT-theirs" }
        let config = { model.PeriodConfig with ApprovalRequired = true }
        let period = Chrona.Domain.Periods.containing config theirs.Occurrence.LocalDate

        let submission: Chrona.Domain.PeriodReview.PeriodReview =
            { Kind = Chrona.Domain.PeriodReview.Submission
              ActorId = "github:42"
              Period = period
              SubmissionId = "SUB-1"
              By = "github:42"
              At = model.Now
              Covered = [ theirs.ActivityId, theirs.Revision ]
              Note = None }

        let waiting =
            { model with
                PeriodConfig = config
                Reviews = [ submission ]
                Reviewing = [ theirs ]
                Problems = model.Problems.Add(ReviewForm, [ Chrona.Domain.Diagnostics.SubmittedPeriodRestriction period.Start ]) }

        view (send { state with Model = Some waiting } (locationChanged $"#/periods/{period.Start:``yyyy-MM-dd``}"))

    // Observations' candidates (WI-0038): one waiting with evidence and a
    // link, one decided, from two sources, a refusal shown where it was
    // made, and a file in the inbox that is not an observation.
    let observing =
        let model = state.Model.Value
        let at = model.Now

        let observation: Chrona.Domain.Observations.Observation =
            { ObservationId = "obs-1"
              SourceSystem = "github"
              OrganizationId = model.Session.OrganizationId
              ProjectId = model.Ledger.Activities[first].Classification.ProjectId
              ActorId = Some model.Session.ActorId
              WorkItemId = Some "PR-7"
              ExternalUrl = Some "https://example.test/pr/7"
              Timing = Chrona.Domain.Observations.ObservedInterval(at.AddHours -2.0, at.AddHours -1.0)
              Description = Some "Reviewed the pull request"
              Evidence = [ "commit", "abc123" ]
              ObservedAt = at }

        let waiting: Chrona.Domain.Observations.Candidate =
            { CandidateId = "CAND-github-obs-1"
              Observation = observation
              ReceivedAt = at
              Disposition = Chrona.Domain.Observations.Pending
              Revision = 1
              Decisions = [ { By = "chrona"; At = at; Disposition = Chrona.Domain.Observations.Pending } ] }

        let decided =
            { waiting with
                CandidateId = "CAND-jira-obs-2"
                Observation = { observation with SourceSystem = "jira"; ObservationId = "obs-2"; Evidence = [] }
                Disposition = Chrona.Domain.Observations.Rejected "Not mine"
                Revision = 2 }

        let observed =
            { model with
                Candidates = [ waiting; decided ]
                Inbox =
                    { idleInbox with
                        Invalid = [ "github", "broken", [ "the payload is not JSON" ] ]
                        Orphaned = [ "jira", "PROJ-7", "CAND-jira-PROJ-7" ]
                        LastRun = Some at }
                Problems = model.Problems.Add(CandidateForm, [ Chrona.Domain.Diagnostics.MissingField "activityType" ]) }

        let opened = { state with Model = Some observed }
        [ view (send opened (locationChanged "#/candidates?status=all")); view (send opened (locationChanged "#/candidates/CAND-github-obs-1")) ]

    [ activity; review; month; today; project; view restricted; view troubled; view administering; reviewing ] @ observing

[<Fact>]
let ``the application page binds only what its engine projects and sends only what it handles`` () =
    let html = readRepoFile "web/index.html"
    let views = richViews ()

    let lists =
        views
        |> List.collect (List.choose (fun (name, value) -> match value with Chrona.Engine.View.Items items -> Some(name, items) | _ -> None))

    for name in lists |> List.map fst |> List.distinct do
        let everyEmpty = lists |> List.filter (fst >> (=) name) |> List.forall (snd >> List.isEmpty)
        Assert.False(everyEmpty, $"{name} is empty in every view, so its item fields are not checked")

    let names =
        views
        |> List.concat
        |> List.collect (fun (name, value) ->
            match value with
            | Chrona.Engine.View.Value _ -> [ name ]
            | Chrona.Engine.View.Items items -> name :: (items |> List.collect (List.map fst)))
        |> Set.ofList
        |> Set.union (Boundary.faultView None |> List.map fst |> Set.ofList)

    Assert.Empty(Set.difference (boundKeys html) names)
    // Every event the page can send is one the engine handles, and every
    // event the engine handles is one the page can send.
    Assert.Equal<Set<string>>(Set.ofList Chrona.Engine.App.Update.eventNames, attributeValues "data-event" html)

[<Fact>]
let ``an export is copied through Limen's clipboard effect, and its answer is shown`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let state, _ = run aegis env [ initialize; event "goReports" None "" ]
    let state, text = handle aegis env state (event "copyExport" None "")
    let copy = (effects text).Head
    Assert.Equal("Clipboard", copy.["kind"].GetValue<string>())
    Assert.Equal("writeText", copy.["operation"].GetValue<string>())
    Assert.StartsWith("# schema: chrona.time-report/1", copy.["text"].GetValue<string>())
    let id = copy.["correlationId"].GetValue<string>()
    let result = $"""{{"kind":"EffectResult","result":{{"kind":"ClipboardResult","correlationId":"{id}","outcome":{{"kind":"Success"}}}}}}"""
    let _, text = handle aegis env state result
    Assert.Equal("Copied to the clipboard.", viewText "copyStatus" text)

[<Fact>]
let ``without the clipboard effect the page says so at once; without the print pack nothing is requested`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let noClipboard = initializeWith """["Http","Storage","Navigation"]""" (offer $"{schedule},{environment}") "#/reports"
    let state, _ = run aegis env [ noClipboard ]
    let state, text = handle aegis env state (event "copyExport" None "")
    Assert.Empty(effects text)
    Assert.StartsWith("This browser did not allow copying.", viewText "copyStatus" text)
    let _, text = handle aegis env state (event "printReport" None "")
    Assert.Empty(effects text)

[<Fact>]
let ``the print pack is selected when offered and opens the print dialog`` () =
    let _, aegis = collector ()
    let env, _ = envWith committed
    let printOffer = $"""{{"id":"chrona.print","version":1,"fingerprint":"{AppProtocol.print.Fingerprint}"}}"""
    let state, text = run aegis env [ initializeWith events (offer $"{schedule},{environment},{printOffer}") "#/reports" ]
    Assert.Equal(3, (reply text).["handshake"].["capabilities"].AsArray().Count)
    let _, text = handle aegis env state (event "printReport" None "")
    let request = (effects text).Head
    Assert.Equal("chrona.print", request.["capability"].GetValue<string>())
    Assert.Equal("print", request.["request"].["action"].GetValue<string>())

/// Limen mounts one element for each `data-if` or `data-each` template: the
/// first. A second root would never be shown (as the period's reopen button
/// was not, until WI-0038 found it).
[<Fact>]
let ``every conditional or repeated part of the page is one element, as Limen mounts it`` () =
    let html = Text.RegularExpressions.Regex.Replace(readRepoFile "web/index.html", "<!--.*?-->", "", Text.RegularExpressions.RegexOptions.Singleline)
    let voids = set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "source"; "track"; "wbr" ]
    let tags = Text.RegularExpressions.Regex.Matches(html, "<(/?)([a-zA-Z][a-zA-Z0-9-]*)([^>]*)>")

    // Open elements, innermost first: the tag, the binding a template
    // carries, and the root elements found directly inside it so far.
    let mutable open' : (string * string option * string list) list = []
    let mutable found = []

    for tag in tags do
        let closing = tag.Groups[1].Value = "/"
        let name = tag.Groups[2].Value.ToLowerInvariant()
        let attributes = tag.Groups[3].Value

        if closing then
            let rec pop stack =
                match stack with
                | (top, binding, roots) :: rest ->
                    match binding with
                    | Some bound when List.length roots <> 1 -> found <- found @ [ bound, roots ]
                    | _ -> ()

                    if top = name then rest else pop rest
                | [] -> []

            open' <- pop open'
        else
            match open' with
            | (top, (Some _ as binding), roots) :: rest -> open' <- (top, binding, roots @ [ name ]) :: rest
            | _ -> ()

            if not (voids.Contains name) && not (attributes.TrimEnd().EndsWith "/") then
                let binding =
                    match Text.RegularExpressions.Regex.Match(attributes, "data-(?:if|each)=\"([^\"]+)\"") with
                    | m when name = "template" && m.Success -> Some m.Groups[1].Value
                    | _ -> None

                open' <- (name, binding, []) :: open'

    Assert.Empty(found)
