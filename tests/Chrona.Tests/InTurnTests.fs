/// Operations run together inside the bridge's loop (WI-0077).
module Chrona.Tests.InTurnTests

open System
open Xunit
open Chrona.Application
open Chrona.Application.Bridge

/// Runs an operation started on this thread and returns what it finished
/// with, when it finished without waiting on anything.
let private now (operation: Async<'a>) =
    let mutable result = None
    Async.StartWithContinuations(operation, (fun value -> result <- Some(Ok value)), (fun error -> result <- Some(Error error)), ignore)
    result

[<Fact>]
let ``nothing to run gives nothing, at once`` () =
    Assert.Equal(Some(Ok []), now (InTurn.all (fun resume -> resume ()) InTurn.Limit ([]: Async<int> list)))

[<Fact>]
let ``operations that finish at once give their results in the order given, however many there are`` () =
    let many = [ 1..20000 ]
    Assert.Equal(Some(Ok many), now (InTurn.all (fun resume -> resume ()) InTurn.Limit (many |> List.map async.Return)))

[<Fact>]
let ``browser calls are in flight together, at most six, and the results keep their order whatever order the answers come in`` () =
    let bridge = Bridge()
    let asks = [ 1..10 ] |> List.map (fun n -> async { let! _ = bridge.Call(Sleep n) in return n * 10 })
    let mutable finished = None
    bridge.Start(async {
        let! results = InTurn.together bridge asks
        finished <- Some results
        return []
    })

    let drain () =
        match bridge.Drain() with
        | Ok(calls, _) -> calls
        | Error error -> raise error

    // Six go out at once; the rest wait for a place.
    let first = drain ()
    Assert.Equal(6, first.Length)
    // Answered last first: each answer frees a place for the next waiting call.
    for id, _ in List.rev first do
        bridge.Answer id Done |> ignore

    let second = drain ()
    Assert.Equal(4, second.Length)
    Assert.Equal(None, finished)

    for id, _ in second do
        bridge.Answer id Done |> ignore

    // Resumed on this thread, inside the bridge's turn, never the thread pool.
    Assert.Equal(Some [ 10..10..100 ], finished)
    Assert.Empty(drain ())

[<Fact>]
let ``operations that wait on the browser and others that finish at once resume in the same turn, one after another`` () =
    let bridge = Bridge()
    let mutable finished = None
    bridge.Start(async {
        let! first = InTurn.together bridge [ async { let! _ = bridge.Call(Sleep 1) in return 1 }; async.Return 2 ]
        let! second = InTurn.together bridge ([ 1..1000 ] |> List.map async.Return)
        finished <- Some(first, second.Length, Threading.Thread.CurrentThread.ManagedThreadId)
        return []
    })

    match bridge.Drain() with
    | Ok([ id, _ ], _) -> bridge.Answer id Done |> ignore
    | other -> failwith $"%A{other}"

    Assert.Equal(Some([ 1; 2 ], 1000, Threading.Thread.CurrentThread.ManagedThreadId), finished)

[<Fact>]
let ``the first failure fails the whole, and nothing more is started after it`` () =
    let mutable started = 0

    let operation n =
        async {
            started <- started + 1

            if n = 2 then
                return raise (InvalidOperationException "two")
            else
                return n
        }

    match now (InTurn.all (fun resume -> resume ()) 1 ([ 1..5 ] |> List.map operation)) with
    | Some(Error error) -> Assert.Equal("two", error.Message)
    | other -> failwith $"%A{other}"

    Assert.Equal(2, started)

[<Fact>]
let ``a limit below one still runs one at a time`` () =
    Assert.Equal(Some(Ok [ 1; 2; 3 ]), now (InTurn.all (fun resume -> resume ()) 0 ([ 1; 2; 3 ] |> List.map async.Return)))
