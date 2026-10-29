using System.Text.Json.Nodes;
using System.Threading.Channels;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;

namespace WeaveCopilot.Tests;

/// <summary>Builds SDK events from JSON, the same way the SDK parses the CLI's wire format. All content here is invented.</summary>
public static class Ev
{
    public static SessionEvent Make(string type, string data, string? extra = null) =>
        SessionEvent.FromJson($$"""{"type":"{{type}}","id":"{{Guid.NewGuid()}}","timestamp":"2026-01-01T00:00:00Z"{{extra}},"data":{{data}}}""")
        ?? throw new InvalidOperationException("unparsed " + type);

    public static SessionEvent Delta(string id, string text) => Make("assistant.message_delta", $$"""{"messageId":"{{id}}","deltaContent":"{{text}}"}""");
    public static SessionEvent Message(string id, string text, string? fusion = null) => Make("assistant.message", $$"""{"messageId":"{{id}}","content":"{{text}}"{{Fusion(fusion)}}}""");
    public static SessionEvent ToolStart(string id, string tool, string args, string? fusion = null) => Make("tool.execution_start", $$"""{"toolCallId":"{{id}}","toolName":"{{tool}}","toolTitle":"Using {{tool}}","arguments":{{args}}{{Fusion(fusion)}}}""");
    public static SessionEvent ToolDone(string id, bool ok, string text, string? fusion = null) => Make("tool.execution_complete", $$"""{"toolCallId":"{{id}}","success":{{(ok ? "true" : "false")}},"result":{"content":"{{text}}"}{{Fusion(fusion)}}}""");
    public static SessionEvent Idle(bool aborted = false) => Make("session.idle", $$"""{"aborted":{{(aborted ? "true" : "false")}}}""");
    public static SessionEvent Error(string msg) => Make("session.error", $$"""{"errorType":"x","message":"{{msg}}"}""");
    public static SessionEvent Usage(int input, int output, string? fusion = null) => Make("assistant.usage", $$"""{"model":"m","inputTokens":{{input}},"outputTokens":{{output}}{{Fusion(fusion)}}}""");

    private static string Fusion(string? commitId) =>
        commitId is null ? "" : $$""","fusion":{"fusionId":"f1","commitId":"{{commitId}}","pattern":"single","policy":"max","syntheticModel":"hf"}""";
}

/// <summary>In-memory duplex for JSON-RPC: lines in from a fake client, lines out captured.</summary>
public sealed class Wire
{
    private readonly Channel<string> _in = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>();
    private long _id;
    public JsonRpcConnection Rpc { get; }
    public Task Running { get; }
    public List<JsonObject> Notifications { get; } = [];
    public Func<JsonObject, JsonNode?> OnAgentRequest { get; set; } = _ => new JsonObject();

    public Wire()
    {
        Rpc = new JsonRpcConnection(new LineReader(_in.Reader), new LineWriter(_out.Writer));
        Running = Rpc.RunAsync();
    }

    public void SendRaw(string line) => _in.Writer.TryWrite(line);

    public void Send(JsonObject msg) => SendRaw(msg.ToJsonString());

    public void Notify(string method, JsonObject p) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });

    public async Task<JsonObject> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return (JsonObject)JsonNode.Parse(await _out.Reader.ReadAsync(cts.Token))!;
    }

    /// <summary>Sends a request and returns the full response, recording notifications and answering agent->client requests on the way.</summary>
    public async Task<JsonObject> CallAsync(string method, JsonObject p)
    {
        var id = ++_id;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
        while (true)
        {
            var m = await NextAsync();
            if (m["method"] is not null && m["id"] is null) Notifications.Add(m);
            else if (m["method"] is not null) Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = m["id"]!.DeepClone(), ["result"] = OnAgentRequest(m) });
            else if (m["id"]!.GetValue<long>() == id) return m;
        }
    }

    public async Task<JsonNode> ResultAsync(string method, JsonObject p) => (await CallAsync(method, p))["result"] ?? throw new Exception("no result");

    public async Task CloseAsync() { _in.Writer.TryComplete(); await Running; }

    private sealed class LineReader(ChannelReader<string> r) : TextReader
    {
        public override async Task<string?> ReadLineAsync() => await r.WaitToReadAsync() ? await r.ReadAsync() : null;
    }

    private sealed class LineWriter(ChannelWriter<string> w) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteLineAsync(string? value) { w.TryWrite(value ?? ""); return Task.CompletedTask; }
        public override Task FlushAsync() => Task.CompletedTask;
    }
}

