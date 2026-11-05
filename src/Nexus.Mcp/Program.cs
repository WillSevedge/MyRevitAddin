using Nexus.Mcp;

// stdout carries the protocol only; anything for people goes to stderr and the log.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
var server = new McpServer(new RunningAgents(), Console.OpenStandardInput(), Console.OpenStandardOutput());
await server.RunAsync(stop.Token);
return 0;
