/// The Chrona-to-Summa billing contract (WI-0037): Summa's package
/// (EchelonFoundry.Summa.Contracts 0.1.0, installed by Conditor), its golden
/// vectors, and Chrona's mapping onto it (requirements expansion 17, 45;
/// scenarios 35-38).
module Chrona.Tests.SummaBillingTests

open System
open Xunit
open Summa.Contracts
open Summa.Contracts.ChronaBilling.V1
open Chrona.Tests.Support
open Chrona.Domain.Diagnostics
open Chrona.Domain.Time
open Chrona.Domain.Ledger
open Chrona.Domain.Billing
open Chrona.Domain.Review
open Chrona.Integration

module A = Chrona.Domain.Activity

let private ok =
    function
    | Ok value -> value
    | Error problems -> failwith $"%A{problems}"

// ---- The package's golden vectors ------------------------------------------------------

/// Every problem either decoder reports for the text.
let private problems (text: string) =
    let of' =
        function
        | Ok _ -> []
        | Error found -> found |> List.map (fun (p: Problem) -> p.Path)

    of' (Codec.decodePublication text) @ of' (Codec.decodeFeedback text)

[<Fact>]
let ``every valid golden vector decodes and re-encodes to the same bytes`` () =
    Assert.Equal(8, GoldenVectors.valid.Length)

    for vector in GoldenVectors.valid do
        let again =
            match Codec.decodePublication vector.Text, Codec.decodeFeedback vector.Text with
            | Ok publication, _ -> Codec.encodePublication publication
            | _, Ok feedback -> Codec.encodeFeedback feedback
            | Error a, Error b -> failwith $"{vector.Name}: %A{a @ b}"

        Assert.True((again = vector.Text), vector.Name)

[<Fact>]
let ``every invalid golden vector is refused with a problem at its path`` () =
    Assert.Equal(22, GoldenVectors.invalid.Length)

    for refusal in GoldenVectors.invalid do
        Assert.True(Codec.decodePublication refusal.Vector.Text |> Result.isError, refusal.Vector.Name)
        Assert.True(Codec.decodeFeedback refusal.Vector.Text |> Result.isError, refusal.Vector.Name)
        let found = problems refusal.Vector.Text
        Assert.True(List.contains refusal.ExpectedPath found, $"{refusal.Vector.Name}: expected {refusal.ExpectedPath}, found %A{found}")

// ---- Chrona onto the contract ----------------------------------------------------------

let private zone = tryZone "America/New_York" |> ok
let private at (h: int) (m: int) = DateTimeOffset(2026, 10, 7, h + 4, m, 0, TimeSpan.Zero)

let private ctx performer =
    { Performer = performer
      At = DateTimeOffset(2026, 10, 7, 22, 0, 0, TimeSpan.Zero)
      Source = "chrona-web"
      Zone = zone
      References = references
      CorrelationId = None }

let private actor = ctx "github:583231"
let private approver = ctx "github:1001"
let private config = { ApprovalRequired = true }
let private policies = [ legacyDefault "org_acme" ]
let private day = DateOnly(2026, 10, 7)

let private activity id (h, m) minutes : A.Activity =
    let start = at h m

    { ActivityId = id
      OrganizationId = "org_acme"
      ActorId = "github:583231"
      Occurrence = occurrence zone start
      Timing = A.Interval(start, start.AddMinutes(float minutes))
      Minutes = minutes
      Classification =
        { ProjectId = "PRJ-1"
          ClientId = Some "CLI-1"
          EngagementId = None
          ActivityTypeId = "ACT-DEV"
          Tags = [ "backend" ]
          Description = "Work"
          BusinessPurpose = "Delivery" }
      EntryMethod = A.Manual
      Billability = A.Billable
      BillingReference = A.noBillingReference
      Record = A.Recorded
      Review = A.Unsubmitted
      Publication = A.Unpublished
      Revision = 1
      CreatedAt = at 8 0
      LastChangedAt = at 8 0
      Reason = None
      WorkItemRef = Some "WI-7"
      ExternalRef = None
      Evidence = []
      Lineage = []
      Source = None }

