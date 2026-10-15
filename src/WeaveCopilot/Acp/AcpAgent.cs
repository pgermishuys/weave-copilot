using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using WeaveCopilot.Sdk;

namespace WeaveCopilot.Acp;

/// <summary>The ACP agent: JSON-RPC methods on one side, an <see cref="ICopilotHost"/> on the other.</summary>
public sealed class AcpAgent(JsonRpcConnection rpc, ICopilotHost host, bool hydraFusion, string version, Action<string> onFatal)
{
    private const string DefaultModel = "gpt-5-mini";
    private const string HydraModel = "hydrafusion";

    private sealed class Sess(ICopilotSession session, string cwd, EventMapper mapper)
    {
        public ICopilotSession Session { get; } = session;
        public string Cwd { get; } = cwd;
        public EventMapper Mapper { get; } = mapper;
        public string? Model { get; set; }
        public TaskCompletionSource<AgentOutput>? Turn;
        public bool CancelRequested { get; set; }
        public IDisposable? Subscription { get; set; }
    }

    private readonly Dictionary<string, Sess> _sessions = [];
    private IReadOnlyList<ModelEntry>? _models;
    private Task<bool>? _hydraProbe;

    public void Attach()
    {
        rpc.OnRequest = HandleAsync;
        rpc.OnNotification = (method, p) =>
        {
            if (method == "session/cancel" && p.Str("sessionId") is { } id && _sessions.TryGetValue(id, out var s)) _ = CancelAsync(s);
        };
    }

    public async Task<JsonNode?> HandleAsync(string method, JsonNode? p)
    {
        try
        {
            return method switch
            {
                "initialize" => await InitializeAsync(),
                "session/new" => await NewAsync(p),
                "session/load" => await LoadAsync(p),
                "session/set_model" => await SetModelAsync(p, p.Str("modelId")),
                "session/set_config_option" when p.Str("configId") == "model" => await SetModelAsync(p, p.Str("value")),
                "session/prompt" => await PromptAsync(p),
                "_fleet/steer" => await SteerAsync(p),
                _ => throw new JsonRpcException(-32601, $"Method not found: {method}"),
            };
        }
        catch (JsonRpcException) { throw; }
        catch (Exception e) when (IsChildDeath(e)) { onFatal("Copilot CLI exited: " + e.Message); throw; }
        catch (Exception e) { throw new JsonRpcException(-32000, Readable(e)); }
    }

    private async Task<JsonNode?> InitializeAsync()
    {
        await host.StartAsync();
        var cli = await host.VersionAsync();
        return new JsonObject
        {
            ["protocolVersion"] = 1,
            ["agentCapabilities"] = new JsonObject
            {
                ["loadSession"] = true,
                ["promptCapabilities"] = new JsonObject { ["image"] = false, ["embeddedContext"] = true },
            },
            ["agentInfo"] = new JsonObject { ["name"] = "weave-copilot", ["version"] = version },
            ["_meta"] = new JsonObject { ["cliVersion"] = cli },
        };
    }

    private async Task<JsonNode?> NewAsync(JsonNode? p)
    {
        var cwd = p.Str("cwd") ?? Directory.GetCurrentDirectory();
        var models = await ModelsAsync(cwd);
        var model = models.Any(m => m.Id == DefaultModel) ? DefaultModel : null;
        var s = await Register(await host.CreateAsync(cwd, model, Permission), cwd, model ?? models.FirstOrDefault()?.Id);
        return new JsonObject { ["sessionId"] = s.Session.Id, ["models"] = ModelsJson(models, s.Model) };
    }

    private async Task<JsonNode?> LoadAsync(JsonNode? p)
    {
        var id = p.Str("sessionId") ?? throw new JsonRpcException(-32602, "sessionId is required");
        var cwd = p.Str("cwd") ?? Directory.GetCurrentDirectory();
        var models = await ModelsAsync(cwd);
        var s = await Register(await host.ResumeAsync(id, cwd, Permission), cwd, null, replay: true);
        return new JsonObject { ["models"] = ModelsJson(models, s.Model) };
    }

    /// <summary>Wires events and the permission callback, then (for load) replays history before returning.</summary>
    private async Task<Sess> Register(ICopilotSession session, string cwd, string? model, bool replay = false)
    {
        var s = new Sess(session, cwd, new EventMapper(cwd)) { Model = model };
        _sessions[session.Id] = s;
        if (replay)
        {
            var past = new EventMapper(cwd, history: true);
            foreach (var ev in await session.GetEventsAsync())
            {
                if (ev is SessionStartEvent { Data.SelectedModel: { } sel }) s.Model = sel;
                foreach (var o in past.Map(ev)) Emit(session.Id, o, s);
            }
        }
        s.Subscription = session.Subscribe(ev => { foreach (var o in s.Mapper.Map(ev)) Emit(session.Id, o, s); });
        return s;
    }

