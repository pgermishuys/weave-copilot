using System.Text.Json.Nodes;
using GitHub.Copilot;
using WeaveCopilot.Acp;
using Xunit;

namespace WeaveCopilot.Tests;

/// <summary>Fork, off-the-record questions, `!` shell commands and stable replay keys. All content is invented.</summary>
public class Lane5Tests
{
    private static JsonObject Obj(params (string, JsonNode?)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v; return o; }
    private static List<AgentOutput> Run(EventMapper m, params SessionEvent[] evs) => evs.SelectMany(m.Map).ToList();

    private static SessionEvent User(string text) => Ev.Make("user.message", $$"""{"content":"{{text}}"}""");
    private static SessionEvent Asked(string call, string cmd) => Ev.Make("tool.user_requested", """{"toolCallId":"CALL","toolName":"local_shell","arguments":{"command":"CMD","description":"Execute shell command: CMD"}}""".Replace("CALL", call).Replace("CMD", cmd));
    private static SessionEvent Printed(string call, int seq, string text) => Ev.Make("tool.shell_output", $$"""{"toolCallId":"{{call}}","sequence":{{seq}},"stream":"stdout","text":"{{text}}"}""");
    private static SessionEvent ShellDone(string call, string text, int exit = 0) =>
        Ev.Make("tool.execution_complete", """{"toolCallId":"CALL","isUserRequested":true,"success":OK,"result":{"content":"TEXT"},"shellExecution":{"exitCode":EXIT}}""".Replace("CALL", call).Replace("OK", exit == 0 ? "true" : "false").Replace("TEXT", text).Replace("EXIT", exit.ToString()));

    private static (Wire wire, FakeHost host) Start()
    {
        var wire = new Wire();
        var host = new FakeHost();
        new AcpAgent(wire.Rpc, host, false, "0.1.0", _ => { }).Attach();
        return (wire, host);
    }

    private static async Task<(Wire, FakeHost)> StartSession()
    {
        var (wire, host) = Start();
        await wire.ResultAsync("session/new", Obj(("cwd", "/w")));
        return (wire, host);
    }

    [Fact]
    public void Replayed_user_messages_carry_a_stable_key_and_live_ones_do_not()
    {
        var u = User("Hello");
        var key = ((Update)Run(new EventMapper(history: true), u).Single()).Body["_meta"]!["fleetKey"]!;
        Assert.Equal(u.Id.ToString(), key.Str("id"));
        Assert.Equal(u.Timestamp.ToString("O"), key.Str("at"));
        Assert.Empty(Run(new EventMapper(), u));
    }

    [Fact]
    public void Ephemeral_question_and_session_info_events_produce_nothing()
    {
        var evs = new[]
        {
            Ev.Make("ui.ephemeral_query", """{"requestId":"r1","phase":"chunk","chunk":"part"}"""),
            Ev.Make("ui.ephemeral_query", """{"requestId":"r1","phase":"completed","answer":"all of it"}"""),
            Ev.Make("session.info", """{"infoType":"fork","message":"Forked"}"""),
        };
        Assert.Empty(Run(new EventMapper(), evs));
        Assert.Empty(Run(new EventMapper(history: true), evs));
    }

    [Fact]
    public void Live_shell_command_goes_started_output_ended_and_makes_no_tool_call()
    {
        var m = new EventMapper();
        var outs = Run(m, Asked("c1", "ls -a"), Printed("c1", 0, "one\\n"), Printed("other", 0, "ignored"), ShellDone("c1", "one", 3));
        Assert.All(outs, o => Assert.IsType<ShellCommand>(o));
        var p = outs.Cast<ShellCommand>().Select(o => o.Params).ToList();
        Assert.Equal(["started", "output", "ended"], p.Select(x => x.Str("phase")));
        Assert.Equal("ls -a", p[0].Str("command"));
        Assert.Equal("c1", p[0].Str("toolCallId"));
        Assert.Equal("one\n", p[1].Str("text"));
        Assert.Equal("one", p[2].Str("output"));
        Assert.Equal(3, p[2]["exitCode"]!.GetValue<long>());
        Assert.False(p[2]["success"]!.GetValue<bool>());
        Assert.NotNull(p[2].Str("at"));
    }

