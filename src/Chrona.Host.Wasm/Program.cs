using System.Runtime.InteropServices.JavaScript;

// .NET JSExport is currently generated through Roslyn/C#. This file is a
// mechanical browser boundary only. Chrona state, validation, transitions,
// projection and event interpretation remain in F#.
return;

public partial class ChronaHostWasm
{
    [JSExport]
    internal static string Dispatch(string messageJson) =>
        Chrona.Application.Dispatch.handle(messageJson);
}
