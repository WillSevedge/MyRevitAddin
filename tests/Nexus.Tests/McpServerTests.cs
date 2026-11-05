using System.Text;
using System.Text.Json.Nodes;
using Nexus.Agent;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Mcp;

namespace Nexus.Tests;

public sealed class McpServerTests
{
    /// <summary>A CAD tool: echoes its arguments and the document; "fail" reports a ToolException.</summary>
    private sealed class EchoTool : IAgentTool<FakeDoc>
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = "echo", Title = "Echo", Description = "Says it back.", ReadOnly = true,
            InputSchema = Schema.Of("{\"type\": \"object\", \"properties\": {\"text\": {\"type\": \"string\"}}}"),
        };

        public object? Call(FakeDoc document, ToolArgs args, AgentLog log, CancellationToken ct)
        {
            if (args.String("text") == "fail") throw new ToolException("No layer 'X'.");
            return new ToolOutput
            {
                Data = new { doc = document.Title, text = args.String("text"), has_document_arg = args.Has("document") },
                Summary = "Echoed.",
                Png = new byte[] { 1, 2, 3 },
            };
        }
    }

    private sealed class FixedAgents : IAgentSource
    {
        private readonly AgentRegistration _reg;
        public FixedAgents(AgentRegistration reg) => _reg = reg;
        public IReadOnlyList<AgentRegistration> Discover() => new[] { _reg };
        public Task<AgentClient> ConnectAsync(AgentRegistration agent, CancellationToken ct) => AgentClient.ConnectAsync(agent.PipeName, TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task Assistant_tools_reach_the_program_through_mcp()
    {
        var log = new AgentLog("test");
        using var host = new FakeHost(log);
        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(new FakeSheetsReader());
        var tools = new ToolRegistry<FakeDoc>();
        tools.Register(new EchoTool());
        var info = HostInfoFactory.Create("AutoCAD", "AutoCAD", "2026", "test", log);
        info.HostKind = "AutoCAD";
        using var server = new AgentServer<FakeDoc>(info, host.Queue, new FakeDocs(), readers, log,
            new AgentServerOptions { WriteRegistration = false }, tools: tools);
        server.Start();
        var hello = await (await AgentClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5))).HelloAsync();
        var reg = new AgentRegistration { PipeName = server.PipeName, Host = hello.Host };

        string Request(int id, string method, string parameters = "{}") =>
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parameters}}}";
        string input = string.Join("\n", new[]
        {
            Request(1, "initialize", "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}"),
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            Request(2, "tools/list"),
            Request(3, "tools/call", "{\"name\":\"autocad_echo\",\"arguments\":{\"text\":\"hi\",\"document\":\"Site\"}}"),
            Request(4, "tools/call", "{\"name\":\"autocad_echo\",\"arguments\":{\"text\":\"fail\"}}"),
            Request(5, "tools/call", "{\"name\":\"nexus_status\",\"arguments\":{}}"),
            Request(6, "nope"),
            Request(7, "ping"),
        }) + "\n";
        var output = new MemoryStream();
        var mcp = new McpServer(new FixedAgents(reg), new MemoryStream(Encoding.UTF8.GetBytes(input)), output, cacheFile: null);
        await mcp.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonNode.Parse(l)!.AsObject()).Where(m => m["id"] is not null)
            .ToDictionary(m => m["id"]!.GetValue<int>());

        Assert.Equal("2025-06-18", replies[1]["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.True(replies[1]["result"]!["capabilities"]!["tools"]!["listChanged"]!.GetValue<bool>());

        var listed = replies[2]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("nexus_status", listed);
        Assert.Contains("autocad_echo", listed);
        var echo = replies[2]["result"]!["tools"]!.AsArray().First(t => t!["name"]!.GetValue<string>() == "autocad_echo")!;
        Assert.NotNull(echo["inputSchema"]!["properties"]!["document"]);
        Assert.True(echo["annotations"]!["readOnlyHint"]!.GetValue<bool>());

        var call = replies[3]["result"]!;
        Assert.False(call["isError"]!.GetValue<bool>());
        string text = call["content"]![0]!["text"]!.GetValue<string>();
        Assert.StartsWith("Echoed.", text);
        Assert.Contains("\"doc\":\"Site.rvt\"", text);         // the document argument picked the drawing
        Assert.Contains("\"has_document_arg\":false", text);  // …and was not passed on to the tool
        Assert.Equal("image", call["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), call["content"]![1]!["data"]!.GetValue<string>());

        Assert.True(replies[4]["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("No layer 'X'.", replies[4]["result"]!["content"]![0]!["text"]!.GetValue<string>());

        Assert.Contains("Tower.rvt", replies[5]["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(-32601, replies[6]["error"]!["code"]!.GetValue<int>());
        Assert.NotNull(replies[7]["result"]);
    }

    private sealed class NamedTool : IAgentTool<FakeDoc>
    {
        private readonly string _answer;
        public NamedTool(string name, string answer)
        {
            _answer = answer;
            Descriptor = new ToolDescriptor { Name = name, Title = name, Description = name, ReadOnly = true, InputSchema = Schema.Of("{\"type\":\"object\"}") };
        }
        public ToolDescriptor Descriptor { get; }
        public object? Call(FakeDoc document, ToolArgs args, AgentLog log, CancellationToken ct) => new ToolOutput { Summary = _answer };
    }

    private sealed class ManyAgents : IAgentSource
    {
        private readonly List<AgentRegistration> _regs;
        public ManyAgents(List<AgentRegistration> regs) => _regs = regs;
        public IReadOnlyList<AgentRegistration> Discover() => _regs;
        public Task<AgentClient> ConnectAsync(AgentRegistration agent, CancellationToken ct) => AgentClient.ConnectAsync(agent.PipeName, TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task Civil_3d_tools_go_to_the_civil_3d_session()
    {
        var log = new AgentLog("test");
        using var host = new FakeHost(log);
        var servers = new List<AgentServer<FakeDoc>>();
        var regs = new List<AgentRegistration>();
        foreach (var (product, tool) in new[] { ("AutoCAD", "drawing_info"), ("Civil 3D", "civil3d_overview") })
        {
            var tools = new ToolRegistry<FakeDoc>();
            tools.Register(new NamedTool(tool, "answered by " + product));
            tools.Register(new NamedTool("drawing_info", "answered by " + product));
            var info = HostInfoFactory.Create("AutoCAD", product, "2026", "test", log);
            info.HostKind = "AutoCAD";
            info.ProcessId = 1000 + regs.Count; // two sessions
            var server = new AgentServer<FakeDoc>(info, host.Queue, new FakeDocs(), new ReaderRegistry<FakeDoc>(), log,
                new AgentServerOptions { WriteRegistration = false }, tools: tools);
            server.Start();
            servers.Add(server);
            var hello = await (await AgentClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5))).HelloAsync();
            hello.Host.ProcessId = info.ProcessId;
            regs.Add(new AgentRegistration { PipeName = server.PipeName, Host = hello.Host });
        }
        try
        {
            string input = string.Join("\n", new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{}}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"autocad_civil3d_overview\",\"arguments\":{}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"autocad_plant3d_objects\",\"arguments\":{}}}",
            }) + "\n";
            var output = new MemoryStream();
            var mcp = new McpServer(new ManyAgents(regs), new MemoryStream(Encoding.UTF8.GetBytes(input)), output, cacheFile: null);
            await mcp.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonNode.Parse(l)!.AsObject()).Where(m => m["id"] is not null).ToDictionary(m => m["id"]!.GetValue<int>());

            var names = replies[1]["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("autocad_civil3d_overview", names);
            Assert.Contains("autocad_query_entities", names); // the core tools stay listed
            Assert.Equal("answered by Civil 3D", replies[2]["result"]!["content"]![0]!["text"]!.GetValue<string>());
            Assert.True(replies[3]["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("Plant 3D", replies[3]["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        finally
        {
            foreach (var s in servers) s.Dispose();
        }
    }

    [Fact]
    public void The_cad_tool_catalog_is_complete_and_valid()
    {
        var names = CadToolCatalog.All.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var expected in new[] { "drawing_info", "query_entities", "create_entities", "modify_entities", "delete_entities", "view_image", "run_command" })
            Assert.Contains(expected, names);
        foreach (var t in CadToolCatalog.All)
        {
            Assert.Equal("object", t.InputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.Matches("^[a-z_]+$", t.Name);
        }
        Assert.True(CadToolCatalog.Get("delete_entities").Destructive);
        Assert.True(CadToolCatalog.Get("query_entities").ReadOnly);
    }

    [Fact]
    public void Tool_arguments_are_read_forgivingly()
    {
        var a = new ToolArgs(System.Text.Json.JsonDocument.Parse(
            "{\"p\":[1,2],\"q\":{\"x\":3,\"y\":\"4\"},\"n\":\"2.5\",\"list\":\"one\",\"b\":\"true\",\"pts\":[[0,0],[1,1,1]]}").RootElement);
        Assert.Equal(new[] { 1.0, 2.0, 0.0 }, a.Point("p"));
        Assert.Equal(new[] { 3.0, 4.0, 0.0 }, a.Point("q"));
        Assert.Equal(2.5, a.Number("n"));
        Assert.Equal(new[] { "one" }, a.Strings("list"));
        Assert.True(a.Bool("b", false));
        Assert.Equal(2, a.Points("pts").Count);
        Assert.Throws<ToolException>(() => a.RequiredPoint("missing"));
        Assert.Throws<ToolException>(() => a.Point("n"));
    }
}
