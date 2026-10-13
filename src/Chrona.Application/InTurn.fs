/// Operations run together inside the bridge's request/reply loop (WI-0077).
///
/// The browser runtime has one thread, and the bridge resumes an operation
/// when the kernel answers its call (`Bridge`). `Async.Parallel` and
/// `Async.Sequential` hand work to the thread pool, which leaves the bridge's
/// turn; here every operation is started on the calling thread and resumed
/// by the bridge, so several browser calls can be in flight at once while
/// nothing leaves the loop.
module Chrona.Application.InTurn

/// How many operations one step keeps in flight: what a browser sends to
/// one host at a time over HTTP/1.1, and a polite load on GitHub's API.
[<Literal>]
let Limit = 6

/// Runs these operations together, at most `limit` at a time, and gives their
/// results in the order they were given. The first to fail fails the whole;
/// none is started after that, and those already running finish unheard.
/// The whole resumes through `defer`, which runs it back at the top of the
/// turn (`Bridge.Defer`): resumed from inside an operation's continuation,
/// F#'s trampoline would move it to the thread pool.
let all (defer: (unit -> unit) -> unit) (limit: int) (operations: Async<'a> list) : Async<'a list> =
    match operations with
    | [] -> async.Return []
    | _ ->
        Async.FromContinuations(fun (succeed, fail, cancel) ->
            let pending = Array.ofList operations
            let results: 'a array = Array.zeroCreate pending.Length
            let mutable started = 0
            let mutable running = 0
            let mutable finished = 0
            let mutable stopped = false
            // An operation that finishes at once resumes inside the loop
            // below; it only frees its place, and the loop starts the next.
            let mutable pumping = false

            let rec pump () =
                if not pumping then
                    pumping <- true

                    while not stopped && running < max 1 limit && started < pending.Length do
                        let index = started
                        started <- started + 1
                        running <- running + 1

                        Async.StartWithContinuations(
                            pending[index],
                            (fun value ->
                                if not stopped then
                                    results[index] <- value
                                    running <- running - 1
                                    finished <- finished + 1

                                    if finished = pending.Length then
                                        stopped <- true
                                        let all = List.ofArray results
                                        defer (fun () -> succeed all)
                                    else
                                        pump ()),
                            (fun error ->
                                if not stopped then
                                    stopped <- true
                                    defer (fun () -> fail error)),
                            (fun cancelled ->
                                if not stopped then
                                    stopped <- true
                                    defer (fun () -> cancel cancelled))
                        )

                    pumping <- false

            pump ())

/// Runs these operations together in this bridge's turn, at most `Limit` at
/// a time, in order.
let together (bridge: Bridge.Bridge) (operations: Async<'a> list) = all bridge.Defer Limit operations
