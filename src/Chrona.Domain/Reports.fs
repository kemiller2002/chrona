/// Search, reporting and export (requirements expansion 27, 28, 29).
///
/// Pure. A report is a filter over activities, totals that keep exact
/// recorded time apart from billable (rounded) time, groupings, and a
/// deterministic export: the same records, filter and generation time always
/// produce byte-identical CSV and JSON.
module Chrona.Domain.Reports

open System
open System.Buffers
open System.Globalization
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open Chrona.Domain.Activity

/// The schema of every export; it changes only with a new version.
[<Literal>]
let Schema = "chrona.time-report/1"

/// What the two durations in a report mean (29).
let semantics =
    [ "exactMinutes", "Recorded time in whole minutes. It is authoritative and is never rounded."
      "billableMinutes", "Billable time derived from exactMinutes by the named billing policy; it never replaces exactMinutes." ]

type EntryKind =
    | ManualEntries
    | TimerEntries
    | ImportedEntries

/// Who, what and when to report on (27). An empty list means any.
type Filter =
    { From: DateOnly
      To: DateOnly
      ActorIds: string list
      ProjectIds: string list
      ClientIds: string list
      EngagementIds: string list
      ActivityTypeIds: string list
      Tags: string list
      Billability: Billability list
      EntryKinds: EntryKind list
      /// Review states by name (`reviewName`), e.g. "approved".
      ReviewStates: string list
      /// Publication states by name (`publicationName`), e.g. "published".
      PublicationStates: string list
      /// Words that must all appear in the description, business purpose,
      /// work item or source, ignoring case.
      Text: string
      /// Include voided records (shown, never totalled).
      IncludeRemoved: bool }

let between (from: DateOnly) (``to``: DateOnly) =
    { From = from
      To = ``to``
      ActorIds = []
      ProjectIds = []
      ClientIds = []
      EngagementIds = []
      ActivityTypeIds = []
      Tags = []
      Billability = []
      EntryKinds = []
      ReviewStates = []
      PublicationStates = []
      Text = ""
      IncludeRemoved = false }

/// The review state's name, as filters and exports write it.
let reviewName =
    function
    | Unsubmitted -> "unsubmitted"
    | Submitted -> "submitted"
    | Approved -> "approved"
    | Rejected _ -> "rejected"
    | Reopened -> "reopened"

/// The publication state's name, as filters and exports write it.
let publicationName =
    function
    | NotBillable -> "not-billable"
    | Unpublished -> "unpublished"
    | ReadyForPublication -> "ready-for-publication"
    | Published -> "published"
    | InvoicedExternally -> "invoiced-externally"
    | AdjustmentRequired -> "adjustment-required"

let private kindOf (activity: Activity) =
    match activity.EntryMethod with
    | Manual -> ManualEntries
    | Timer -> TimerEntries
    | Imported _ -> ImportedEntries

let private any (chosen: 'a list) (value: 'a) = chosen.IsEmpty || List.contains value chosen

let private anyOf (chosen: 'a list) (value: 'a option) =
    chosen.IsEmpty || (value |> Option.exists (fun v -> List.contains v chosen))