    [Fact]
    public void Replayed_shell_command_has_started_and_ended_only_among_the_other_updates()
    {
        var outs = Run(new EventMapper(history: true), User("Hi"), Asked("c1", "pwd"), Printed("c1", 0, "x"), ShellDone("c1", "/w"), Ev.Message("m1", "Done"));
        Assert.Equal(["Update", "ShellCommand", "ShellCommand", "Update"], outs.Select(o => o.GetType().Name));
        Assert.Equal(["started", "ended"], outs.OfType<ShellCommand>().Select(o => o.Params.Str("phase")));
        Assert.DoesNotContain(outs.OfType<Update>(), u => u.Body.Str("sessionUpdate") is "tool_call" or "tool_call_update");
    }

    [Fact]
    public void An_agents_own_shell_output_stays_ignored()
    {
        Assert.Empty(Run(new EventMapper(), Printed("agent-call", 0, "text")));
    }

    [Fact]
    public async Task Shell_notifications_reach_the_client_and_the_request_returns_the_result()
    {
        var (wire, host) = await StartSession();
        host.Session.ShellOutcome = new("c9", false, 2, "boom", "bad");
        var started = Asked("c9", "false");
        var res = await wire.ResultAsync("_fleet/shell", Obj(("sessionId", "s-1"), ("command", "false"), ("requestId", "r-7")));
        host.Session.Raise(started);
        Assert.Equal(("r-7", "false"), host.Session.Shells.Single());
        Assert.Equal("c9", res.Str("toolCallId"));
        Assert.False(res["success"]!.GetValue<bool>());
        Assert.Equal(2, res["exitCode"]!.GetValue<int>());
        Assert.Equal("boom", res.Str("output"));
        Assert.Equal("bad", res.Str("error"));
        var n = await wire.NextAsync();
        Assert.Equal("_fleet/shell_command", n.Str("method"));
        Assert.Equal("s-1", n["params"]!.Str("sessionId"));
        Assert.Equal("started", n["params"]!.Str("phase"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Shell_without_a_request_id_makes_one_up()
    {
        var (wire, host) = await StartSession();
        await wire.ResultAsync("_fleet/shell", Obj(("sessionId", "s-1"), ("command", "pwd")));
        Assert.True(Guid.TryParse(host.Session.Shells.Single().RequestId, out _));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Shell_while_a_turn_runs_is_refused_with_32001()
    {
        var (wire, host) = await StartSession();
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", 900), ("method", "session/prompt"), ("params", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "go"))))))));
        while (host.Session.Sent.Count == 0) await Task.Delay(10);
        var res = await wire.CallAsync("_fleet/shell", Obj(("sessionId", "s-1"), ("command", "pwd")));
        Assert.Equal(-32001, res["error"]!["code"]!.GetValue<int>());
        Assert.Empty(host.Session.Shells);
        host.Session.Raise(Ev.Idle());
        await wire.NextAsync();
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Ask_without_a_user_message_answers_null_and_never_calls_the_model()
    {
        var (wire, host) = await StartSession();
        var res = await wire.ResultAsync("_fleet/ask", Obj(("sessionId", "s-1"), ("question", "What now?")));
        Assert.Null(res["answer"]);
        Assert.Empty(host.Session.Asked);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Ask_returns_the_answer_and_an_empty_one_as_null()
    {
        var (wire, host) = await StartSession();
        host.Session.History.Add(User("Hi"));
        var res = await wire.ResultAsync("_fleet/ask", Obj(("sessionId", "s-1"), ("question", "What is up?")));
        Assert.Equal("an answer", res.Str("answer"));
        Assert.Equal(["What is up?"], host.Session.Asked);
        host.Session.Answer = "";
        Assert.Null((await wire.ResultAsync("_fleet/ask", Obj(("sessionId", "s-1"), ("question", "Again?"))))["answer"]);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Fork_with_no_running_turn_copies_everything()
    {
        var (wire, host) = await StartSession();
        var u = User("Hi");
        host.Session.History.AddRange([u, Ev.Message("m1", "Hello")]);
        var res = await wire.ResultAsync("_fleet/fork", Obj(("sessionId", "s-1")));
        Assert.Equal("fork-1", res.Str("sessionId"));
        Assert.Equal(("s-1", (string?)null), host.Forks.Single());
        Assert.Equal(u.Id.ToString(), res["boundary"]!.Str("id"));
        Assert.Equal(u.Timestamp.ToString("O"), res["boundary"]!.Str("at"));
        Assert.Equal(1, res["boundary"]!["offset"]!.GetValue<int>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Fork_during_a_turn_cuts_before_the_message_that_started_it()
    {
        var (wire, host) = await StartSession();
        var first = User("First");
        var second = User("Second");
        host.Session.History.AddRange([first, Ev.Message("m1", "Reply"), second]);
        host.Session.OnSend = (s, _) => s.Raise(second);
        wire.Send(Obj(("jsonrpc", "2.0"), ("id", 900), ("method", "session/prompt"), ("params", Obj(("sessionId", "s-1"), ("prompt", new JsonArray(Obj(("type", "text"), ("text", "Second"))))))));
        while (host.Session.Sent.Count == 0) await Task.Delay(10);
        var res = await wire.ResultAsync("_fleet/fork", Obj(("sessionId", "s-1")));
        Assert.Equal(second.Id.ToString(), host.Forks.Single().ToEventId);
        Assert.Equal(first.Id.ToString(), res["boundary"]!.Str("id"));
        Assert.Equal(1, res["boundary"]!["offset"]!.GetValue<int>());
        host.Session.Raise(Ev.Idle());
        await wire.NextAsync();

        // The turn is over: the cut is forgotten.
        await wire.ResultAsync("_fleet/fork", Obj(("sessionId", "s-1")));
        Assert.Null(host.Forks[1].ToEventId);
        await wire.CloseAsync();
    }

    [Fact]
    public void Boundary_offsets_follow_whether_the_agent_replied()
    {
        var replied = User("A");
        var unreplied = User("B");
        Assert.Equal(1, ForkCut.Boundary([replied, Ev.Message("m", "Hi")], null)!.Offset);
        Assert.Equal(1, ForkCut.Boundary([replied, Ev.ToolStart("t", "view", "{}")], null)!.Offset);
        Assert.Equal(0, ForkCut.Boundary([replied, Ev.Message("m", "Hi"), unreplied], null)!.Offset);
        Assert.Equal(unreplied.Id.ToString(), ForkCut.Boundary([replied, Ev.Message("m", "Hi"), unreplied], null)!.Id);
        Assert.Equal(0, ForkCut.Boundary([unreplied, Ev.Message("m", "")], null)!.Offset);
    }

    [Fact]
    public void Boundary_is_a_shell_command_when_that_came_last_and_null_when_nothing_is_keyed()
    {
        var shell = Asked("c1", "pwd");
        var b = ForkCut.Boundary([User("A"), Ev.Message("m", "Hi"), shell, ShellDone("c1", "/w"), Ev.Message("m2", "Seen")], null)!;
        Assert.Equal(("c1", 0, shell.Timestamp.ToString("O")), (b.Id, b.Offset, b.At));
        Assert.Null(ForkCut.Boundary([Ev.Message("m", "Hi")], null));
        Assert.Null(ForkCut.Boundary([], null));
    }

    [Fact]
    public void Boundary_stops_before_the_cut()
    {
        var a = User("A");
        var b = User("B");
        Assert.Equal(a.Id.ToString(), ForkCut.Boundary([a, Ev.Message("m", "Hi"), b], b.Id.ToString())!.Id);
        Assert.Null(ForkCut.Boundary([a], a.Id.ToString()));
    }
}
