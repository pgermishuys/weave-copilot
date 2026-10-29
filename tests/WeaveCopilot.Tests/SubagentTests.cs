using System.Text.Json.Nodes;
using GitHub.Copilot;
using WeaveCopilot.Acp;
using Xunit;

namespace WeaveCopilot.Tests;

public class SubagentTests
{
    private const string A = ",\"agentId\":\"a-1\"";
    private static List<AgentOutput> Run(EventMapper m, params SessionEvent[] evs) => evs.SelectMany(m.Map).ToList();
    private static JsonObject Body(AgentOutput o) => ((Update)o).Body;
    private static string? Parent(AgentOutput o) => Body(o)["_meta"]?["parentToolCallId"]?.GetValue<string>();
    private static JsonObject[] Works(IEnumerable<AgentOutput> outs) => [.. outs.OfType<Work>().Select(w => w.Params)];

    private static SessionEvent Started(string mode = "sync") => Ev.Make("subagent.started",
        $$"""{"agentDescription":"Read the readme","agentDisplayName":"readme-reader","agentName":"explore","agentType":"explore","executionMode":"{{mode}}","model":"m","toolCallId":"task-1"}""", A);
    private static SessionEvent Completed(bool cancelled = false) => Ev.Make("subagent.completed",
        $$"""{"agentDisplayName":"readme-reader","agentName":"explore","toolCallId":"task-1"{{(cancelled ? ",\"cancelled\":true" : "")}}}""", A);
    private static SessionEvent ShellDone(string id, int exit) => Ev.Make("system.notification",
        $$$"""{"content":"x","kind":{"type":"shell_completed","description":"d","exitCode":{{{exit}}},"shellId":"{{{id}}}"}}""");
    private static SessionEvent DetachedDone(string id) => Ev.Make("system.notification",
        $$$"""{"content":"x","kind":{"type":"shell_detached_completed","description":"d","shellId":"{{{id}}}"}}""");
    private static SessionEvent ShellOut(string call, string text) => Ev.Make("tool.shell_output", $$"""{"sequence":0,"stream":"stdout","text":"{{text}}","toolCallId":"{{call}}"}""");
    private static SessionEvent AsyncBash(string call, string shell, bool detach) =>
        Ev.ToolStart(call, "bash", $$"""{"command":"sleep 9","description":"Wait","mode":"async","shellId":"{{shell}}"{{(detach ? ",\"detach\":true" : "")}}}""");
    private static SessionEvent AsyncBashDone(string call, string shell) => Ev.ToolDone(call, true, $"<command started in background with shellId: {shell}>");

    [Fact]
    public void A_subagents_text_and_tools_are_tagged_with_its_task_call_and_never_untagged()
    {
        var m = new EventMapper();
        var outs = Run(m, Started(),
            Ev.Make("user.message", """{"content":"Read README"}""", A),
            Ev.Make("assistant.message_delta", """{"messageId":"c1","deltaContent":"Hel"}""", A),
            Ev.Make("assistant.message", """{"messageId":"c1","content":"Hello"}""", A),
            Ev.Make("tool.execution_start", """{"toolCallId":"c-t1","toolName":"fleet_page_show","arguments":{}}""", A),
            Ev.Make("tool.execution_complete", """{"toolCallId":"c-t1","success":true,"result":{"content":"ok"}}""", A),
            Ev.Delta("p1", "main"));
        var updates = outs.OfType<Update>().ToList();
        Assert.Equal(["user_message_chunk", "agent_message_chunk", "tool_call", "tool_call_update", "agent_message_chunk"], updates.Select(u => u.Body.Str("sessionUpdate")));
        Assert.All(updates.Take(4), u => Assert.Equal("task-1", Parent(u)));
        Assert.Equal("fleet_page_show", updates[2].Body["_meta"]!["toolName"]!.GetValue<string>());   // merged, not replaced
        Assert.Null(updates[4].Body["_meta"]);
    }

    [Fact]
    public void A_subagents_context_bar_and_errors_are_dropped_but_its_usage_still_counts()
    {
        var m = new EventMapper();
        var outs = Run(m, Started(),
            Ev.Make("session.usage_info", """{"tokenLimit":100,"currentTokens":5,"messagesLength":1}""", A),
            Ev.Make("session.error", """{"errorType":"x","message":"boom"}""", A),
            Ev.Make("assistant.usage", """{"model":"m","inputTokens":7,"outputTokens":3}""", A),
            Ev.Usage(10, 2), Ev.Idle());
        Assert.DoesNotContain(outs, o => o is Failure);
        Assert.DoesNotContain(outs, o => o is Update);
        var usage = Assert.IsType<Idle>(outs.Last()).Usage!;
        Assert.Equal(17, usage["inputTokens"]!.GetValue<long>());
    }

