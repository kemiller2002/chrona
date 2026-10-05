/// index.html and the engine are not type-checked against each other: the
/// page names events and view keys as strings. These tests hold the page to
/// its engine in both directions, so a renamed key fails here instead of
/// silently rendering nothing (Limen unmounts a data-if whose key is missing).
module Chrona.Tests.BindingAgreementTests

open System.Text.RegularExpressions
open Xunit
open Chrona.Engine.View
open Chrona.Engine.KernelSlice
open Chrona.Tests.Support

let private attributeValues (attribute: string) (html: string) =
    Regex.Matches(html, $"\\s{attribute}=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

/// Every key the page binds: data-text, data-if, data-each, data-key and every data-bind-*.
let private boundKeys (html: string) =
    let bindings =
        Regex.Matches(html, "\\sdata-bind-[a-z-]+=\"([^\"]+)\"") |> Seq.map (fun m -> m.Groups[1].Value) |> Set.ofSeq

    [ "data-text"; "data-if"; "data-each"; "data-key" ]
    |> List.map (fun attribute -> attributeValues attribute html)
    |> Set.unionMany
    |> Set.union bindings

/// The names a view offers: its own keys and the fields of its list items.
let private viewNames (view: View) =
    view
    |> List.collect (fun (name, value) ->
        match value with
        | Value _ -> [ name ]
        | Items items -> name :: (items |> List.collect (List.map fst)))
    |> Set.ofList

let private faultNames = Chrona.Application.Boundary.faultView None |> List.map fst |> Set.ofList

/// A model with a log entry, so the log's item fields are projected.
let private views =
    let saving =
        [ DraftChanged "note"; Save "c1" ]
        |> List.fold (fun model command ->
            match transition model command with
            | Accepted(next, _) | Rejected(next, _) -> next) initialModel

    [ Chrona.Engine.KernelSliceView.project initialModel; Chrona.Engine.KernelSliceView.project saving ]

[<Fact>]
let ``the page binds only what its engine projects and sends only what it handles`` () =
    let html = readRepoFile "web/index.html"
    let offered = views |> List.map viewNames |> Set.unionMany |> Set.union faultNames
    Assert.Empty(Set.difference (boundKeys html) offered)
    // Every event the page can send is one the engine handles, and every
    // event the engine handles is one the page can send.
    Assert.Equal<Set<string>>(Set.ofList eventNames, attributeValues "data-event" html)
