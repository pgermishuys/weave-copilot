using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
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
        Assert.False(init["agentCapabilities"]!["promptCapabilities"]!["image"]!.GetValue<bool>());
        Assert.Equal("weave-copilot", init["agentInfo"]!.Str("name"));
        Assert.Equal("9.9.9", init["_meta"]!.Str("cliVersion"));

        var res = await wire.ResultAsync("session/new", Obj(("cwd", "/work/demo"), ("mcpServers", new JsonArray())));
        Assert.Equal("s-1", res.Str("sessionId"));
        Assert.Equal("gpt-5-mini", res["models"]!.Str("currentModelId"));
        Assert.Equal("gpt-5-mini", host.CreatedModel);
        Assert.Equal("0x", res["models"]!["availableModels"]![0]!["_meta"]!.Str("copilotUsage"));
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
        Assert.Equal("agent_message_chunk", wire.Notifications.Single()["params"]!["update"]!.Str("sessionUpdate"));
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
    public async Task Steer_sends_immediately_and_set_model_switches_the_model()
    {
        var (wire, host, _) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        await wire.ResultAsync("_fleet/steer", Obj(("sessionId", "s-1"), ("prompt", "change course")));
        Assert.Equal(("change course", true), host.Session.Sent.Single());
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
}
