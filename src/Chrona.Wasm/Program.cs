using System.Runtime.InteropServices.JavaScript;

// Entry point required by the WebAssembly SDK. The module is driven entirely
// through the [JSExport] method below; nothing runs here.
return;

// Limen kernel side: owns WASM and browser interop.
//
// A pure marshalling shim. It forwards one Limen message, as JSON, to the
// engine in Chrona.Application and returns the engine's reply. It must
// never contain a decision: every rule lives in F#.
public partial class ChronaWasm
{
    /// <summary>One message for the slice page (web/).</summary>
    [JSExport]
    internal static string Dispatch(string messageJson) =>
        Chrona.Application.Runtime.dispatch(messageJson);
}