    [Fact]
    public void An_event_whose_agent_is_unknown_is_dropped_not_shown_as_the_parents()
    {
        var outs = Run(new EventMapper(), Ev.Make("assistant.message", """{"messageId":"c1","content":"Hello"}""", A), Ev.Make("user.message", """{"content":"hi"}""", A));
        Assert.Empty(outs);
    }

    [Fact]
    public void Replay_tags_a_subagents_events_too_including_its_own_prompt()
    {
        var outs = Run(new EventMapper(history: true), Started(),
            Ev.Make("user.message", """{"content":"Read README"}""", A),
            Ev.Make("assistant.message", """{"messageId":"c1","content":"Hello"}""", A),
            Ev.Make("user.message", """{"content":"Thanks"}"""));
        Assert.Empty(Works(outs));
        Assert.Equal(["task-1", "task-1", null], outs.Select(Parent));
        Assert.Equal("user_message_chunk", Body(outs[0]).Str("sessionUpdate"));
    }

    [Fact]
    public void Subagents_start_and_end_once_sync_or_background()
    {
        var m = new EventMapper();
        var started = Works(Run(m, Started("background"))).Single();
        Assert.Equal(("started", "task-1", "subagent", "readme-reader", "Read the readme"), (started.Str("event"), started.Str("workId"), started.Str("kind"), started.Str("title"), started.Str("label")));
        Assert.True(started["background"]!.GetValue<bool>());
        Assert.True(started["canStop"]!.GetValue<bool>());
        Assert.False(started["canReadOutput"]!.GetValue<bool>());
        var ended = Works(Run(m, Completed(cancelled: true), Completed())).Single();
        Assert.Equal(("ended", "cancelled"), (ended.Str("event"), ended.Str("endedReason")));
        Assert.False(Works(Run(new EventMapper(), Started("sync")).Take(1)).Single()["background"]!.GetValue<bool>());
    }

    [Fact]
    public void A_failed_subagent_ends_with_its_error()
    {
        var m = new EventMapper();
        Run(m, Started());
        var ended = Works(Run(m, Ev.Make("subagent.failed", """{"agentName":"explore","agentDisplayName":"r","error":"it broke","toolCallId":"task-1"}""", A))).Single();
        Assert.Equal(("error", "it broke"), (ended.Str("endedReason"), ended.Str("detail")));
    }

    [Fact]
    public void Attached_and_detached_shells_start_when_the_bash_call_completes_and_end_from_notifications()
    {
        var m = new EventMapper();
        Assert.Empty(Works(Run(m, AsyncBash("b1", "tick-a", false), AsyncBash("b2", "tick-b", true))));
        var a = Works(Run(m, AsyncBashDone("b1", "tick-a"))).Single();
        Assert.Equal(("started", "tick-a", "shell", "Wait", "sleep 9", "b1"), (a.Str("event"), a.Str("workId"), a.Str("kind"), a.Str("title"), a.Str("label"), a.Str("toolCallId")));
        Assert.True(a["canReadOutput"]!.GetValue<bool>());
        Assert.Equal("tick-b", Works(Run(m, AsyncBashDone("b2", "tick-b"))).Single().Str("workId"));
        var end1 = Works(Run(m, ShellDone("tick-a", 3), ShellDone("tick-a", 0))).Single();
        Assert.Equal(("error", "exit 3"), (end1.Str("endedReason"), end1.Str("detail")));
        Assert.Equal("completed", Works(Run(m, DetachedDone("tick-b"), DetachedDone("tick-b"))).Single().Str("endedReason"));
    }

    [Fact]
    public void A_plain_bash_call_is_not_background_work()
    {
        Assert.Empty(Works(Run(new EventMapper(), Ev.ToolStart("b1", "bash", """{"command":"ls"}"""), Ev.ToolDone("b1", true, "x"))));
    }

    private static async Task<(Wire wire, FakeHost host)> Started2()
    {
        var wire = new Wire();
        var host = new FakeHost();
        new AcpAgent(wire.Rpc, host, false, "0.1.0", _ => { }).Attach();
        await wire.ResultAsync("session/new", new JsonObject { ["cwd"] = "/w" });
        return (wire, host);
    }

    private static JsonObject P(string method, string workId, long? offset = null) =>
        new() { ["sessionId"] = "s-1", ["workId"] = workId, ["offset"] = offset };

