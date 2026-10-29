using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class AgentTests
{
    private static (Wire wire, FakeHost host, string[] fatal) Start(bool hydra = false)
    {
        var wire = new Wire();
        var host = new FakeHost();
        var fatal = new string[1];
        new AcpAgent(wire.Rpc, host, hydra, "0.1.0", m => fatal[0] = m).Attach();
        return (wire, host, fatal);
    }

    private static JsonObject Obj(params (string, JsonNode?)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }

    [Fact]
    public async Task Initialize_and_session_new_follow_the_contract()
    {
        var (wire, host, _) = Start();
        var init = await wire.ResultAsync("initialize", Obj(("protocolVersion", 1)));
        Assert.Equal(1, init["protocolVersion"]!.GetValue<int>());
        Assert.True(init["agentCapabilities"]!["loadSession"]!.GetValue<bool>());
        Assert.True(init["agentCapabilities"]!["promptCapabilities"]!["image"]!.GetValue<bool>());
        Assert.Equal("weave-copilot", init["agentInfo"]!.Str("name"));
        Assert.Equal("9.9.9", init["_meta"]!.Str("cliVersion"));

        var res = await wire.ResultAsync("session/new", Obj(("cwd", "/work/demo"), ("mcpServers", new JsonArray())));
        Assert.Equal("s-1", res.Str("sessionId"));
        Assert.Equal("gpt-5-mini", res["models"]!.Str("currentModelId"));
        Assert.Equal("gpt-5-mini", host.CreatedModel);
        Assert.Equal("0x", res["models"]!["availableModels"]![0]!["_meta"]!.Str("copilotUsage"));
        await wire.CloseAsync();
    }

    private static JsonArray McpList() => new()
    {
        Obj(("type", "http"), ("name", "fleet"), ("url", "http://127.0.0.1:6262/mcp"),
            ("headers", new JsonArray(Obj(("name", "Authorization"), ("value", "Bearer t0ken"))))),
        Obj(("type", "stdio"), ("name", "local"), ("command", "x"), ("args", new JsonArray())),
    };

    [Fact]
    public async Task Session_new_and_load_pass_http_mcp_servers_with_their_headers_and_skip_stdio()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("initialize", Obj(("protocolVersion", 1)));
        await wire.ResultAsync("session/new", Obj(("cwd", "/w"), ("mcpServers", McpList())));
        var created = Assert.Single(host.Mcp!);
        Assert.Equal(("fleet", "http://127.0.0.1:6262/mcp"), (created.Name, created.Url));
        Assert.Equal("Bearer t0ken", created.Headers["Authorization"]);

        host.Mcp = null;
        await wire.ResultAsync("session/load", Obj(("sessionId", "s-1"), ("cwd", "/w"), ("mcpServers", McpList())));
        Assert.Equal("Bearer t0ken", Assert.Single(host.Mcp!).Headers["authorization"]);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Prompt_streams_updates_and_returns_end_turn_with_usage()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(Ev.Delta("m", "Hi"), Ev.Usage(10, 2), Ev.Idle());
        var res = await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "say hi"))))));
        Assert.Equal("end_turn", res.Str("stopReason"));
        Assert.Equal(12, res["usage"]!["totalTokens"]!.GetValue<long>());
        Assert.Equal("say hi", host.Session.Sent.Single().Prompt);
        Assert.Equal(["agent_message_chunk", "usage_update"], wire.Notifications.Select(n => n["params"]!["update"]!.Str("sessionUpdate")));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Session_error_becomes_a_readable_jsonrpc_error_and_never_hangs()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(Ev.Error("fusion routing transport failed"));
        var res = await wire.CallAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "go"))))));
        Assert.Equal("fusion routing transport failed", res["error"]!.Str("message"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Cancel_aborts_and_the_prompt_returns_cancelled()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        var prompt = wire.CallAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "long"))))));
        while (host.Session.Sent.Count == 0) await Task.Delay(10);
        wire.Notify("session/cancel", Obj(("sessionId", "s-1")));
        Assert.Equal("cancelled", (await prompt)["result"]!.Str("stopReason"));
        Assert.True(host.Session.Aborted);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Steer_with_no_running_turn_is_rejected_with_32001_and_sends_nothing()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        var res = await wire.CallAsync("_fleet/steer", Obj(("sessionId", "s-1"), ("prompt", "late")));
        Assert.Equal(-32001, res["error"]!["code"]!.GetValue<int>());
        Assert.Equal("No turn is running; send the text as a prompt instead", res["error"]!["message"]!.GetValue<string>());
        Assert.Empty(host.Session.Sent);
        await wire.CloseAsync();
    }

    [Fact]
    public void An_mcp_permission_ask_names_the_tool_without_its_servers_prefix()
    {
        var req = new GitHub.Copilot.PermissionRequestMcp { ServerName = "fleet", ToolName = "fleet-fleet_page_show", ToolTitle = "Show page", ToolCallId = "t1", ReadOnly = false };
        Assert.Equal("fleet_page_show", PermissionMapper.ToolCall(req, null)["_meta"]!["toolName"]!.GetValue<string>());
    }

    [Fact]
    public void Permission_asks_without_a_tool_call_id_get_a_unique_synthetic_id()
    {
        var req = new GitHub.Copilot.PermissionRequestUrl { Url = "https://example.test", Intention = "Fetch" };
        var a = PermissionMapper.ToolCall(req, null)["toolCallId"]!.GetValue<string>();
        var b = PermissionMapper.ToolCall(req, null)["toolCallId"]!.GetValue<string>();
        Assert.StartsWith("perm-", a);
        Assert.NotEqual(a, b);
        var withId = new GitHub.Copilot.PermissionRequestUrl { Url = "u", Intention = "Fetch", ToolCallId = "t9" };
        Assert.Equal("t9", PermissionMapper.ToolCall(withId, null)["toolCallId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Steer_sends_immediately_and_set_model_switches_the_model()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", 900), ("method", "session/prompt"), ("params", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "go"))))))));
        while (host.Session.Sent.Count == 0) await Task.Delay(10);
        await wire.ResultAsync("_fleet/steer", Obj(("sessionId", "s-1"), ("prompt", "change course")));
        Assert.Equal([("go", false), ("change course", true)], host.Session.Sent);
        host.Session.Raise(Ev.Idle());
        Assert.Equal(900, (await wire.NextAsync())["id"]!.GetValue<int>());
        await wire.ResultAsync("session/set_model", Obj(("sessionId", "s-1"), ("modelId", "other")));
        await wire.ResultAsync("session/set_config_option", Obj(("sessionId", "s-1"), ("configId", "model"), ("value", "gpt-5-mini")));
        Assert.Equal(["other", "gpt-5-mini"], host.Session.Models);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Permission_round_trip_maps_the_clients_choice_back_to_the_sdk_decision()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/work/demo")));
        JsonObject? seen = null;
        var choice = "allow_once";
        wire.OnAgentRequest = m => { seen = m["params"]!.AsObject(); return Obj(("outcome", Obj(("outcome", "selected"), ("optionId", choice)))); };

        var write = new GitHub.Copilot.PermissionRequestWrite { CanOfferSessionApproval = false, Intention = "Create file", ToolCallId = "t1", FileName = "/work/demo/src/a.ts", Diff = "d", NewFileContents = "x" };
        // The agent asks the client while the fake "CLI" waits for the decision.
        var pending = host.Permission!("s-1", write);
        var drain = wire.NextAsync();
        var req = await drain;
        Assert.Equal("session/request_permission", req.Str("method"));
        Assert.Equal("Write src/a.ts", req["params"]!["toolCall"]!.Str("title"));
        Assert.Equal(["allow_once", "allow_always", "reject_once"], req["params"]!["options"]!.AsArray().Select(o => o.Str("optionId")));
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", req["id"]!.DeepClone()), ("result", Obj(("outcome", Obj(("outcome", "selected"), ("optionId", "allow_once")))))));
        Assert.Equal("approve-once", (await pending).Kind);

        var denied = host.Permission!("s-1", write);
        req = await wire.NextAsync();
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", req["id"]!.DeepClone()), ("result", Obj(("outcome", Obj(("outcome", "cancelled")))))));
        Assert.Equal("reject", (await denied).Kind);
        await wire.CloseAsync();
    }

    [Fact]
    public void Always_allow_maps_to_a_session_approval_and_reject_denies()
    {
        var shell = new GitHub.Copilot.PermissionRequestShell { CanOfferSessionApproval = false, FullCommandText = "ls", HasWriteFileRedirection = false, Intention = "List", PossiblePaths = [], PossibleUrls = [], Commands = [new() { Identifier = "ls", ReadOnly = true }] };
        var always = PermissionMapper.ToDecision(shell, Obj(("outcome", "selected"), ("optionId", "allow_always")));
        var forSession = Assert.IsType<GitHub.Copilot.Rpc.PermissionDecisionApproveForSession>(always);
        Assert.Equal(["ls"], Assert.IsType<GitHub.Copilot.Rpc.PermissionDecisionApproveForSessionApprovalCommands>(forSession.Approval).CommandIdentifiers);
        Assert.Equal("reject", PermissionMapper.ToDecision(shell, Obj(("outcome", "selected"), ("optionId", "reject_once"))).Kind);
    }

    [Fact]
    public async Task Fleet_models_lists_models_without_creating_a_session_and_shares_the_hydrafusion_gate()
    {
        var (wire, host, _) = Start(hydra: true);
        var res = await wire.ResultAsync("_fleet/models", Obj());
        var ids = res["models"]!.AsArray().Select(m => m.Str("modelId")).ToList();
        Assert.Equal(["gpt-5-mini", "other", "hydrafusion"], ids);
        Assert.Equal("gpt-5-mini", res.Str("currentModelId"));
        Assert.Null(host.CreatedModel);
        Assert.Equal(0, host.Creates);
        var again = await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        Assert.Equal(1, host.HydraProbes); // cached: one probe for both requests
        Assert.Contains("hydrafusion", again["models"]!["availableModels"]!.AsArray().Select(m => m.Str("modelId")));
        await wire.CloseAsync();

        var (wire2, host2, _) = Start(hydra: false);
        var plain = await wire2.ResultAsync("_fleet/models", Obj());
        Assert.DoesNotContain("hydrafusion", plain["models"]!.AsArray().Select(m => m.Str("modelId")));
        Assert.Equal(0, host2.HydraProbes);
        await wire2.CloseAsync();
    }

    [Fact]
    public async Task Null_fields_are_omitted_from_notifications()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(
            Ev.Make("assistant.fusion_phase_completed", """{"fusionId":"f","phaseId":"p","phaseKind":"primary","role":"solver","model":"m","pattern":"single","status":"succeeded","durationMs":5,"conversationScope":"root","content":"c","usage":{"cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"totalNanoAiu":0}}"""),
            Ev.Make("assistant.fusion_phase_started", """{"fusionId":"f","phaseId":"p","phaseKind":"primary","role":"solver","model":"m","pattern":"single","conversationScope":"root"}"""),
            Ev.Idle());
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "go"))))));
        var fusion = wire.Notifications.Where(n => n.Str("method") == "_fleet/fusion").Select(n => n["params"]!.AsObject()).ToList();
        Assert.NotEmpty(fusion);
        Assert.All(fusion, f => Assert.DoesNotContain(f, kv => kv.Value is null));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Load_replays_history_before_responding()
    {
        var (wire, host, _) = Start();
        host.Session.History.AddRange([Ev.Make("user.message", """{"content":"hello"}"""), Ev.Message("m1", "hi there")]);
        var res = await wire.ResultAsync("session/load", Obj(("sessionId", "s-1"), ("cwd", "/w"), ("mcpServers", new JsonArray())));
        Assert.NotNull(res["models"]);
        Assert.Equal(["user_message_chunk", "agent_message_chunk"], wire.Notifications.Select(n => n["params"]!["update"]!.Str("sessionUpdate")));
        await wire.CloseAsync();
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task Hydrafusion_is_offered_only_when_enabled_and_the_probe_succeeds(bool flag, bool probeOk, bool offered)
    {
        var (wire, host, _) = Start(flag);
        host.HydraProbeResult = probeOk;
        var res = await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        var ids = res["models"]!["availableModels"]!.AsArray().Select(m => m.Str("modelId")).ToList();
        Assert.Equal(offered, ids.Contains("hydrafusion"));
        Assert.Equal(flag ? 1 : 0, host.HydraProbes);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Prompt_images_reach_copilot_as_attachments_and_an_image_alone_still_sends_text()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "image"), ("data", "iVBORw0="), ("mimeType", "image/png"))))));
        var image = Assert.Single(host.Session.Images);
        Assert.Equal(("iVBORw0=", "image/png"), (image.Base64, image.MimeType));
        Assert.NotEmpty(host.Session.Sent.Single().Prompt);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_finished_sql_call_publishes_copilots_todo_list_as_a_plan_update_once()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.Todos.AddRange([new TodoRow("Pack the basket", "done"), new TodoRow("Pick a spot", "in_progress"), new TodoRow("Bring water", "pending")]);
        host.Session.OnSend = (s, _) => s.Raise(Ev.ToolStart("c1", "sql", "{}"), Ev.ToolDone("c1", true, "3 row(s) inserted"), Ev.ToolStart("c2", "sql", "{}"), Ev.ToolDone("c2", true, "ok"), Ev.Idle());
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "plan a picnic"))))));
        await Task.Delay(300);
        var plans = wire.Notifications.Select(n => n["params"]!["update"]!).Where(u => u.Str("sessionUpdate") == "plan").ToList();
        var plan = Assert.Single(plans);
        Assert.Equal(["completed", "in_progress", "pending"], plan["entries"]!.AsArray().Select(e => e.Str("status")));
        Assert.Equal("Pack the basket", plan["entries"]![0]!.Str("content"));

        var asked = await wire.ResultAsync("_fleet/todos", Obj(("sessionId", "s-1")));
        Assert.Equal(3, asked["entries"]!.AsArray().Count);
        await wire.CloseAsync();
    }

    private static JsonArray Text(string t) => new(Obj(("type", "text"), ("text", t)));

    [Fact]
    public async Task Compact_runs_a_manual_compaction_and_reports_what_it_removed()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        var res = await wire.ResultAsync("_fleet/compact", Obj(("sessionId", "s-1"), ("instructions", "keep the plan")));
        Assert.Equal((true, 1200, 14), (res["success"]!.GetValue<bool>(), res["tokensRemoved"]!.GetValue<int>(), res["messagesRemoved"]!.GetValue<int>()));
        Assert.Equal("keep the plan", Assert.Single(host.Session.Compactions));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Compact_while_a_turn_runs_is_rejected_with_32001_and_compacts_nothing()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", 900), ("method", "session/prompt"), ("params", Obj(("sessionId", "s-1"), ("prompt", Text("long"))))));
        while (host.Session.Sent.Count == 0) await Task.Delay(10);
        var res = await wire.CallAsync("_fleet/compact", Obj(("sessionId", "s-1")));
        Assert.Equal(-32001, res["error"]!["code"]!.GetValue<int>());
        Assert.Equal("A turn is running; compact the session when it's over", res["error"]!.Str("message"));
        Assert.Empty(host.Session.Compactions);
        host.Session.Raise(Ev.Idle());
        Assert.Equal(900, (await wire.NextAsync())["id"]!.GetValue<int>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Compact_that_does_not_succeed_is_a_plain_32000_error()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.CompactOutcome = new CompactResult(false, 0, 0);
        var res = await wire.CallAsync("_fleet/compact", Obj(("sessionId", "s-1")));
        Assert.Equal(-32000, res["error"]!["code"]!.GetValue<int>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Compaction_events_reach_the_client_as_fleet_compaction_with_the_session_id()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(Ev.CompactStart("a1", "threshold"), Ev.Idle(), Ev.CompactDone("a1"));
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", Text("go"))));
        var notes = wire.Notifications.Where(n => n.Str("method") == "_fleet/compaction").Select(n => n["params"]!).ToList();
        Assert.Equal(["started", "ended"], notes.Select(n => n.Str("phase")));
        Assert.All(notes, n => Assert.Equal(("s-1", "auto"), (n.Str("sessionId"), n.Str("trigger"))));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Model_notes_go_ahead_of_the_text_for_the_model_while_the_transcript_keeps_only_the_text()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", Text("say hi")), ("_meta", Obj(("modelNotes", new JsonArray("note one", 42, "", "note two"))))));
        await wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", Text("again")), ("_meta", Obj(("modelNotes", new JsonArray())))));
        Assert.Equal(["note one\n\nnote two\n\nsay hi", "again"], host.Session.Sent.Select(x => x.Prompt));
        Assert.Equal(["say hi", null], host.Session.DisplayPrompts);
        await wire.CloseAsync();
    }

    private static async Task<(Wire wire, FakeHost host)> Commanding()
    {
        var (wire, host, _) = Start();
        host.Session.Commands.AddRange([new("haiku-poet", "Writes a haiku", "topic", "skill"), new("tone", "Sets the tone", null, "skill"), new("env", "Shows the environment", null, "builtin"), new("model", "Pick a model", null, "builtin")]);
        host.Session.Results["haiku-poet"] = new("prompt", "Follow the haiku-poet skill.\n\nARGUMENTS: about rain");
        host.Session.Results["tone"] = new("prompt", "Follow the tone skill.");
        host.Session.Results["env"] = new("text", "cwd: /work/demo");
        host.Session.Results["model"] = new("unsupported", null);
        host.Session.OnSend = (s, _) => s.Raise(Ev.Idle());
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        return (wire, host);
    }

    private static Task<JsonNode> Prompt(Wire wire, string text, JsonObject? meta = null) =>
        wire.ResultAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", Text(text)), ("_meta", meta)));

    [Fact]
    public async Task Fleet_commands_lists_every_command_with_its_hint_and_kind()
    {
        var (wire, _) = await Commanding();
        var cmds = (JsonArray)(await wire.ResultAsync("_fleet/commands", Obj(("sessionId", "s-1"))))["commands"]!;
        Assert.Equal(4, cmds.Count);
        Assert.Equal(("haiku-poet", "Writes a haiku", "topic", "skill"), (cmds[0].Str("name"), cmds[0].Str("description"), cmds[0].Str("hint"), cmds[0].Str("kind")));
        Assert.Null(cmds[1]!["hint"]);
        Assert.Equal("builtin", cmds[2].Str("kind"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_typed_skill_command_is_expanded_and_sent_with_the_typed_text_as_the_display()
    {
        var (wire, host) = await Commanding();
        var res = await Prompt(wire, "/haiku-poet  about rain ", Obj(("modelNotes", new JsonArray("note one"))));
        Assert.Equal("end_turn", res.Str("stopReason"));
        Assert.Equal([("haiku-poet", "about rain")], host.Session.Invoked);
        Assert.Equal("note one\n\nFollow the haiku-poet skill.\n\nARGUMENTS: about rain", Assert.Single(host.Session.Sent).Prompt);
        Assert.Equal("/haiku-poet  about rain ", Assert.Single(host.Session.DisplayPrompts));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_command_without_input_is_invoked_with_null()
    {
        var (wire, host) = await Commanding();
        await Prompt(wire, "/tone");
        Assert.Equal([("tone", (string?)null)], host.Session.Invoked);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_text_result_is_one_message_chunk_and_end_turn_with_no_send_and_the_next_prompt_still_runs()
    {
        var (wire, host) = await Commanding();
        var res = await Prompt(wire, "/env");
        Assert.Equal("end_turn", res.Str("stopReason"));
        Assert.Empty(host.Session.Sent);
        var up = Assert.Single(wire.Notifications, n => n["params"]!["update"].Str("sessionUpdate") == "agent_message_chunk")["params"]!["update"]!["content"];
        Assert.Equal("cwd: /work/demo", up.Str("text"));
        await Prompt(wire, "hello");
        Assert.Equal(["hello"], host.Session.Sent.Select(x => x.Prompt));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_completed_result_with_no_message_ends_the_turn_quietly()
    {
        var (wire, host) = await Commanding();
        host.Session.Results["env"] = new("text", null);
        Assert.Equal("end_turn", (await Prompt(wire, "/env")).Str("stopReason"));
        Assert.Empty(wire.Notifications);
        Assert.Empty(host.Session.Sent);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_command_that_needs_the_terminal_is_a_32000_error_and_frees_the_turn()
    {
        var (wire, host) = await Commanding();
        var err = (await wire.CallAsync("session/prompt", Obj(("sessionId", "s-1"), ("prompt", Text("/model"))))) ["error"]!;
        Assert.Equal(-32000, err["code"]!.GetValue<int>());
        Assert.Equal("/model needs Copilot's own terminal; Fleet can't show it.", err.Str("message"));
        await Prompt(wire, "next");
        Assert.Equal(["next"], host.Session.Sent.Select(x => x.Prompt));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task An_unknown_slash_name_is_plain_text()
    {
        var (wire, host) = await Commanding();
        await Prompt(wire, "/etc/hosts is odd");
        Assert.Empty(host.Session.Invoked);
        Assert.Equal(["/etc/hosts is odd"], host.Session.Sent.Select(x => x.Prompt));
        Assert.Null(Assert.Single(host.Session.DisplayPrompts));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Meta_skills_go_between_the_notes_and_the_text_and_skip_names_that_are_not_skills()
    {
        var (wire, host) = await Commanding();
        await Prompt(wire, "write it", Obj(("modelNotes", new JsonArray("note one")), ("skills", new JsonArray("tone", "env", "nope", "haiku-poet"))));
        Assert.Equal([("tone", (string?)null), ("haiku-poet", null)], host.Session.Invoked);
        Assert.Equal("note one\n\nFollow the tone skill.\n\nFollow the haiku-poet skill.\n\nARGUMENTS: about rain\n\nwrite it", Assert.Single(host.Session.Sent).Prompt);
        Assert.Equal("write it", Assert.Single(host.Session.DisplayPrompts));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Skill_directories_from_meta_reach_create_and_resume()
    {
        var (wire, host, _) = Start();
        JsonObject Meta() => Obj(("skillDirectories", new JsonArray("/data/skills/me", "", 7)));
        await wire.ResultAsync("session/new", Obj(("cwd", "/w"), ("_meta", Meta())));
        await wire.ResultAsync("session/load", Obj(("sessionId", "s-1"), ("cwd", "/w"), ("_meta", Meta())));
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        Assert.Equal(["/data/skills/me"], host.SkillDirs[0]);
        Assert.Equal(["/data/skills/me"], host.SkillDirs[1]);
        Assert.Empty(host.SkillDirs[2] ?? []);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Fleet_agents_lists_the_hosts_agents_without_a_session()
    {
        var (wire, host, _) = Start();
        host.AgentList.Add(new("pirate", "Pirate", "Talks like a pirate", "project"));
        var agents = (JsonArray)(await wire.ResultAsync("_fleet/agents", Obj(("cwd", "/w"))))["agents"]!;
        Assert.Equal(("pirate", "Pirate", "Talks like a pirate", "project"), (agents[0].Str("name"), agents[0].Str("displayName"), agents[0].Str("description"), agents[0].Str("source")));
        await wire.CloseAsync();
    }

    [Fact]
    public void Agents_marked_not_user_invocable_are_left_out()
    {
        var shown = SdkHost.UserAgents([new() { Name = "pirate", UserInvocable = true }, new() { Name = "internal", UserInvocable = false }, new() { Name = "plain" }]);
        Assert.Equal(["pirate", "plain"], shown.Select(a => a.Name));
    }

    [Fact]
    public async Task Set_config_option_agent_selects_and_an_empty_or_null_value_deselects()
    {
        var (wire, host) = await Commanding();
        Assert.Empty((JsonObject)await wire.ResultAsync("session/set_config_option", Obj(("sessionId", "s-1"), ("configId", "agent"), ("value", "pirate"))));
        await wire.ResultAsync("session/set_config_option", Obj(("sessionId", "s-1"), ("configId", "agent"), ("value", "")));
        await wire.ResultAsync("session/set_config_option", Obj(("sessionId", "s-1"), ("configId", "agent"), ("value", null)));
        Assert.Equal(["pirate", "", null], host.Session.Agents);
        await wire.CloseAsync();
    }
}