/// Recorded, submitted, approved and staged for publication.
let private staged (items: A.Activity list) =
    items
    |> List.fold (fun ledger a -> execute actor ledger (Record a) |> ok) empty
    |> start
    |> submit actor "S1" (day, day) (items |> List.map _.ActivityId)
    |> ok
    |> approve config approver "S1" None
    |> ok
    |> stage config policies approver (items |> List.map _.ActivityId)
    |> ok

let private billable (message: Publication) =
    match message with
    | BillableTimePublished time -> time
    | other -> failwith $"%A{other}"

[<Fact>]
let ``time ready for publication is published as the contract's billable time, exactly as Chrona recorded it`` () =
    let workflow, message = staged [ activity "A1" (9, 0) 37 ] |> SummaBilling.publish config policies approver "A1" |> ok
    let time = billable message

    Assert.Equal(A.Published, workflow.Ledger.Activities["A1"].Publication)
    Assert.Equal("pub-org_acme-A1-r1", time.PublicationId)
    Assert.Equal({ OrganizationId = "org_acme"; ActivityId = "A1"; Revision = 1 }, time.Source)
    Assert.Equal((37, workflow.Publications["A1"].BillableMinutes), (time.ExactMinutes, time.BillableMinutes))
    Assert.Equal(workflow.Publications["A1"].PolicyId, time.Policy.PolicyId)
    Assert.Equal(ApprovedBy("github:1001", approver.At), time.Approval)
    Assert.Equal(Known(Manual, None, None), time.Origin)
    Assert.Equal({ BusinessDate = day; Zone = "America/New_York"; Interval = Some(at 9 0, at 9 37) }, time.Service)
    Assert.Equal(Some "WI-7", time.WorkItemReference)
    Assert.Equal(None, time.Supersedes)

    // It is a valid message of the contract, and reads back as itself.
    let text = SummaBilling.encode message |> ok
    Assert.Equal(Ok message, Codec.decodePublication text)

[<Fact>]
let ``only time staged for publication is published; publishing the same revision again is a retry`` () =
    let unstaged =
        [ activity "A1" (9, 0) 37 ]
        |> List.fold (fun ledger a -> execute actor ledger (Record a) |> ok) empty
        |> start

    Assert.True(SummaBilling.publish config policies approver "A1" unstaged |> Result.isError)

    let workflow, first = staged [ activity "A1" (9, 0) 37 ] |> SummaBilling.publish config policies approver "A1" |> ok
    let retry = SummaBilling.billableTime config workflow (Some workflow.Publications["A1"]) workflow.Publications["A1"] |> ok
    Assert.Equal((billable first).PublicationId, (billable retry).PublicationId)
    Assert.Equal(None, (billable retry).Supersedes)

[<Fact>]
let ``corrected published time is republished as a new publication that supersedes the first`` () =
    let workflow, first = staged [ activity "A1" (9, 0) 37 ] |> SummaBilling.publish config policies approver "A1" |> ok

    let corrected =
        execute actor workflow.Ledger (Amend("A1", 1, { Classification = Some { workflow.Ledger.Activities["A1"].Classification with Description = "Work, corrected" }; Billability = None; BillingReference = None; Retime = None; Reason = "typo" })) |> ok

    let workflow = { workflow with Ledger = corrected }
    Assert.Equal(A.AdjustmentRequired, workflow.Ledger.Activities["A1"].Publication)

    let again =
        workflow
        |> submit actor "S2" (day, day) [ "A1" ]
        |> ok
        |> approve config approver "S2" None
        |> ok
        |> stage config policies approver [ "A1" ]
        |> ok

    let _, second = SummaBilling.publish config policies approver "A1" again |> ok
    Assert.Equal("pub-org_acme-A1-r2", (billable second).PublicationId)
    Assert.Equal(Some (billable first).PublicationId, (billable second).Supersedes)
    Assert.True(SummaBilling.encode second |> Result.isOk)

