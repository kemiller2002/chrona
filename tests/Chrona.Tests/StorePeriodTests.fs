/// Periods under review, stored on Arca (WI-0036).
[<Xunit.Collection("Store")>]
module Chrona.Tests.StorePeriodTests

open System
open Xunit
open Arca
open Chrona.Domain
open Chrona.Domain.Diagnostics
open Chrona.Engine.App
open Chrona.Engine.App.ModelPorts
open Chrona.Engine.App.ModelForms
open Chrona.Engine.App.Model
open Chrona.Engine.App.Update
open Chrona.Application
open Chrona.Tests.StoreBrowserFixture
open Chrona.Tests.StoreDeviceFixture
open Chrona.Tests.StoreFixtures

// ---- Periods under review on Arca (WI-0036) ---------------------------------

/// Saves the organization's period settings, as its administrator.
let private periodSettings (device: Device) (approval: bool) =
    device.Send(Ui("periodSubmissionExpected", None, "", Some true))
    device.Send(Ui("periodApprovalRequired", None, "", Some approval))
    device.Ui("savePeriodSettings", "")

[<Fact>]
let ``the organization's period settings are stored and every device reads them`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    device.Ui("periodCadence", "monthly")
    device.Ui("periodWeekStart", "Sunday")
    periodSettings device true
    Assert.Empty(device.Model.Store.Pending)

    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    Assert.Equal(Chrona.Domain.Periods.Monthly, other.Model.PeriodConfig.Cadence)
    Assert.Equal(DayOfWeek.Sunday, other.Model.PeriodConfig.WeekStart)
    Assert.True(other.Model.PeriodConfig.SubmissionExpected)
    Assert.True(other.Model.PeriodConfig.ApprovalRequired)
    Assert.Equal(Some 1, other.Model.PeriodsStored |> Option.map _.Revision)

    // A second save is the next revision; one made from a stale read is a
    // conflict, never a silent overwrite.
    other.Ui("periodCadence", "weekly")
    other.Ui("savePeriodSettings", "")
    Assert.Empty(other.Model.Store.Conflicts)
    device.Ui("periodCadence", "daily")
    device.Ui("savePeriodSettings", "")

    match device.Model.Store.Conflicts with
    | [ { Divergences = [ Reconcile.PeriodsChanged _ ] } ] -> ()
    | other -> failwith $"%A{other}"

[<Fact>]
let ``a submitted period holds its time until it is reopened, and that is stored`` () =
    let github = InMemoryStore()
    let device = Device(github, RepositoryVisibility.Private, "production")
    device.Open()
    periodSettings device false
    record device "08:00" "08:30" "Setup"
    let id = device.Model.Ledger.Activities |> Map.toList |> List.head |> fst

    device.Ui("submitPeriod", "")
    Assert.Empty(device.Model.Store.Pending)
    Assert.Equal(Chrona.Domain.Activity.Submitted, device.Model.Ledger.Activities[id].Review)

    // Read back elsewhere: the period is closed (no approval required).
    let other = Device(github, RepositoryVisibility.Private, "production")
    other.Open()
    Assert.Equal(Chrona.Domain.Activity.Submitted, other.Model.Ledger.Activities[id].Review)
    Assert.Single(other.Model.Reviews) |> ignore

    // Its time is not added to, changed or attested.
    record other "09:00" "09:30" "Late"
    Assert.Equal<string list>([ "CHRONA.REVIEW.SUBMITTED_PERIOD" ], other.Model.Problems[ManualForm] |> List.map code)
    other.Ui("attestStatement", "All of it")
    other.Ui("attestDay", "")
    Assert.Equal<string list>([ "CHRONA.REVIEW.SUBMITTED_PERIOD" ], other.Model.Problems[AttestForm] |> List.map code)

    // Reopened with a reason (the administrator may reopen time), it can be
    // changed again, and the reopening is stored.
    other.Ui("reopenReason", "A missing entry")
    other.Ui("reopenPeriod", "")
    Assert.Empty(other.Model.Store.Pending)
    record other "09:00" "09:30" "Late"
    Assert.Empty(other.Model.Store.Pending)

    let reader = Device(github, RepositoryVisibility.Private, "production")
    reader.Open()
    Assert.Equal(Chrona.Domain.Activity.Reopened, reader.Model.Ledger.Activities[id].Review)
    Assert.Equal(2, reader.Model.Reviews.Length)
    // Each step's audit entry is stored with the time it changed (25).
    Assert.Equal<string list>([ "reopen"; "submit" ], reader.Model.Ledger.Audit |> List.map _.Command |> List.filter (fun c -> c = "submit" || c = "reopen") |> List.sort)
    Assert.Equal<string list>([ "Late"; "Setup" ], descriptions reader)