    [Fact]
    public async Task Work_stop_resolves_the_call_id_to_the_agent_and_reports_it_ended_cancelled()
    {
        var (wire, host) = await Started2();
        host.Session.Raise(Started());
        Assert.True((await wire.ResultAsync("_fleet/work/stop", P("", "task-1")))["stopped"]!.GetValue<bool>());
        Assert.Equal(["a-1"], host.Session.Cancelled);
        var note = wire.Notifications.Last();
        Assert.Equal("_fleet/work", note.Str("method"));
        Assert.Equal(("ended", "cancelled", "s-1"), (note["params"]!.Str("event"), note["params"]!.Str("endedReason"), note["params"]!.Str("sessionId")));
        Assert.False((await wire.ResultAsync("_fleet/work/stop", P("", "task-1")))["stopped"]!.GetValue<bool>());
        Assert.False((await wire.ResultAsync("_fleet/work/stop", P("", "nope")))["stopped"]!.GetValue<bool>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Work_stop_says_false_when_the_cli_did_not_cancel_and_keeps_the_work_running()
    {
        var (wire, host) = await Started2();
        host.Session.CancelResult = false;
        host.Session.Raise(AsyncBash("b1", "sh-1", false), AsyncBashDone("b1", "sh-1"));
        Assert.False((await wire.ResultAsync("_fleet/work/stop", P("", "sh-1")))["stopped"]!.GetValue<bool>());
        Assert.Equal(["sh-1"], host.Session.Cancelled);
        host.Session.CancelResult = true;
        Assert.True((await wire.ResultAsync("_fleet/work/stop", P("", "sh-1")))["stopped"]!.GetValue<bool>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Work_output_reads_an_attached_shell_from_its_buffer_by_byte_offset()
    {
        var (wire, host) = await Started2();
        host.Session.Raise(AsyncBash("b1", "sh-1", false), ShellOut("b1", "a1\\n"), AsyncBashDone("b1", "sh-1"), ShellOut("b1", "é2\\n"));
        var all = await wire.ResultAsync("_fleet/work/output", P("", "sh-1"));
        Assert.Equal(("a1\né2\n", 7L, 7L, false), (all.Str("text"), all["nextOffset"]!.GetValue<long>(), all["size"]!.GetValue<long>(), all["truncated"]!.GetValue<bool>()));
        var rest = await wire.ResultAsync("_fleet/work/output", P("", "sh-1", 3));
        Assert.Equal(("é2\n", 7L), (rest.Str("text"), rest["nextOffset"]!.GetValue<long>()));
        Assert.Empty(host.Session.Reads);
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Work_output_keeps_only_the_last_mebibyte_and_says_so()
    {
        var (wire, host) = await Started2();
        host.Session.Raise(AsyncBash("b1", "sh-1", false), AsyncBashDone("b1", "sh-1"));
        var chunk = new string('x', 600_000);
        host.Session.Raise(ShellOut("b1", chunk), ShellOut("b1", chunk), ShellOut("b1", chunk), ShellOut("b1", chunk));
        var r = await wire.ResultAsync("_fleet/work/output", P("", "sh-1", 0));
        Assert.Equal(2_400_000L, r["size"]!.GetValue<long>());
        Assert.True(r["truncated"]!.GetValue<bool>());
        Assert.Equal(1 << 20, r.Str("text")!.Length);
        Assert.False((await wire.ResultAsync("_fleet/work/output", P("", "sh-1", 2_400_000 - 10)))["truncated"]!.GetValue<bool>());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Work_output_reads_a_detached_shell_through_a_cursor_and_is_null_for_unknown_work_and_subagents()
    {
        var (wire, host) = await Started2();
        host.Session.ReadResult = new("b8\n", "shell-output:v1:42", false, null);
        host.Session.Raise(AsyncBash("b2", "sh-2", true), AsyncBashDone("b2", "sh-2"), Started());
        var first = await wire.ResultAsync("_fleet/work/output", P("", "sh-2"));
        var again = await wire.ResultAsync("_fleet/work/output", P("", "sh-2", 21));
        Assert.Equal([("sh-2", (string?)null), ("sh-2", "shell-output:v1:21")], host.Session.Reads);
        Assert.Equal(("b8\n", 42L, 42L, false), (first.Str("text"), first["nextOffset"]!.GetValue<long>(), first["size"]!.GetValue<long>(), first["truncated"]!.GetValue<bool>()));
        Assert.Equal(42L, again["nextOffset"]!.GetValue<long>());
        Assert.Null((await wire.CallAsync("_fleet/work/output", P("", "task-1")))["result"]);
        Assert.Null((await wire.CallAsync("_fleet/work/output", P("", "nope")))["result"]);
        await wire.CloseAsync();
    }
}
