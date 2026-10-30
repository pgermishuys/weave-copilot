using System.Text;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using WeaveCopilot.Sdk;

namespace WeaveCopilot.Acp;

/// <summary>The ACP agent: JSON-RPC methods on one side, an <see cref="ICopilotHost"/> on the other.</summary>
public sealed class AcpAgent(JsonRpcConnection rpc, ICopilotHost host, bool hydraFusion, string version, Action<string> onFatal, ICopilotLogin? login = null, ICopilotUsage? usage = null)
{
    /// <summary>Copilot's own router. Passed to the CLI, so what a new session shows is what it runs on (with no model the CLI picks its startup default).</summary>
    private const string DefaultModel = "auto";
    private const string HydraModel = "hydrafusion";

    private sealed class Sess(ICopilotSession session, string cwd, EventMapper mapper)
    {
        public ICopilotSession Session { get; } = session;
        public string Cwd { get; } = cwd;
        public EventMapper Mapper { get; } = mapper;
        public string? Model { get; set; }
        public TaskCompletionSource<AgentOutput>? Turn;
        public bool CancelRequested { get; set; }
        public string? LastTodos { get; set; }
        public IDisposable? Subscription { get; set; }
        /// <summary>Id of the UserMessageEvent that started the running prompt: where a fork made mid-turn is cut.</summary>
        public string? TurnUserEventId { get; set; }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Sess> _sessions = [];
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
                "_fleet/models" => await ModelsRequestAsync(p),
                "session/new" => await NewAsync(p),
                "session/load" => await LoadAsync(p),
                "session/set_model" => await SetModelAsync(p, p.Str("modelId")),
                "session/set_config_option" when p.Str("configId") == "model" => await SetModelAsync(p, p.Str("value")),
                "session/set_config_option" when p.Str("configId") == "agent" => await SetAgentAsync(p),
                "_fleet/commands" => await CommandsAsync(p),
                "_fleet/agents" => await AgentsAsync(p),
                "session/prompt" => await PromptAsync(p),
                "_fleet/steer" => await SteerAsync(p),
                "_fleet/compact" => await CompactAsync(p),
                "_fleet/usage" => await UsageAsync(),
                "_fleet/auth/status" => await AuthStatusAsync(),
                "_fleet/auth/login" => await LoginAsync(p),
                "_fleet/auth/attempt" => AttemptStatus(p),
                "_fleet/auth/cancel" => CancelLogin(p),
                "_fleet/auth/logout" => await LogoutAsync(),
                "_fleet/todos" => await TodosAsync(p),
                "_fleet/shell" => await ShellAsync(p),
                "_fleet/ask" => await AskAsync(p),
                "_fleet/fork" => await ForkAsync(p),
                "_fleet/work/stop" => await StopWorkAsync(p),
                "_fleet/work/output" => await WorkOutputAsync(p),
                _ => throw new JsonRpcException(-32601, $"Method not found: {method}"),
            };
        }
        catch (JsonRpcException) { throw; }
        catch (Exception e) when (IsChildDeath(e)) { onFatal("Copilot CLI exited: " + e.Message); throw; }
        catch (Exception e) { throw new JsonRpcException(-32000, Readable(e)); }
    }

    private async Task<JsonNode?> AuthStatusAsync()
    {
        var s = await host.AuthStatusAsync();
        // Who is signed in and how, never the token itself.
        return new JsonObject { ["authenticated"] = s.Authenticated, ["authType"] = s.AuthType, ["host"] = s.Host, ["login"] = s.Login, ["message"] = s.Message };
    }

    /// <summary>Plan and quotas for the signed-in user. The token is read and used inside <see cref="ICopilotUsage"/> and never comes back.</summary>
    private async Task<JsonNode?> UsageAsync()
    {
        var s = await host.AuthStatusAsync();
        var none = new JsonObject { ["authenticated"] = false, ["login"] = null, ["plan"] = null, ["resetAt"] = null, ["quotas"] = new JsonArray() };
        if (!s.Authenticated) return none;
        if (usage is null) throw new JsonRpcException(-32601, "Reading usage is not available here");
        CopilotUsageResult? u;
        try { u = await usage.GetAsync(s.Login, s.Host, CancellationToken.None); }
        catch (UsageException e) { throw new JsonRpcException(-32000, e.Message); }
        if (u is null) throw new JsonRpcException(-32000, "Copilot is signed in, but its token isn't somewhere Fleet can read it (it may be in the system keychain); set COPILOT_GITHUB_TOKEN or sign in again.");
        var quotas = new JsonArray();
        foreach (var q in u.Quotas)
            quotas.Add(new JsonObject { ["id"] = q.Id, ["entitlement"] = q.Entitlement, ["remaining"] = q.Remaining, ["percentRemaining"] = q.PercentRemaining, ["unlimited"] = q.Unlimited,
                ["creditsUsed"] = q.CreditsUsed, ["hasQuota"] = q.HasQuota, ["tokenBasedBilling"] = q.TokenBasedBilling });
        return new JsonObject { ["authenticated"] = true, ["login"] = u.Login ?? s.Login, ["plan"] = u.Plan, ["resetAt"] = u.ResetAt, ["quotas"] = quotas };
    }

    private ICopilotLogin Login => login ?? throw new JsonRpcException(-32601, "Signing in is not available here");

    /// <summary>Starts the CLI's device-code sign-in; the user finishes it in a browser, and <c>_fleet/auth/attempt</c> says when it has.</summary>
    private async Task<JsonNode?> LoginAsync(JsonNode? p)
    {
        var a = await Login.StartAsync(p.Str("host"), CancellationToken.None);
        return new JsonObject { ["attemptId"] = a.Id, ["url"] = a.Url, ["code"] = a.Code, ["expiresAt"] = a.ExpiresAt.ToString("O") };
    }

    private JsonNode? AttemptStatus(JsonNode? p)
    {
        var g = Login.Get(p.Str("attemptId") ?? "");
        return new JsonObject { ["state"] = g.State, ["message"] = g.Message };
    }

    private JsonNode? CancelLogin(JsonNode? p)
    {
        Login.Cancel(p.Str("attemptId") ?? "");
        return new JsonObject();
    }

    /// <summary>Removes the sign-in the CLI keeps in its own config; a sign-in from an environment variable or the gh CLI isn't ours to remove.</summary>
    private async Task<JsonNode?> LogoutAsync()
    {
        var s = await host.AuthStatusAsync();
        if (s.Authenticated && s.AuthType is "env" or "gh-cli")
            throw new JsonRpcException(-32000, s.AuthType == "env"
                ? "Copilot is signed in through an environment variable (COPILOT_GITHUB_TOKEN, GH_TOKEN or GITHUB_TOKEN); unset it to sign out."
                : "Copilot is using the GitHub CLI's sign-in; run `gh auth logout` to sign out.");
        return new JsonObject { ["removed"] = Login.SignOut(s.Login) };
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
                ["promptCapabilities"] = new JsonObject { ["image"] = true, ["embeddedContext"] = true },
            },
            ["agentInfo"] = new JsonObject { ["name"] = "weave-copilot", ["version"] = version },
            ["_meta"] = new JsonObject { ["cliVersion"] = cli },
        };
    }

    private async Task<JsonNode?> NewAsync(JsonNode? p)
    {
        var cwd = p.Str("cwd") ?? Directory.GetCurrentDirectory();
        var models = await ModelsAsync(cwd);
        var model = DefaultFor(models);
        var s = await Register(await host.CreateAsync(cwd, model, Permission, AcpMcpServer.Parse(p?["mcpServers"]), SkillDirectories(p)), cwd, model);
        return new JsonObject { ["sessionId"] = s.Session.Id, ["models"] = ModelsJson(models, s.Model) };
    }

    /// <summary>Auto when the account lists it, else the first model listed; null only when there are none.</summary>
    private static string? DefaultFor(IReadOnlyList<ModelEntry> models) => models.Any(m => m.Id == DefaultModel) ? DefaultModel : models.FirstOrDefault()?.Id;

    private static List<string> SkillDirectories(JsonNode? p) => Strings(p?["_meta"]?["skillDirectories"]);

    private static List<string> Strings(JsonNode? array) =>
        (array as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var t) ? t : null).OfType<string>().Where(t => t.Length > 0).ToList() ?? [];

    /// <summary>Every slash command the session knows; the client filters.</summary>
    private async Task<JsonNode?> CommandsAsync(JsonNode? p) => new JsonObject
    {
        ["commands"] = new JsonArray((await Find(p).Session.ListCommandsAsync()).Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["description"] = c.Description, ["hint"] = c.Hint, ["kind"] = c.Kind }).ToArray()),
    };

    /// <summary>Custom agents for a folder, without a session.</summary>
    private async Task<JsonNode?> AgentsAsync(JsonNode? p) => new JsonObject
    {
        ["agents"] = new JsonArray((await host.ListAgentsAsync(p.Str("cwd") ?? Directory.GetCurrentDirectory())).Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["displayName"] = a.DisplayName, ["description"] = a.Description, ["source"] = a.Source }).ToArray()),
    };

    /// <summary>A name selects that custom agent for later turns; empty or null goes back to Copilot's own.</summary>
    private async Task<JsonNode?> SetAgentAsync(JsonNode? p)
    {
        await Find(p).Session.SelectAgentAsync(p.Str("value"));
        return new JsonObject();
    }

    /// <summary>Model catalog without creating a Copilot session, so nothing piles up in the user's session list.</summary>
    private async Task<JsonNode?> ModelsRequestAsync(JsonNode? p)
    {
        var models = await ModelsAsync(p.Str("cwd") ?? Directory.GetCurrentDirectory());
        return new JsonObject
        {
            ["models"] = ModelList(models),
            ["currentModelId"] = DefaultFor(models),
        };
    }

    private async Task<JsonNode?> LoadAsync(JsonNode? p)
    {
        var id = p.Str("sessionId") ?? throw new JsonRpcException(-32602, "sessionId is required");
        var cwd = p.Str("cwd") ?? Directory.GetCurrentDirectory();
        var models = await ModelsAsync(cwd);
        var s = await Register(await host.ResumeAsync(id, cwd, Permission, AcpMcpServer.Parse(p?["mcpServers"]), SkillDirectories(p)), cwd, null, replay: true);
        await PublishTodosAsync(id, s);
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
        s.Subscription = session.Subscribe(ev =>
        {
            if (ev is UserMessageEvent um && s.Turn is not null && s.TurnUserEventId is null) s.TurnUserEventId = um.Id.ToString();
            foreach (var o in s.Mapper.Map(ev)) Emit(session.Id, o, s);
        });
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
            case ShellCommand c: c.Params["sessionId"] = sessionId; rpc.SendNotification("_fleet/shell_command", c.Params); break;
            case Work w: w.Params["sessionId"] = sessionId; rpc.SendNotification("_fleet/work", w.Params); break;
            case Compaction c: c.Params["sessionId"] = sessionId; rpc.SendNotification("_fleet/compaction", c.Params); break;
            case TodosMaybeChanged: _ = PublishTodosAsync(sessionId, s); break;
            case Idle or Failure: s.Turn?.TrySetResult(o); break;
        }
    }

    /// <summary>Reads Copilot's todo list and tells the client as an ACP `plan` update, when it differs from the last one sent.</summary>
    private async Task PublishTodosAsync(string sessionId, Sess s)
    {
        try
        {
            var entries = await ReadEntriesAsync(s);
            var key = entries.ToJsonString();
            if (key == (s.LastTodos ?? "[]")) return;
            s.LastTodos = key;
            rpc.SendNotification("session/update", new JsonObject { ["sessionId"] = sessionId, ["update"] = new JsonObject { ["sessionUpdate"] = "plan", ["entries"] = entries } });
        }
        catch (Exception e) when (e is not OperationCanceledException) { /* a list that can't be read stays as it was */ }
    }

    private static async Task<JsonArray> ReadEntriesAsync(Sess s) => new(
        (await s.Session.ReadTodosAsync()).Select(t => (JsonNode)new JsonObject
        {
            ["content"] = t.Title,
            ["priority"] = "medium",
            ["status"] = t.Status switch { "done" or "completed" => "completed", "in_progress" => "in_progress", _ => "pending" },
        }).ToArray());

    private async Task<JsonNode?> TodosAsync(JsonNode? p) => new JsonObject { ["entries"] = await ReadEntriesAsync(Find(p)) };

    private async Task<JsonNode?> PromptAsync(JsonNode? p)
    {
        var s = Find(p);
        var text = PromptText(p?["prompt"] as JsonArray);
        var images = PromptImages(p?["prompt"] as JsonArray);
        if (text.Length == 0 && images.Count > 0) text = "(see the attached image)";
        var notes = Strings(p?["_meta"]?["modelNotes"]);
        var turn = new TaskCompletionSource<AgentOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref s.Turn, turn, null) is not null) throw new JsonRpcException(-32000, "A prompt is already running for this session");
        s.CancelRequested = false;
        using var stop = new CancellationTokenSource();
        try
        {
            var parts = new List<string>(notes);
            string? display = null;
            if (text.StartsWith('/') && await SlashAsync(s, text) is { } slash)
            {
                if (slash.Kind != "prompt") return Reply(s, slash);
                (parts, display) = ([.. parts, slash.Text ?? text], text);
            }
            else
            {
                parts.AddRange(await SkillPromptsAsync(s, Strings(p?["_meta"]?["skills"])));
                parts.Add(text);
                if (parts.Count > 1) display = text;
            }
            await s.Session.SendAsync(string.Join("\n\n", parts), immediate: false, images, displayPrompt: display);
            _ = Task.Run(() => HeartbeatAsync(turn, stop.Token));
            var end = await turn.Task;
            if (end is Failure f) throw new JsonRpcException(-32000, f.Message);
            var idle = (Idle)end;
            var result = new JsonObject { ["stopReason"] = idle.Aborted || s.CancelRequested ? "cancelled" : "end_turn" };
            if (idle.Usage is not null) result["usage"] = idle.Usage;
            return result;
        }
        finally { stop.Cancel(); s.Turn = null; s.TurnUserEventId = null; }
    }

    /// <summary>Expands a typed <c>/name rest</c> when <c>name</c> is one of the session's commands; null for plain text.</summary>
    private static async Task<CommandResult?> SlashAsync(Sess s, string text)
    {
        var end = text.IndexOfAny([' ', '\t', '\n', '\r']);
        var name = end < 0 ? text[1..] : text[1..end];
        if (!(await s.Session.ListCommandsAsync()).Any(c => c.Name == name)) return null;
        var rest = end < 0 ? "" : text[end..].Trim();
        var r = await s.Session.InvokeCommandAsync(name, rest.Length > 0 ? rest : null);
        return r.Kind == "unsupported" ? throw new JsonRpcException(-32000, $"/{name} needs Copilot's own terminal; Fleet can't show it.") : r;
    }

    /// <summary>A command that answered with text instead of a turn: show it as one message and end.</summary>
    private JsonNode Reply(Sess s, CommandResult r)
    {
        if (!string.IsNullOrEmpty(r.Text))
            Emit(s.Session.Id, new Update(new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = r.Text } }), s);
        return new JsonObject { ["stopReason"] = "end_turn" };
    }

    /// <summary>The prompts of the named skills, in order; names that aren't skill commands are skipped.</summary>
    private static async Task<List<string>> SkillPromptsAsync(Sess s, List<string> names)
    {
        var prompts = new List<string>();
        if (names.Count == 0) return prompts;
        var skills = (await s.Session.ListCommandsAsync()).Where(c => c.Kind == "skill").Select(c => c.Name).ToHashSet();
        foreach (var n in names.Where(skills.Contains))
            if ((await s.Session.InvokeCommandAsync(n, null)) is { Kind: "prompt", Text: { } t }) prompts.Add(t);
        return prompts;
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

    /// <summary>Only valid while a `session/prompt` is in flight; otherwise nobody would wait for the turn it starts. -32001 tells the caller to send it as a normal prompt.</summary>
    private async Task<JsonNode?> SteerAsync(JsonNode? p)
    {
        var s = Find(p);
        if (s.Turn is null) throw new JsonRpcException(-32001, "No turn is running; send the text as a prompt instead");
        await s.Session.SendAsync(p.Str("prompt") ?? PromptText(p?["prompt"] as JsonArray), immediate: true);
        return new JsonObject();
    }

    /// <summary>A `!` command. Refused while a prompt runs (the CLI would interleave it with the turn); the output reaches the model on the next turn.</summary>
    private async Task<JsonNode?> ShellAsync(JsonNode? p)
    {
        var s = Find(p);
        if (s.Turn is not null) throw new JsonRpcException(-32001, "A turn is running; run the command when it's over");
        var command = p.Str("command") ?? throw new JsonRpcException(-32602, "command is required");
        var r = await s.Session.RunShellAsync(p.Str("requestId") ?? Guid.NewGuid().ToString(), command);
        return new JsonObject { ["toolCallId"] = r.ToolCallId, ["success"] = r.Success, ["exitCode"] = r.ExitCode, ["output"] = r.Output, ["error"] = r.Error };
    }

    /// <summary>An off-the-record question. Before the first user message there is nothing to ask about, so the model isn't called.</summary>
    private async Task<JsonNode?> AskAsync(JsonNode? p)
    {
        var s = Find(p);
        var question = p.Str("question") ?? throw new JsonRpcException(-32602, "question is required");
        if (!(await s.Session.GetEventsAsync()).OfType<UserMessageEvent>().Any()) return new JsonObject { ["answer"] = null };
        var answer = await s.Session.AskAsync(question);
        return new JsonObject { ["answer"] = string.IsNullOrEmpty(answer) ? null : answer };
    }

    /// <summary>Copies the session. A running prompt is left out (the copy stops before the message that started it); the new session is opened later by `session/load`.</summary>
    private async Task<JsonNode?> ForkAsync(JsonNode? p)
    {
        var s = Find(p);
        var cut = s.Turn is not null ? s.TurnUserEventId : null;
        var forkId = await host.ForkAsync(s.Session.Id, cut);
        var b = ForkCut.Boundary(await s.Session.GetEventsAsync(), cut);
        return new JsonObject
        {
            ["sessionId"] = forkId,
            ["boundary"] = b is null ? null : new JsonObject { ["id"] = b.Id, ["at"] = b.At, ["offset"] = b.Offset },
        };
    }

    /// <summary>Cancels a subagent (by its task call id) or a background shell; tells the client it ended at once, since a cancelled shell sends no notification.</summary>
    private async Task<JsonNode?> StopWorkAsync(JsonNode? p)
    {
        var s = Find(p);
        if (p.Str("workId") is not { } workId || s.Mapper.Background.Running(workId) is not { } item) return new JsonObject { ["stopped"] = false };
        var cancelled = await s.Session.CancelTaskAsync(item.Id);
        if (cancelled && s.Mapper.Background.End(workId, "cancelled", null) is { } ended) Emit(s.Session.Id, ended, s);
        return new JsonObject { ["stopped"] = cancelled };
    }

    /// <summary>A shell's output from a byte offset: kept from its events when attached, asked of the CLI when detached. Null for unknown work and for subagents.</summary>
    private async Task<JsonNode?> WorkOutputAsync(JsonNode? p)
    {
        var s = Find(p);
        var offset = p?["offset"] is JsonValue v && v.TryGetValue<long>(out var o) ? Math.Max(0, o) : 0;
        if (p.Str("workId") is not { } workId || s.Mapper.Background.Find(workId) is not { Kind: "shell" } item) return null;
        if (!item.Detached)
            return s.Mapper.Background.Read(item, offset) is { } b
                ? new JsonObject { ["text"] = b.Text, ["nextOffset"] = b.NextOffset, ["size"] = b.Size, ["truncated"] = b.Truncated }
                : null;
        var r = await s.Session.ReadTaskOutputAsync(item.Id, offset > 0 ? $"shell-output:v1:{offset}" : null);
        var next = r.Cursor?.LastIndexOf(':') is >= 0 and var i && long.TryParse(r.Cursor![(i + 1)..], out var n) ? n : offset + System.Text.Encoding.UTF8.GetByteCount(r.Text);
        return new JsonObject { ["text"] = r.Text, ["nextOffset"] = next, ["size"] = next, ["truncated"] = false };
    }

    /// <summary>Compacts the session's history now. Not while a turn runs: -32001 tells the caller to wait.</summary>
    private async Task<JsonNode?> CompactAsync(JsonNode? p)
    {
        var s = Find(p);
        if (s.Turn is not null) throw new JsonRpcException(-32001, "A turn is running; compact the session when it's over");
        var r = await s.Session.CompactAsync(p.Str("instructions"));
        if (!r.Success) throw new JsonRpcException(-32000, "Copilot couldn't compact the session");
        return new JsonObject { ["success"] = true, ["tokensRemoved"] = r.TokensRemoved, ["messagesRemoved"] = r.MessagesRemoved };
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
        ["availableModels"] = ModelList(models),
    };

    private static JsonArray ModelList(IReadOnlyList<ModelEntry> models) => new(models.Select(m =>
    {
        var o = new JsonObject { ["modelId"] = m.Id, ["name"] = m.Name };
        var meta = new JsonObject();
        if (m.Usage is not null) meta["copilotUsage"] = m.Usage;
        if (m.PriceTier is not null) meta["copilotPriceTier"] = m.PriceTier;
        if (m.Category is not null) meta["copilotCategory"] = m.Category;
        if (meta.Count > 0) o["_meta"] = meta;
        return (JsonNode)o;
    }).ToArray());

    private static IReadOnlyList<ImageData> PromptImages(JsonArray? blocks) =>
        [.. (blocks ?? []).Where(b => b.Str("type") == "image" && b.Str("data") is { Length: > 0 }).Select(b => new ImageData(b.Str("data")!, b.Str("mimeType") ?? "image/png", b.Str("uri")))];

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
