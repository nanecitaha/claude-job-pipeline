using JobPipeline.Core;
using JobPipeline.Mcp;

// Usage: JobPipeline.Mcp.exe [--data <folder>]
// Data folder precedence: --data, then JOBPIPELINE_DATA, then %LOCALAPPDATA%\JobPipeline.
// stdout carries the MCP protocol only; diagnostics go to stderr.

var dataDir = JobStore.DefaultDataDir();
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] is "--data" or "-d") dataDir = args[i + 1];

var store = new JobStore(dataDir);
Configs.EnsureDefaults(store.DataDir);
Console.Error.WriteLine($"[job-pipeline] data folder: {store.DataDir}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var server = new McpServer(Tools.Create(store), Tools.Instructions);
await server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cts.Token);