public sealed class FakeSession(string id) : ICopilotSession
{
    private Action<SessionEvent>? _handler;
    public string Id { get; } = id;
    public List<(string Prompt, bool Immediate)> Sent { get; } = [];
    public List<ImageData> Images { get; } = [];
    public List<TodoRow> Todos { get; } = [];
    public List<string> Models { get; } = [];
    public bool Aborted { get; private set; }
    public List<SessionEvent> History { get; } = [];
    /// <summary>What the "model" does when a prompt arrives.</summary>
    public Action<FakeSession, string>? OnSend { get; set; }

    public void Raise(params SessionEvent[] events) { foreach (var e in events) _handler?.Invoke(e); }
    public IDisposable Subscribe(Action<SessionEvent> handler) { _handler = handler; return new Noop(); }
    public Task SendAsync(string prompt, bool immediate, IReadOnlyList<ImageData>? images = null) { Sent.Add((prompt, immediate)); Images.AddRange(images ?? []); OnSend?.Invoke(this, prompt); return Task.CompletedTask; }
    public Task<IReadOnlyList<TodoRow>> ReadTodosAsync() => Task.FromResult<IReadOnlyList<TodoRow>>([.. Todos]);
    public Task AbortAsync() { Aborted = true; Raise(Ev.Idle(aborted: true)); return Task.CompletedTask; }
    public Task SetModelAsync(string modelId) { Models.Add(modelId); return Task.CompletedTask; }
    public Task<IReadOnlyList<SessionEvent>> GetEventsAsync() => Task.FromResult<IReadOnlyList<SessionEvent>>(History);
    public List<string> Asked { get; } = [];
    public string? Answer { get; set; } = "an answer";
    public Task<string?> AskAsync(string question) { Asked.Add(question); return Task.FromResult(Answer); }
    public List<(string RequestId, string Command)> Shells { get; } = [];
    public ShellResult ShellOutcome { get; set; } = new("call-1", true, 0, "hi", null);
    public Task<ShellResult> RunShellAsync(string requestId, string command) { Shells.Add((requestId, command)); return Task.FromResult(ShellOutcome); }
    public List<string> Cancelled { get; } = [];
    public bool CancelResult { get; set; } = true;
    public List<(string Id, string? Cursor)> Reads { get; } = [];
    public TaskOutput ReadResult { get; set; } = new("", null, false, null);
    public Task<bool> CancelTaskAsync(string id) { Cancelled.Add(id); return Task.FromResult(CancelResult); }
    public Task<TaskOutput> ReadTaskOutputAsync(string id, string? cursor) { Reads.Add((id, cursor)); return Task.FromResult(ReadResult); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

public sealed class FakeHost : ICopilotHost
{
    public FakeSession Session { get; } = new("s-1");
    public List<ModelEntry> Models { get; } = [new("gpt-5-mini", "GPT-5 mini", "0x"), new("other", "Other", "1x")];
    public bool HydraProbeResult { get; set; } = true;
    public int HydraProbes { get; private set; }
    public string? CreatedModel { get; private set; }
    public int Creates { get; private set; }
    public PermissionCallback? Permission { get; private set; }

    public Task StartAsync() => Task.CompletedTask;
    public Task<string?> VersionAsync() => Task.FromResult<string?>("9.9.9");
    public Task PingAsync() => Task.CompletedTask;
    public AuthStatus Auth { get; set; } = new(true, "user", "https://github.com", "octo-demo", null);
    public Task<AuthStatus> AuthStatusAsync() => Task.FromResult(Auth);
    public Task<IReadOnlyList<ModelEntry>> ListModelsAsync() => Task.FromResult<IReadOnlyList<ModelEntry>>(Models);
    public IReadOnlyList<AcpMcpServer>? Mcp;
    public Task<ICopilotSession> CreateAsync(string cwd, string? model, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null) { Mcp = mcp; Creates++; CreatedModel = model; Permission = permission; return Task.FromResult<ICopilotSession>(Session); }
    public Task<ICopilotSession> ResumeAsync(string sessionId, string cwd, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null) { Mcp = mcp; Permission = permission; return Task.FromResult<ICopilotSession>(Session); }
    public List<(string SessionId, string? ToEventId)> Forks { get; } = [];
    public Task<string> ForkAsync(string sessionId, string? toEventId) { Forks.Add((sessionId, toEventId)); return Task.FromResult("fork-1"); }
    public Task<bool> ProbeHydraFusionAsync(string cwd) { HydraProbes++; return Task.FromResult(HydraProbeResult); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
