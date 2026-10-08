/// One organization member (requirements expansion 3) as an Arca record:
/// `records/chrona.member/<principal>.json` inside the organization's
/// folder, mutable under its revision. The roster is the set of member
/// records; a removed member's record is deleted.
///
/// Pure.
module Chrona.Domain.MemberRecord

open Arca
open Chrona.Domain.Diagnostics
open Chrona.Domain.Access
open Chrona.Domain.Codec

/// The record type of a membership.
let recordType =
    match RecordType.create "chrona.member" with
    | Ok found -> found
    | Error text -> invalidOp ("internal: not a record type: " + text)

/// The membership schema versions this Chrona reads and writes.
let schema =
    { Type = recordType
      OldestReadable = 1
      Current = 1 }

/// The record id of a principal: its id as a path segment, so it is stable
/// and distinct (ActivityRecord.actorSegment).
let idOf (principalId: string) = ActivityRecord.actorSegment principalId

/// The membership's record key.
let key (principalId: string) : Result<RecordKey, Diagnostic> =
    match RecordId.create (idOf principalId) with
    | Ok id ->
        Ok
            { Type = recordType
              Partition = []
              Id = id }
    | Error _ -> Error(UnstorableRecord(principalId, "the principal id is too long to store"))

/// The membership's path inside its organization's folder.
let path (principalId: string) : Result<RelativePath, Diagnostic> =
    key principalId
    |> Result.bind (Layout.recordPath >> Result.mapError (LocationError.describe >> InvalidDataLocation))

/// The folder of the organization's members.
let folder () : Result<RelativePath, Diagnostic> =
    RelativePath.parse (String.concat "/" [ Layout.RecordsFolder; RecordType.value recordType ])
    |> Result.mapError (LocationError.describe >> InvalidDataLocation)

/// The membership's record body.
let body (membership: Membership) =
    Json.objectOf
        [ "principalId", Json.String membership.Principal.PrincipalId
          "kind", Json.String(kindName membership.Principal.Kind)
          "displayName", Json.String membership.Principal.DisplayName
          "capabilities",
          Json.Array(
              allCapabilities
              |> List.filter membership.Capabilities.Contains
              |> List.map (capabilityName >> Json.String)
          )
          "revision", Json.Number(decimal membership.Revision) ]

/// The membership's canonical stored text.
let encode (membership: Membership) : Result<string, Diagnostic> =
    key membership.Principal.PrincipalId
    |> Result.bind (fun found ->
        { Id = found.Id
          Type = recordType
          SchemaVersion = schema.Current
          Mutability = Mutability.Mutable
          Body = body membership }
        |> Record.encode Record.DefaultMaxBytes
        |> Result.mapError (fun _ -> UnstorableRecord(membership.Principal.PrincipalId, "the membership record is too large")))

/// A membership from its record body. A capability or kind this version
/// does not know is refused, never dropped: a roster read must be the
/// roster stored.
let ofBody (value: Json) : Decoded<Membership> =
    closed [ "capabilities"; "displayName"; "kind"; "principalId"; "revision" ] value
    |> Result.bind (fun () ->
        match text "principalId" value, text "kind" value, text "displayName" value, texts "capabilities" value, integer "revision" value with
        | Ok principalId, Ok kind, Ok displayName, Ok capabilities, Ok revision ->
            match kindOf kind, capabilities |> List.map capabilityOf with
            | None, _ -> Error $"'{kind}' is not a kind of principal"
            | Some _, found when found |> List.exists Option.isNone -> Error "a capability is not one this version knows"
            | Some _, _ when revision < 1 -> Error "'revision' must be at least 1"
            | Some kind, found ->
                let held = found |> List.choose id |> Set.ofList

                if held |> Set.exists (permitsKind kind >> not) then
                    Error "a person-only capability is held by a principal that is not a person"
                else
                    Ok
                        { Principal =
                            { PrincipalId = principalId
                              Kind = kind
                              DisplayName = displayName }
                          Capabilities = held
                          Revision = revision }
        | Error e, _, _, _, _
        | _, Error e, _, _, _
        | _, _, Error e, _, _
        | _, _, _, Error e, _
        | _, _, _, _, Error e -> Error e)