    /// <summary>Asks the client (`session/request_permission`) and maps its answer to an SDK decision; unclear or failed asks deny.</summary>
    private async Task<PermissionDecision> Permission(string sessionId, PermissionRequest req)
    {
        try
        {
            var cwd = _sessions.TryGetValue(sessionId, out var s) ? s.Cwd : null;
            var res = await rpc.SendRequestAsync("session/request_permission", PermissionMapper.Params(sessionId, req, cwd));
            return PermissionMapper.ToDecision(req, res?["outcome"]);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return PermissionMapper.ToDecision(req, null); }
    }

    private void Emit(string sessionId, AgentOutput o, Sess s)
    {
        switch (o)
        {
            case Update u: rpc.SendNotification("session/update", new JsonObject { ["sessionId"] = sessionId, ["update"] = u.Body }); break;
            case Fusion f: f.Params["sessionId"] = sessionId; rpc.SendNotification("_fleet/fusion", f.Params); break;
            case Idle or Failure: s.Turn?.TrySetResult(o); break;
        }
    }

    private async Task<JsonNode?> PromptAsync(JsonNode? p)
    {
        var s = Find(p);
        var text = PromptText(p?["prompt"] as JsonArray);
        var turn = new TaskCompletionSource<AgentOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref s.Turn, turn, null) is not null) throw new JsonRpcException(-32000, "A prompt is already running for this session");
        s.CancelRequested = false;
        using var stop = new CancellationTokenSource();
        try
        {
            await s.Session.SendAsync(text, immediate: false);
            _ = Task.Run(() => HeartbeatAsync(turn, stop.Token));
            var end = await turn.Task;
            if (end is Failure f) throw new JsonRpcException(-32000, f.Message);
            var idle = (Idle)end;
            var result = new JsonObject { ["stopReason"] = idle.Aborted || s.CancelRequested ? "cancelled" : "end_turn" };
            if (idle.Usage is not null) result["usage"] = idle.Usage;
            return result;
        }
        finally { stop.Cancel(); s.Turn = null; }
    }

    /// <summary>While a prompt runs, notice a dead CLI child (no idle event will ever come).</summary>
    private async Task HeartbeatAsync(TaskCompletionSource<AgentOutput> turn, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await host.PingAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            turn.TrySetResult(new Failure("Copilot CLI is not responding: " + e.Message));
            onFatal("Copilot CLI exited: " + e.Message);
        }
    }

    private async Task CancelAsync(Sess s)
    {
        s.CancelRequested = true;
        try { await s.Session.AbortAsync(); } catch (Exception e) { s.Turn?.TrySetResult(new Failure("Cancel failed: " + e.Message)); }
    }

    private async Task<JsonNode?> SteerAsync(JsonNode? p)
    {
        var s = Find(p);
        await s.Session.SendAsync(p.Str("prompt") ?? PromptText(p?["prompt"] as JsonArray), immediate: true);
        return new JsonObject();
    }

    private async Task<JsonNode?> SetModelAsync(JsonNode? p, string? model)
    {
        var s = Find(p);
        if (string.IsNullOrEmpty(model)) throw new JsonRpcException(-32602, "modelId is required");
        await s.Session.SetModelAsync(model);
        s.Model = model;
        return new JsonObject();
    }

    private Sess Find(JsonNode? p) =>
        p.Str("sessionId") is { } id && _sessions.TryGetValue(id, out var s) ? s : throw new JsonRpcException(-32602, "Unknown sessionId");

    private async Task<IReadOnlyList<ModelEntry>> ModelsAsync(string cwd)
    {
        _models ??= await host.ListModelsAsync();
        if (hydraFusion && _models.All(m => m.Id != HydraModel) && await (_hydraProbe ??= host.ProbeHydraFusionAsync(cwd)))
            _models = [.. _models, new ModelEntry(HydraModel, "HydraFusion", null)];
        return _models;
    }

    private static JsonObject ModelsJson(IReadOnlyList<ModelEntry> models, string? current) => new()
    {
        ["currentModelId"] = current,
        ["availableModels"] = new JsonArray(models.Select(m =>
        {
            var o = new JsonObject { ["modelId"] = m.Id, ["name"] = m.Name };
            if (m.Usage is not null) o["_meta"] = new JsonObject { ["copilotUsage"] = m.Usage };
            return (JsonNode)o;
        }).ToArray()),
    };

    private static string PromptText(JsonArray? blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks ?? [])
        {
            var piece = b.Str("type") switch
            {
                "text" => b.Str("text"),
                "resource" when b?["resource"] is { } r => $"<resource uri=\"{r.Str("uri")}\">\n{r.Str("text")}\n</resource>",
                "resource_link" => $"[{b.Str("name")}]({b.Str("uri")})",
                _ => null,
            };
            if (piece is null) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(piece);
        }
        return sb.ToString();
    }

    private static bool IsChildDeath(Exception e) => e is IOException && e.Message.Contains("exited unexpectedly", StringComparison.OrdinalIgnoreCase);

    private static string Readable(Exception e)
    {
        var m = e.Message.Split('\n')[0].Trim();
        return m.Contains("auth", StringComparison.OrdinalIgnoreCase) || m.Contains("sign in", StringComparison.OrdinalIgnoreCase) || m.Contains("logged in", StringComparison.OrdinalIgnoreCase)
            ? m + " (not signed in? run `copilot login` with the same COPILOT_HOME)"
            : m;
    }
}