[<Fact>]
let ``someone who approves sees another member's submission, approves it, and the member's period is closed`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    periodSettings owner true
    // Reference data hubot records against.
    record owner "07:00" "07:30" "Owner's own"

    let teammate = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    teammate.Open()
    record teammate "08:00" "08:30" "Hubot's work"
    teammate.Ui("submitPeriod", "")
    Assert.Empty(teammate.Model.Store.Pending)

    // The approver opens the records again: the submission waits for them.
    let approver = Device(github, RepositoryVisibility.Private, "production")
    approver.Open()
    Assert.True(viewFlag "hasObligations" approver.Model)
    Assert.Single(approver.Model.Reviewing) |> ignore
    Assert.Equal<string list>([ "Owner's own" ], descriptions approver)

    let submission = Chrona.Domain.PeriodReview.awaitingApproval true approver.Model.Reviews |> List.exactlyOne
    approver.Send(Ui("reviewNote", Some submission.SubmissionId, "Looks right", None))
    approver.Send(Ui("approveSubmission", Some submission.SubmissionId, "", None))
    Assert.Empty(approver.Model.Store.Pending)
    Assert.Empty(approver.Model.Store.Conflicts)

    let back = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    back.Open()
    Assert.Equal<Chrona.Domain.Activity.ReviewState list>([ Chrona.Domain.Activity.Approved ], back.Model.Ledger.Activities |> Map.toList |> List.map (snd >> _.Review))

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.Closed approval ->
        Assert.Equal(octocat.ActorId, approval.By)
        Assert.Equal(Some "Looks right", approval.Note)
    | other -> failwith $"%A{other}"

    // The member cannot reopen approved time on their own.
    back.Ui("reopenReason", "Changed my mind")
    back.Ui("reopenPeriod", "")
    Assert.Equal<string list>([ "CHRONA.AUTH.UNAUTHORIZED_CAPABILITY" ], back.Model.Problems[ReviewForm] |> List.map code)

[<Fact>]
let ``a returned submission is open for correction, with the reason, and can be submitted again`` () =
    let github = InMemoryStore()
    let config = configuration "production"
    let owner = Device(github, RepositoryVisibility.Private, "production")
    owner.Open()
    owner.Ui("memberId", "1001")
    owner.Ui("memberName", "hubot")
    owner.Ui("memberAccess", "ownTime")
    owner.Ui("admitMember", "")
    periodSettings owner true
    record owner "07:00" "07:30" "Owner's own"

    let teammate = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    teammate.Open()
    record teammate "08:00" "08:30" "Hubot's work"
    teammate.Ui("submitPeriod", "")

    let approver = Device(github, RepositoryVisibility.Private, "production")
    approver.Open()
    let submission = Chrona.Domain.PeriodReview.awaitingApproval true approver.Model.Reviews |> List.exactlyOne
    approver.Send(Ui("reviewNote", Some submission.SubmissionId, "Add the standup", None))
    approver.Send(Ui("rejectSubmission", Some submission.SubmissionId, "", None))
    Assert.Empty(approver.Model.Store.Pending)

    let back = Device(github, RepositoryVisibility.Private, "production", hubot, config)
    back.Open()

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.Returned rejection -> Assert.Equal(Some "Add the standup", rejection.Note)
    | other -> failwith $"%A{other}"

    record back "09:00" "09:15" "Standup"
    Assert.Empty(back.Model.Store.Pending)
    // Submitted again, later.
    back.Now <- start.AddMinutes 30.0
    back.Ui("submitPeriod", "")
    Assert.Empty(back.Model.Store.Pending)
    Assert.Empty(back.Model.Store.Conflicts)

    match Chrona.Domain.PeriodReview.stateOf true back.Model.Reviews hubot.ActorId submission.Period with
    | Chrona.Domain.PeriodReview.AwaitingApproval again -> Assert.Equal(2, again.Covered.Length)
    | other -> failwith $"%A{other}"