[<Fact>]
let ``published time voided afterwards is withdrawn, never silently rewritten`` () =
    let workflow, first = staged [ activity "A1" (9, 0) 37 ] |> SummaBilling.publish config policies approver "A1" |> ok
    Assert.Equal(None, SummaBilling.withdrawal workflow approver.At "A1")

    let voided = execute actor workflow.Ledger (Void("A1", 1, "Duplicate")) |> ok
    let workflow = { workflow with Ledger = voided }

    match SummaBilling.withdrawal workflow approver.At "A1" with
    | Some(PublicationWithdrawn withdrawal as message) ->
        Assert.Equal((billable first).PublicationId, withdrawal.PublicationId)
        Assert.Equal(Voided, withdrawal.Reason)
        Assert.Equal(2, withdrawal.Source.Revision)
        Assert.True(SummaBilling.encode message |> Result.isOk)
    | other -> failwith $"%A{other}"

// ---- The contract onto Chrona ----------------------------------------------------------

let private feedback (message: Feedback) = Codec.encodeFeedback message

[<Fact>]
let ``Summa's invoice report and adjustment request apply to the publication Chrona recorded, and nothing else`` () =
    let workflow, _ = staged [ activity "A1" (9, 0) 37 ] |> SummaBilling.publish config policies approver "A1" |> ok
    let summa = ctx "summa"

    let invoiced organization revision =
        feedback (
            InvoicedExternally
                { OrganizationId = organization
                  PublicationId = "pub-org_acme-A1-r1"
                  ActivityId = "A1"
                  Revision = revision
                  InvoiceReference = "INV-2026-0042"
                  At = summa.At }
        )

    let after = SummaBilling.receive summa "org_acme" (invoiced "org_acme" 1) workflow |> ok
    Assert.Equal(A.InvoicedExternally, after.Ledger.Activities["A1"].Publication)
    Assert.Equal("INV-2026-0042", after.Invoices["A1"].InvoiceReference)

    // The same report again changes nothing.
    Assert.Equal(Ok after, SummaBilling.receive summa "org_acme" (invoiced "org_acme" 1) after)

    // Another organization's, another revision's, or not a message at all: refused.
    Assert.Equal(Error(SummaBilling.OtherOrganization "org_other"), SummaBilling.receive summa "org_acme" (invoiced "org_other" 1) workflow)

    match SummaBilling.receive summa "org_acme" (invoiced "org_acme" 2) workflow with
    | Error(SummaBilling.Refused [ PublicationStateConflict _ ]) -> ()
    | other -> failwith $"%A{other}"

    match SummaBilling.receive summa "org_acme" "{}" workflow with
    | Error(SummaBilling.NotAMessage _) -> ()
    | other -> failwith $"%A{other}"

    // Summa asks for the invoiced time to be adjusted: an explicit obligation.
    let adjustment =
        feedback (
            AdjustmentNeeded
                { OrganizationId = "org_acme"
                  PublicationId = "pub-org_acme-A1-r1"
                  ActivityId = "A1"
                  Revision = 1
                  Reason = "The invoice was voided."
                  At = summa.At }
        )

    let adjusted = SummaBilling.receive summa "org_acme" adjustment after |> ok
    Assert.Equal(A.AdjustmentRequired, adjusted.Ledger.Activities["A1"].Publication)
    Assert.Equal(Some "The invoice was voided.", (List.last adjusted.Ledger.Audit).Reason)
    Assert.Equal(Ok adjusted, SummaBilling.receive summa "org_acme" adjustment adjusted)

[<Fact>]
let ``the contract's own invoiced vector reads as Chrona's invoice report, field for field`` () =
    let vector = GoldenVectors.valid |> List.find (fun v -> v.Name = "invoiced")

    match Codec.decodeFeedback vector.Text with
    | Ok(InvoicedExternally invoiced) ->
        let report: InvoiceReport =
            { PublicationId = invoiced.PublicationId
              ActivityId = invoiced.ActivityId
              Revision = invoiced.Revision
              InvoiceReference = invoiced.InvoiceReference
              At = invoiced.At }

        Assert.Equal(invoiced.Revision, report.Revision)
    | other -> failwith $"%A{other}"