let private words (text: string) =
    text.Split([| ' '; '\t'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.map _.ToLowerInvariant() |> Array.toList

let private searchable (activity: Activity) =
    [ activity.Classification.Description
      activity.Classification.BusinessPurpose
      defaultArg activity.WorkItemRef ""
      defaultArg activity.ExternalRef ""
      match activity.EntryMethod with
      | Imported source -> source
      | _ -> "" ]
    |> String.concat " "
    |> _.ToLowerInvariant()

/// Whether an activity is in the report. Superseded records never are: their
/// time lives on in what replaced them.
let matches (filter: Filter) (activity: Activity) =
    let state =
        match activity.Record with
        | Recorded -> true
        | Voided _ -> filter.IncludeRemoved
        | Superseded _ -> false

    let haystack = searchable activity

    state
    && activity.Occurrence.LocalDate >= filter.From
    && activity.Occurrence.LocalDate <= filter.To
    && any filter.ActorIds activity.ActorId
    && any filter.ProjectIds activity.Classification.ProjectId
    && anyOf filter.ClientIds activity.Classification.ClientId
    && anyOf filter.EngagementIds activity.Classification.EngagementId
    && any filter.ActivityTypeIds activity.Classification.ActivityTypeId
    && (filter.Tags.IsEmpty || activity.Classification.Tags |> List.exists (fun t -> List.contains t filter.Tags))
    && any filter.Billability activity.Billability
    && any filter.EntryKinds (kindOf activity)
    && any filter.ReviewStates (reviewName activity.Review)
    && any filter.PublicationStates (publicationName activity.Publication)
    && words filter.Text |> List.forall haystack.Contains

/// The report's activities in a stable order: date, local start, id.
let select (filter: Filter) (activities: Activity list) =
    activities
    |> List.filter (matches filter)
    |> List.sortBy (fun a -> a.Occurrence.LocalDate, a.Occurrence.LocalTime, a.ActivityId)

/// Totals (28). Only records that consume time are totalled.
type Totals =
    { Count: int
      ExactMinutes: int
      BillableMinutes: int
      NonBillableMinutes: int
      UnclassifiedMinutes: int
      TimerMinutes: int
      ManualMinutes: int
      ImportedMinutes: int
      ApprovedMinutes: int
      UnapprovedMinutes: int
      PublishedMinutes: int
      UnpublishedMinutes: int
      /// Changed after it had been submitted or approved (reopened).
      AmendedAfterReview: int
      /// Changed after it had been published or invoiced.
      CorrectedAfterPublication: int }

/// Billable minutes as billed; an unresolvable policy bills exact minutes.
let billed (policies: Billing.BillingPolicy list) (activity: Activity) =
    match activity.Billability with
    | Billable -> Billing.project policies activity |> Result.map _.BillableMinutes |> Result.defaultValue activity.Minutes
    | NonBillable
    | PendingClassification -> 0

let totals (policies: Billing.BillingPolicy list) (activities: Activity list) =
    let counted = activities |> List.filter consumesTime
    let sum (pick: Activity -> bool) = counted |> List.filter pick |> List.sumBy _.Minutes

    let published (a: Activity) =
        match a.Publication with
        | Published
        | InvoicedExternally -> true
        | _ -> false

    { Count = counted.Length
      ExactMinutes = sum (fun _ -> true)
      BillableMinutes = counted |> List.sumBy (billed policies)
      NonBillableMinutes = sum (fun a -> a.Billability = NonBillable)
      UnclassifiedMinutes = sum (fun a -> a.Billability = PendingClassification)
      TimerMinutes = sum (fun a -> kindOf a = TimerEntries)
      ManualMinutes = sum (fun a -> kindOf a = ManualEntries)
      ImportedMinutes = sum (fun a -> kindOf a = ImportedEntries)
      ApprovedMinutes = sum (fun a -> a.Review = Approved)
      UnapprovedMinutes = sum (fun a -> a.Review <> Approved)
      PublishedMinutes = sum published
      UnpublishedMinutes = sum (published >> not)
      AmendedAfterReview = counted |> List.filter (fun a -> a.Review = Reopened) |> List.length
      CorrectedAfterPublication = counted |> List.filter (fun a -> a.Publication = AdjustmentRequired) |> List.length }

type Grouping =
    | ByDay
    | ByActor
    | ByClient
    | ByProject
    | ByEngagement
    | ByActivityType
    | ByTag

/// Totals per group key, by key. An activity with several tags counts once
/// under each, so tag groups may add up to more than the report total; one
/// with no client or engagement groups under "".
let groupBy (policies: Billing.BillingPolicy list) (grouping: Grouping) (activities: Activity list) =
    let keys (a: Activity) =
        match grouping with
        | ByDay -> [ a.Occurrence.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ]
        | ByActor -> [ a.ActorId ]
        | ByClient -> [ defaultArg a.Classification.ClientId "" ]
        | ByProject -> [ a.Classification.ProjectId ]
        | ByEngagement -> [ defaultArg a.Classification.EngagementId "" ]
        | ByActivityType -> [ a.Classification.ActivityTypeId ]
        | ByTag -> if a.Classification.Tags.IsEmpty then [ "" ] else a.Classification.Tags

    activities
    |> List.collect (fun a -> keys a |> List.map (fun k -> k, a))
    |> List.groupBy fst
    |> List.map (fun (key, pairs) -> key, totals policies (List.map snd pairs))
    |> List.sortBy fst

// ---- export (29) ------------------------------------------------------------------

/// What an export says about itself.
type Header =
    { OrganizationId: string
      GeneratedAt: DateTimeOffset
      Filter: Filter }

let private iso (date: DateOnly) = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
let private instant (value: DateTimeOffset) = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)

let private kindName =
    function
    | ManualEntries -> "manual"
    | TimerEntries -> "timer"
    | ImportedEntries -> "imported"

let private billabilityName =
    function
    | Billable -> "billable"
    | NonBillable -> "non-billable"
    | PendingClassification -> "pending-classification"

/// The filter, as named values in a fixed order.
let filterFields (filter: Filter) =
    let list (values: string list) = String.Join(";", values)

    [ "from", iso filter.From
      "to", iso filter.To
      "actors", list filter.ActorIds
      "clients", list filter.ClientIds
      "projects", list filter.ProjectIds
      "engagements", list filter.EngagementIds
      "activityTypes", list filter.ActivityTypeIds
      "tags", list filter.Tags
      "billability", list (filter.Billability |> List.map billabilityName)
      "entryMethods", list (filter.EntryKinds |> List.map kindName)
      "reviewStates", list filter.ReviewStates
      "publicationStates", list filter.PublicationStates
      "text", filter.Text.Trim()
      "includeRemoved", (if filter.IncludeRemoved then "true" else "false") ]

let private recordName (activity: Activity) =
    match activity.Record with
    | Recorded -> "recorded"
    | Voided _ -> "voided"
    | Superseded _ -> "superseded"

let private policyOf (policies: Billing.BillingPolicy list) (activity: Activity) =
    match activity.Billability, Billing.resolve policies activity with
    | Billable, Ok policy -> $"{policy.PolicyId}@{policy.Version}"
    | _ -> ""

/// The columns of one activity, in export order. Times are the activity's
/// own local wall clock and UTC offset, as recorded.
let private columns (policies: Billing.BillingPolicy list) (activity: Activity) =
    let start, finish =
        match activity.Timing with
        | Interval(s, f) -> instant s, instant f
        | DurationOnDate _ -> "", ""

    [ "activityId", activity.ActivityId
      "organizationId", activity.OrganizationId
      "actorId", activity.ActorId
      "date", iso activity.Occurrence.LocalDate
      "timeZone", activity.Occurrence.Zone
      "startUtc", start
      "endUtc", finish
      "exactMinutes", string activity.Minutes
      "billableMinutes", string (billed policies activity)
      "billingPolicy", policyOf policies activity
      "billability", billabilityName activity.Billability
      "clientId", defaultArg activity.Classification.ClientId ""
      "projectId", activity.Classification.ProjectId
      "engagementId", defaultArg activity.Classification.EngagementId ""
      "activityTypeId", activity.Classification.ActivityTypeId
      "tags", String.Join(";", activity.Classification.Tags)
      "description", activity.Classification.Description
      "businessPurpose", activity.Classification.BusinessPurpose
      "entryMethod", kindName (kindOf activity)
      "record", recordName activity
      "review", reviewName activity.Review
      "publication", publicationName activity.Publication
      "revision", string activity.Revision ]

/// The column row of a CSV export, in order (`columns` yields the same
/// names for every activity; a test holds them together).
let columnNames =
    [ "activityId"; "organizationId"; "actorId"; "date"; "timeZone"; "startUtc"; "endUtc"; "exactMinutes"; "billableMinutes"; "billingPolicy"
      "billability"; "clientId"; "projectId"; "engagementId"; "activityTypeId"; "tags"; "description"; "businessPurpose"; "entryMethod"
      "record"; "review"; "publication"; "revision" ]

/// An activity's export columns, by name.
let row (policies: Billing.BillingPolicy list) (activity: Activity) = columns policies activity

let private quote (value: string) =
    if value.IndexOfAny([| ','; '"'; '\r'; '\n' |]) >= 0 || value <> value.Trim() then
        "\"" + value.Replace("\"", "\"\"") + "\""
    else
        value

/// RFC 4180 CSV with LF line endings. The header block, lines beginning
/// `#`, names the schema, organization, generation time, filter and duration
/// semantics before the column row.
let csv (policies: Billing.BillingPolicy list) (header: Header) (activities: Activity list) =
    let rows = select header.Filter activities

    let meta =
        [ "schema", Schema
          "organization", header.OrganizationId
          "generatedAt", instant header.GeneratedAt
          yield! filterFields header.Filter |> List.map (fun (k, v) -> $"filter.{k}", v)
          yield! semantics |> List.map (fun (k, v) -> $"semantics.{k}", v) ]
        |> List.map (fun (k, v) -> $"# {k}: {v}")

    let lines =
        meta
        @ [ String.Join(",", columnNames) ]
        @ (rows |> List.map (fun a -> columns policies a |> List.map (snd >> quote) |> fun values -> String.Join(",", values)))

    String.Join("\n", lines) + "\n"

/// The same report as JSON: keys in a fixed order, indented, no trailing
/// whitespace.
let json (policies: Billing.BillingPolicy list) (header: Header) (activities: Activity list) =
    let rows = select header.Filter activities
    let sum = totals policies rows
    let buffer = ArrayBufferWriter<byte>()

    (
        use w = new Utf8JsonWriter(buffer, JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))
        w.WriteStartObject()
        w.WriteString("schema", Schema)
        w.WriteString("organization", header.OrganizationId)
        w.WriteString("generatedAt", instant header.GeneratedAt)
        w.WritePropertyName "filter"
        w.WriteStartObject()
        for k, v in filterFields header.Filter do
            w.WriteString(k, v)
        w.WriteEndObject()
        w.WritePropertyName "semantics"
        w.WriteStartObject()
        for k, v in semantics do
            w.WriteString(k, v)
        w.WriteEndObject()
        w.WritePropertyName "totals"
        w.WriteStartObject()
        w.WriteNumber("count", sum.Count)
        w.WriteNumber("exactMinutes", sum.ExactMinutes)
        w.WriteNumber("billableMinutes", sum.BillableMinutes)
        w.WriteNumber("nonBillableMinutes", sum.NonBillableMinutes)
        w.WriteNumber("unclassifiedMinutes", sum.UnclassifiedMinutes)
        w.WriteNumber("timerMinutes", sum.TimerMinutes)
        w.WriteNumber("manualMinutes", sum.ManualMinutes)
        w.WriteNumber("importedMinutes", sum.ImportedMinutes)
        w.WriteNumber("approvedMinutes", sum.ApprovedMinutes)
        w.WriteNumber("unapprovedMinutes", sum.UnapprovedMinutes)
        w.WriteNumber("publishedMinutes", sum.PublishedMinutes)
        w.WriteNumber("unpublishedMinutes", sum.UnpublishedMinutes)
        w.WriteNumber("amendedAfterReview", sum.AmendedAfterReview)
        w.WriteNumber("correctedAfterPublication", sum.CorrectedAfterPublication)
        w.WriteEndObject()
        w.WritePropertyName "activities"
        w.WriteStartArray()
        for a in rows do
            w.WriteStartObject()
            for k, v in columns policies a do
                match k with
                | "exactMinutes"
                | "billableMinutes"
                | "revision" -> w.WriteNumber(k, Int32.Parse(v, CultureInfo.InvariantCulture))
                | _ -> w.WriteString(k, v)
            w.WriteEndObject()
        w.WriteEndArray()
        w.WriteEndObject()
    )

    Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n"
