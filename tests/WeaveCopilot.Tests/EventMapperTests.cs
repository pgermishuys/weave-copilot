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
    public void A_tool_acp_has_no_kind_for_carries_its_real_name_in_meta()
    {
        var outs = Run(new EventMapper("/work/demo"), Ev.ToolStart("t1", "fleet_page_show", "{}"), Ev.ToolStart("t2", "edit", "{}"));
        var other = ((Update)outs[0]).Body;
        Assert.Equal("other", other.Str("kind"));
        Assert.Equal("fleet_page_show", other["_meta"]!["toolName"]!.GetValue<string>());
        Assert.Null(((Update)outs[1]).Body["_meta"]);
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

        var replay = Run(m, Ev.ToolStart("t1", "view", """{"path":"/x"}""", fusion: "f1:commit"), Ev.Message("m1", "hi", fusion: "f1:commit"),
            Ev.ToolDone("t1", true, "ok", fusion: "f1:commit"), Ev.Usage(5, 5, fusion: "f1:commit"));
        Assert.Empty(replay);
        // the dropped replay's usage must not be counted either
        Assert.Null(((Idle)Run(m, Ev.Idle()).Single()).Usage);
    }

    [Fact]
    public void A_commit_copy_with_no_live_twin_is_emitted()
    {
        var m = new EventMapper();
        var outs = Run(m,
            Ev.ToolStart("t1", "view", """{"path":"/x"}""", fusion: "f1:commit"), Ev.ToolDone("t1", true, "ok", fusion: "f1:commit"),
            Ev.Message("answer", "Only here", fusion: "f1:commit"));
        Assert.Equal(["tool_call", "tool_call_update", "agent_message_chunk"], outs.Select(Kind));
        // a start seen live but never completed live: only the completion is taken from the commit copy
        var m2 = new EventMapper();
        var mixed = Run(m2, Ev.ToolStart("t2", "view", """{"path":"/x"}"""), Ev.ToolStart("t2", "view", """{"path":"/x"}""", fusion: "f1:commit"), Ev.ToolDone("t2", true, "ok", fusion: "f1:commit"));
        Assert.Equal(["tool_call", "tool_call_update"], mixed.Select(Kind));
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
        Assert.Equal(("judge", "approved", 1), (outs[4].Str("phase"), outs[4].Str("verdict"), outs[4]["index"]!.GetValue<int>()));
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

    // Shapes recorded from real Cascade and Critique runs, with invented content and models.
    private static GitHub.Copilot.SessionEvent Resolved(string pattern) => Ev.Make("session.fusion_resolved", $$"""{"contractVersion":1,"fallbackModel":"model-a","followUpModel":"model-a","policy":"standard","fusionId":"f1","pattern":"{{pattern}}","primaryModel":"model-a","secondaryModel":"model-b","hint":"Using {{pattern}}","syntheticModel":"hf","turnId":"f1:turn","planVersion":"2","phasePlan":[]}""");
    private static string Js(string? v) => v is null ? "null" : System.Text.Json.JsonSerializer.Serialize(v);
    private static GitHub.Copilot.SessionEvent PhaseDone(string id, string kind, string role, string model, string status, string? verdict = null, string? content = null) => Ev.Make("assistant.fusion_phase_completed",
        $$$"""{"fusionId":"f1","phaseId":"{{{id}}}","phaseKind":"{{{kind}}}","role":"{{{role}}}","model":"{{{model}}}","status":"{{{status}}}","durationMs":4000,"conversationScope":"root","projectionMode":"none","verdict":{{{Js(verdict)}}},"content":{{{Js(content)}}},"usage":{"cachedTokens":0,"cacheWriteTokens":0,"inputTokens":10,"outputTokens":5,"requestCount":1,"totalNanoAiu":196170000}}""");
    private static GitHub.Copilot.SessionEvent Completed(string pattern, string outcome = "completed") => Ev.Make("session.fusion_completed",
        $$"""{"fusionId":"f1","pattern":"{{pattern}}","outcome":"{{outcome}}","commitId":"f1:commit","turnId":"f1:turn","syntheticModel":"hf","finalSourcePhaseId":"f1:revision","finalSourceModel":"model-a","followUpModel":"model-a","degradedReason":null,"phaseCount":3,"requestCount":7,"inputTokens":139779,"outputTokens":4882,"cachedTokens":114770,"cacheWriteTokens":24151,"totalNanoAiu":16827470000,"durationMs":69434}""");

    [Fact]
    public void A_critic_without_a_verdict_field_is_read_from_its_json_answer()
    {
        var outs = Run(new EventMapper(),
            PhaseDone("f1:critic", "critic", "critic", "model-b", "succeeded", content: "{\"assessment\":\"revise\",\"feedback\":\"x\"}"),
            PhaseDone("f1:judge", "judge", "judge", "model-b", "succeeded", verdict: "accept", content: "{\"score\":5}")).Cast<Fusion>().Select(f => f.Params).ToList();
        Assert.Equal(["needs repair", "approved"], outs.Select(o => o.Str("verdict")));
        Assert.Null(EventMapper.VerdictOf(null, "plain text answer"));
        Assert.Null(EventMapper.VerdictOf(null, "{not json"));
    }

    [Fact]
    public void A_failed_pass_carries_its_reason_not_a_verdict()
    {
        var f = Run(new EventMapper(), Ev.Make("assistant.fusion_phase_failed", """{"fusionId":"f1","phaseId":"f1:critic","phaseKind":"critic","role":"critic","model":"model-b","status":"failed","reason":"timeout","errorMessage":"The critic timed out","durationMs":900,"conversationScope":"review","usage":{"cachedTokens":0,"cacheWriteTokens":0,"inputTokens":1,"outputTokens":0,"requestCount":1,"totalNanoAiu":0}}"""))
            .Cast<Fusion>().Single().Params;
        Assert.Equal(("failed", "The critic timed out"), (f.Str("status"), f.Str("reason")));
        Assert.Null(f.Str("verdict"));
    }

    [Fact]
    public void A_critique_turn_ends_with_its_totals_which_replace_the_per_call_figures_in_the_usage()
    {
        var m = new EventMapper();
        var outs = Run(m, Resolved("critique"), Ev.Usage(999, 9), PhaseDone("f1:phase:0", "draft", "solver", "model-a", "succeeded"),
            PhaseDone("f1:critic", "critic", "critic", "model-b", "succeeded", verdict: "revise"), Completed("critique"), Ev.Idle());
        var fusion = outs.OfType<Fusion>().Select(f => f.Params).ToList();
        Assert.Equal(["resolved", "phase_completed", "phase_completed", "completed"], fusion.Select(o => o.Str("event")));
        Assert.Equal([0, 1], fusion.Skip(1).Take(2).Select(o => o["index"]!.GetValue<int>()));
        Assert.Equal(("critique", 16.82747, 7L), (fusion[3].Str("pattern"), fusion[3]["totalCredits"]!.GetValue<double>(), fusion[3]["requests"]!.GetValue<long>()));
        var usage = outs.OfType<Idle>().Single().Usage!;
        Assert.Equal((139779L, 4882L, 144661L, 114770L, 24151L), (usage["inputTokens"]!.GetValue<long>(), usage["outputTokens"]!.GetValue<long>(),
            usage["totalTokens"]!.GetValue<long>(), usage["cachedReadTokens"]!.GetValue<long>(), usage["cachedWriteTokens"]!.GetValue<long>()));
        Assert.Equal(16.82747, usage["credits"]!.GetValue<double>(), 5);
    }

    [Fact]
    public void A_plain_turn_reports_the_credits_its_calls_did()
    {
        var m = new EventMapper();
        var idle = (Idle)Run(m, Ev.Make("assistant.usage", """{"model":"m","inputTokens":100,"outputTokens":20,"copilotUsage":{"totalNanoAiu":250000000,"tokenDetails":[]}}"""),
            Ev.Make("assistant.usage", """{"model":"m","inputTokens":50,"outputTokens":5,"copilotUsage":{"totalNanoAiu":250000000,"tokenDetails":[]}}"""), Ev.Idle()).Single();
        Assert.Equal(0.5, idle.Usage!["credits"]!.GetValue<double>());
        Assert.Null(((Idle)Run(m, Ev.Usage(1, 1), Ev.Idle()).Single()).Usage!["credits"]);
    }

    [Fact]
    public void History_replays_the_fusion_events_the_cli_kept_numbering_passes_that_have_no_start_event()
    {
        var outs = Run(new EventMapper(history: true),
            Resolved("cascade"), Ev.Make("user.message", """{"content":"add a test"}"""),
            PhaseDone("f1:phase:0", "primary", "solver", "model-a", "succeeded"), Ev.ToolStart("t1", "bash", """{"command":"ls"}"""),
            PhaseDone("f1:judge", "judge", "judge", "model-b", "succeeded", verdict: "accept"), Completed("cascade"), Ev.Usage(1, 1), Ev.Idle());
        // the CLI logs the route before the user's message; the turn row still comes after it, ahead of the first pass
        Assert.Equal(["user_message_chunk", "_resolved", "_phase_completed", "tool_call", "_phase_completed", "_completed"],
            outs.Select(o => o is Fusion f ? "_" + f.Params.Str("event") : Kind(o)));
        var passes = outs.OfType<Fusion>().Where(f => f.Params.Str("event") == "phase_completed").Select(f => f.Params).ToList();
        Assert.Equal([0, 1], passes.Select(p => p["index"]!.GetValue<int>()));
        Assert.Equal("approved", passes[1].Str("verdict"));
        // and replaying a turn must not leave usage behind for the next live one
        Assert.Null(((Idle)Run(new EventMapper(history: true), Ev.Idle()).SingleOrDefault() ?? new Idle(false, null)).Usage);
    }
}
