using TsqlDbg.Adapter;
using TsqlDbg.Core.Tracing;

// DESIGN §19: "--trace <file> ... implement in M0."
// NOTE: stdout is reserved exclusively for the DAP wire protocol from this point on —
// never Console.WriteLine anywhere in this process.
string? tracePath = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--trace" && i + 1 < args.Length)
    {
        tracePath = args[i + 1];
    }
}

// DESIGN §4/§16 (A41): metadata CLI (--list-databases / --test-connection) runs BEFORE any
// DAP wiring — connect, one query, JSON to stdout, exit. stdout is not yet the DAP wire here.
var metadataMode = AdapterMetadataCli.DetectMode(args);
if (metadataMode is not null)
{
    return await AdapterMetadataCli.RunAsync(metadataMode, args).ConfigureAwait(false);
}

ITraceSink trace = tracePath is not null ? new FileTraceSink(tracePath) : NullTraceSink.Instance;

var session = new TsqlDbgDebugSession(trace);
session.InitializeStreams(Console.OpenStandardInput(), Console.OpenStandardOutput());
session.Protocol.Run();

await session.Completion.ConfigureAwait(false);

session.Protocol.Stop();
(trace as IDisposable)?.Dispose();
return 0;
