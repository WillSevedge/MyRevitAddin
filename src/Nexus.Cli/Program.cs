using System.Text.Json;
using Nexus.Contracts;
using Nexus.Hub.Core;

const string Usage = """
    nexus agents
        List running agents (hosts).
    nexus docs <pid>
        List open documents in the host with that process id.
    nexus readers <pid>
        List the readers an agent offers.
    nexus read <pid> <readerId> [--doc <documentId>|active] [--opt name=value]... [--json out.json] [--csv out.csv] [--long out.csv]
        Run a reader and print a summary (or write the results to files).
    """;

try
{
    return await RunAsync(args);
}
catch (AgentRequestException ex)
{
    Console.Error.WriteLine($"Agent error {ex.Code}: {ex.Message}");
    if (ex.Error.Detail is not null) Console.Error.WriteLine(ex.Error.Detail);
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0) { Console.WriteLine(Usage); return 1; }

    switch (args[0].ToLowerInvariant())
    {
        case "agents":
            var agents = AgentDiscovery.Discover();
            if (agents.Count == 0) Console.WriteLine("No agents found. Is Revit/AutoCAD running with the Nexus add-in loaded?");
            foreach (var a in agents)
                Console.WriteLine($"{a.Host.ProcessId,7}  {a.Host.Product} {a.Host.Version}  modules=[{string.Join(",", a.Host.Modules)}]  {a.PipeName}");
            return 0;

        case "docs":
        {
            await using var c = await ConnectAsync(args);
            foreach (var d in await c.ListDocumentsAsync())
                Console.WriteLine($"{d.Id}  {(d.IsActive ? "*" : " ")} {d.Title}  {d.Path}  {string.Join(" ", d.Extra.Select(kv => kv.Key + "=" + kv.Value))}");
            return 0;
        }

        case "readers":
        {
            await using var c = await ConnectAsync(args);
            foreach (var r in await c.ListReadersAsync())
            {
                Console.WriteLine($"{r.Id,-22} {r.DisplayName}{(r.IsImplemented ? "" : "  [placeholder]")}");
                foreach (var o in r.Options)
                    Console.WriteLine($"{"",24}--opt {o.Name}=<{o.Type}> (default {o.Default})  {o.DisplayName}");
            }
            return 0;
        }

        case "read":
        {
            if (args.Length < 3) { Console.WriteLine(Usage); return 1; }
            await using var c = await ConnectAsync(args);
            var request = new ReadRequest { ReaderId = args[2], DocumentId = Arg(args, "--doc") ?? "active" };
            for (int i = 3; i < args.Length - 1; i++)
            {
                if (args[i] != "--opt") continue;
                var kv = args[i + 1].Split('=', 2);
                if (kv.Length == 2) request.Options[kv[0]] = kv[1];
            }

            var hello = await c.HelloAsync();
            var result = await c.ReadAsync(request);
            var source = new ResultSource { Host = hello.Host, DocumentTitle = result.DocumentTitle, Result = result };
            var table = ResultTable.Build(new[] { source });

            Console.WriteLine($"{result.ReaderId} on '{result.DocumentTitle}': {result.Items.Count} top-level items, {table.Rows.Count} rows, {table.Columns.Count} columns, {result.ElapsedMs} ms{(result.Truncated ? " (truncated)" : "")}");
            foreach (var w in result.Warnings) Console.WriteLine("  warning: " + w);
            foreach (var row in table.Rows.Take(25))
                Console.WriteLine($"  {new string(' ', row.Depth * 2)}{row.ItemType}: {row.Item}  ({row.Values.Count} properties)");
            if (table.Rows.Count > 25) Console.WriteLine($"  ... {table.Rows.Count - 25} more");

            if (Arg(args, "--json") is { } json) { Exporters.WriteJson(new[] { source }, json); Console.WriteLine("Wrote " + json); }
            if (Arg(args, "--csv") is { } csv) { Exporters.WriteWideCsv(table, null, csv); Console.WriteLine("Wrote " + csv); }
            if (Arg(args, "--long") is { } lng) { Exporters.WriteLongCsv(table, null, lng); Console.WriteLine("Wrote " + lng); }
            return 0;
        }

        default:
            Console.WriteLine(Usage);
            return 1;
    }
}

static async Task<AgentClient> ConnectAsync(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[1], out int pid))
        throw new ArgumentException("Give the host process id (see 'nexus agents').");
    var reg = AgentDiscovery.Discover().FirstOrDefault(a => a.Host.ProcessId == pid)
              ?? throw new ArgumentException($"No agent with pid {pid}.");
    var client = await AgentClient.ConnectAsync(reg.PipeName);
    var hello = await client.HelloAsync();
    if (hello.ProtocolVersion != Protocol.Version)
        Console.Error.WriteLine($"Warning: agent protocol {hello.ProtocolVersion}, hub protocol {Protocol.Version}.");
    return client;
}

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
