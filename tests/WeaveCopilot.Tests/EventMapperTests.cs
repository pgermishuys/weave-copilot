using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using Xunit;

namespace WeaveCopilot.Tests;

public class EventMapperTests
{
    private static List<AgentOutput> Run(EventMapper m, params GitHub.Copilot.SessionEvent[] evs) => evs.SelectMany(m.Map).ToList();
    private static string Kind(AgentOutput o) => ((Update)o).Body.Str("sessionUpdate")!;

    [Fact]
    public void Streamed_message_is_not_repeated_by_the_final_message()
    {
        var outs = Run(new EventMapper(),
            Ev.Delta("m1", "Hel"), Ev.Delta("m1", "lo"), Ev.Message("m1", "Hello"), Ev.Message("m2", "Whole message, never streamed"));
        var texts = outs.Cast<Update>().Select(u => u.Body["content"]!["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["Hel", "lo", "Whole message, never streamed"], texts);
    }

    [Fact]
    public void Edit_tool_becomes_tool_call_with_a_diff_and_a_completed_update()
    {
        var outs = Run(new EventMapper("/work/demo"),
            Ev.ToolStart("t1", "edit", """{"path":"/work/demo/src/greet.ts","old_str":"a","new_str":"b"}"""),
            Ev.ToolDone("t1", true, "updated"));
        var call = ((Update)outs[0]).Body;
        Assert.Equal("tool_call", call.Str("sessionUpdate"));
        Assert.Equal("edit", call.Str("kind"));
        Assert.Equal("Using edit src/greet.ts", call.Str("title"));
        Assert.Equal("/work/demo/src/greet.ts", call["locations"]![0]!.Str("path"));
        var diff = call["content"]![0]!;
        Assert.Equal(("diff", "a", "b"), (diff.Str("type"), diff.Str("oldText"), diff.Str("newText")));
        var done = ((Update)outs[1]).Body;
        Assert.Equal(("tool_call_update", "completed"), (done.Str("sessionUpdate"), done.Str("status")));
        Assert.Equal("updated", done["rawOutput"]!.Str("content"));
    }

    [Fact]
    public void Failed_tool_is_reported_failed_and_unseen_tools_get_a_tool_call_first()
    {
        var outs = Run(new EventMapper(), Ev.ToolDone("orphan", false, "boom"));
        Assert.Equal(["tool_call", "tool_call_update"], outs.Select(Kind));
        Assert.Equal("failed", ((Update)outs[1]).Body.Str("status"));
    }

    [Theory]
    [InlineData("view", "read"), InlineData("bash", "execute"), InlineData("grep", "search"), InlineData("create", "edit"), InlineData("web_fetch", "fetch"), InlineData("sql", "other")]
    public void Tool_kinds(string tool, string kind) => Assert.Equal(kind, ToolKinds.Of(tool));

    [Fact]
    public void Events_replayed_at_fusion_commit_are_dropped_but_live_ones_are_kept()
    {
        var m = new EventMapper();
        var live = Run(m, Ev.ToolStart("t1", "view", """{"path":"/x"}"""), Ev.Delta("m1", "hi"), Ev.Message("m1", "hi"), Ev.ToolDone("t1", true, "ok"));
        Assert.Equal(["tool_call", "agent_message_chunk", "tool_call_update"], live.Select(Kind));

        var replay = Run(m, Ev.ToolStart("t1", "view", """{"path":"/x"}""", fusion: "f1:commit"), Ev.Message("m9", "hi", fusion: "f1:commit"),
            Ev.ToolDone("t1", true, "ok", fusion: "f1:commit"), Ev.Usage(5, 5, fusion: "f1:commit"));
        Assert.Empty(replay);
        // the dropped replay's usage must not be counted either
        Assert.Null(((Idle)Run(m, Ev.Idle()).Single()).Usage);
    }

    [Fact]
    public void Idle_carries_the_turn_usage_once_and_error_becomes_failure()
    {
        var m = new EventMapper();
        var idle = (Idle)Run(m, Ev.Usage(100, 20), Ev.Usage(50, 5), Ev.Idle()).Single();
        Assert.Equal(175, idle.Usage!["totalTokens"]!.GetValue<long>());
        Assert.Null(((Idle)Run(m, Ev.Idle(aborted: true)).Single()).Usage);
        Assert.Equal("fusion routing transport failed", ((Failure)Run(m, Ev.Error("fusion routing transport failed")).Single()).Message);
    }

    [Fact]
    public void Fusion_passes_become_fusion_notifications_with_order_and_credits()
    {
        var m = new EventMapper();
        var outs = Run(m,
            Ev.Make("session.fusion_resolved", """{"contractVersion":1,"fallbackModel":"model-a","followUpModel":"model-a","policy":"max","fusionId":"f1","pattern":"cascade","primaryModel":"model-a","hint":"Using Cascade","conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""),
            Ev.Make("assistant.fusion_phase_started", """{"fusionId":"f1","phaseId":"p0","phaseKind":"primary","role":"solver","model":"model-a","pattern":"cascade","conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""),
            Ev.Make("assistant.fusion_phase_completed", """{"fusionId":"f1","phaseId":"p0","phaseKind":"primary","role":"solver","model":"model-a","status":"succeeded","durationMs":1500,"usage":{"cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"totalNanoAiu":2500000000},"conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""),
            Ev.Make("assistant.fusion_phase_started", """{"fusionId":"f1","phaseId":"p1","phaseKind":"judge","role":"judge","model":"model-b","pattern":"cascade","conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""),
            Ev.Make("assistant.fusion_phase_completed", """{"fusionId":"f1","phaseId":"p1","phaseKind":"judge","role":"judge","model":"model-b","status":"succeeded","verdict":"accept","durationMs":900,"usage":{"cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"totalNanoAiu":500000000},"conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""),
            Ev.Make("session.fusion_completed", """{"fusionId":"f1","pattern":"cascade","outcome":"completed","followUpModel":"model-a","finalSourcePhaseId":"p0","durationMs":2500,"totalNanoAiu":3000000000,"finalSourceModel":"model-a","conversationScope":"root","syntheticModel":"hf","turnId":"t","policy":"max","planVersion":"1","phasePlan":[],"content":"c","cachedTokens":0,"inputTokens":0,"outputTokens":0,"requestCount":1,"phaseCount":1,"commitId":"c"}"""))
            .Cast<Fusion>().Select(f => f.Params).ToList();
        Assert.Equal(["resolved", "phase_started", "phase_completed", "phase_started", "phase_completed", "completed"], outs.Select(o => o.Str("event")));
        Assert.Equal("Using Cascade", outs[0].Str("hint"));
        Assert.Equal(("primary", "solver", "model-a", 0), (outs[1].Str("phase"), outs[1].Str("role"), outs[1].Str("model"), outs[1]["index"]!.GetValue<int>()));
        Assert.Equal(2.5, outs[2]["credits"]!.GetValue<double>());
        Assert.Equal(("judge", "accept", 1), (outs[4].Str("phase"), outs[4].Str("verdict"), outs[4]["index"]!.GetValue<int>()));
        Assert.Equal(3.0, outs[5]["totalCredits"]!.GetValue<double>());
    }

    [Fact]
    public void History_replay_has_user_and_agent_text_and_tools_but_no_thoughts_usage_or_idle()
    {
        var outs = Run(new EventMapper(history: true),
            Ev.Make("user.message", """{"content":"add a test"}"""), Ev.Make("assistant.reasoning", """{"reasoningId":"r","content":"thinking"}"""),
            Ev.ToolStart("t1", "bash", """{"command":"ls"}""", fusion: "f:commit"), Ev.ToolDone("t1", true, "a.txt", fusion: "f:commit"),
            Ev.Message("m1", "done", fusion: "f:commit"), Ev.Usage(1, 1), Ev.Idle());
        Assert.Equal(["user_message_chunk", "tool_call", "tool_call_update", "agent_message_chunk"], outs.Select(Kind));
    }
}
