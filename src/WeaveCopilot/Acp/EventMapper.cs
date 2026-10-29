using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace WeaveCopilot.Acp;

/// <summary>What the mapper wants the agent to do. Pure data so the mapper is testable without the CLI.</summary>
public abstract record AgentOutput;
/// <summary>Body of a `session/update` notification.</summary>
public sealed record Update(JsonObject Body) : AgentOutput;
/// <summary>Params (minus sessionId) of the `_fleet/fusion` notification.</summary>
public sealed record Fusion(JsonObject Params) : AgentOutput;
/// <summary>Params (minus sessionId) of the `_fleet/compaction` notification.</summary>
public sealed record Compaction(JsonObject Params) : AgentOutput;
/// <summary>The session went idle: the running prompt is over.</summary>
public sealed record Idle(bool Aborted, JsonObject? Usage) : AgentOutput;
/// <summary>A tool that may have changed Copilot's todo list finished: the agent reads the list again.</summary>
public sealed record TodosMaybeChanged : AgentOutput;
/// <summary>Params (minus sessionId) of the `_fleet/shell_command` notification: a `!` command started, printed, or ended.</summary>
public sealed record ShellCommand(JsonObject Params) : AgentOutput;
/// <summary>Params (minus sessionId) of the `_fleet/work` notification: a subagent or background shell started or ended.</summary>
public sealed record Work(JsonObject Params) : AgentOutput;
/// <summary>The session reported an error: fail the running prompt with this text.</summary>
public sealed record Failure(string Message) : AgentOutput;

/// <summary>
/// Maps Copilot SDK session events to ACP updates. One instance per session; stateful only for
/// de-duplication (streamed ids, tool calls, fusion pass order) and per-turn token usage.
/// Live mode streams; history mode replays a finished transcript (no thoughts, no usage; the CLI keeps a HydraFusion turn's
/// resolved, finished-pass and completed events, so those replay, but not a pass's start).
/// </summary>
public sealed class EventMapper(string? cwd = null, bool history = false)
{
    private readonly HashSet<string> _liveMessages = [];       // message ids that already produced text live
    private readonly HashSet<string> _liveToolStarts = [];
    private readonly HashSet<string> _liveToolDones = [];
    private readonly HashSet<string> _streamedReasoning = [];
    private readonly HashSet<string> _toolsSeen = [];
    private readonly HashSet<string> _userShells = [];         // tool call ids of the user's own `!` commands
    private readonly HashSet<string> _sqlCalls = [];            // Copilot keeps its todo list in a `todos` table it writes with the sql tool
    private readonly Dictionary<string, int> _phaseIndex = [];
    private readonly Dictionary<string, string?> _compactionTriggers = [];   // a Complete without a trigger of its own takes its Start's
    private long _in, _out, _cacheRead, _cacheWrite, _reasoning;
    private double? _size;                                       // the model's context window, from the last usage_info
    private long _nanoAiu;                                       // credits the turn's own calls reported, in nano-AIU
    private Fusion? _heldResolved;                           // history: the CLI logs `resolved` before the user's message, so it waits for the turn's first pass
    private FusionTotals? _fusion;                               // the turn's authoritative totals, once fusion_completed arrived

    /// <summary>The subagents and background shells this session started.</summary>
    public BackgroundWork Background { get; } = new();

    private static string? TriggerOf(string? trigger) => trigger is null ? null : trigger == "manual" ? "manual" : "auto";

    private static Compaction Compact(string phase, string? trigger, double? before, double? after, string? summary, string? error) => new(new JsonObject
    {
        ["phase"] = phase, ["trigger"] = trigger, ["tokensBefore"] = before, ["tokensAfter"] = after, ["summary"] = summary, ["error"] = error,
    });

    /// <summary>
    /// Subagents run in the parent's event stream; every event of theirs carries an AgentId. Their updates are tagged with the
    /// parent's `task` call (`_meta.parentToolCallId`) so the client files them under it; what is the session's own (context bar,
    /// errors, the end of the turn) is dropped, and an event that can't be traced to a call is dropped rather than shown as the parent's.
    /// </summary>
    public IEnumerable<AgentOutput> Map(SessionEvent ev)
    {
        foreach (var w in Background.Track(ev, report: !history)) yield return w;
        if (ev.AgentId is not { } agent)
        {
            foreach (var o in MapMain(ev)) yield return o;
            yield break;
        }
        var call = Background.CallOf(agent) ?? ev switch
        {
            ToolExecutionStartEvent t => t.Data.ParentToolCallId,
            ToolExecutionCompleteEvent t => t.Data.ParentToolCallId,
            _ => null,
        };
        if (call is null || ev is SessionUsageInfoEvent) yield break;
        IEnumerable<AgentOutput> outs = ev is UserMessageEvent { Data.Content: { Length: > 0 } text } u && !history ? [Chunk("user_message_chunk", text + ImagePlaceholders(u.Data))] : MapMain(ev);
        foreach (var o in outs)
        {
            if (o is Update up) { (up.Body["_meta"] as JsonObject ?? (JsonObject)(up.Body["_meta"] = new JsonObject()))["parentToolCallId"] = call; yield return up; }
            else if (o is TodosMaybeChanged) yield return o;
        }
    }

    private IEnumerable<AgentOutput> MapMain(SessionEvent ev)
    {
        // HydraFusion replays tool/message events at commit with a commit id. Drop a replay only when its live twin
        // was already emitted: with streaming on, an answer can reach us only on the commit copy.
        if (!history && CommitId(ev) is not null && IsLiveTwinSeen(ev)) yield break;
        if (!history && CommitId(ev) is not null && ev is AssistantUsageEvent) yield break;

        switch (ev)
        {
            case UserMessageEvent u when history && !string.IsNullOrEmpty(u.Data.Content):
            {
                // The key lets Fleet give a replayed message the same id every time.
                var user = Chunk("user_message_chunk", u.Data.Content + ImagePlaceholders(u.Data));
                user.Body["_meta"] = new JsonObject { ["fleetKey"] = new JsonObject { ["id"] = u.Id.ToString(), ["at"] = u.Timestamp.ToString("O") } };
                yield return user;
                break;
            }
            case ToolUserRequestedEvent ur when ur.Data.ToolCallId is { } shellId:
                _userShells.Add(shellId);
                yield return Shell(shellId, "started", ur.Timestamp, new JsonObject { ["command"] = Json.FromElement(ur.Data.Arguments)?.Str("command") });
                break;
            case ToolShellOutputEvent so when !history && so.Data.ToolCallId is { } outId && _userShells.Contains(outId):
                yield return Shell(outId, "output", so.Timestamp, new JsonObject { ["text"] = so.Data.Text });
                break;
            case ToolExecutionCompleteEvent { Data.IsUserRequested: true } done:
                var text = done.Data.Success ? done.Data.Result?.Content : done.Data.Error?.Message ?? done.Data.Result?.Content;
                yield return Shell(done.Data.ToolCallId ?? "", "ended", done.Timestamp, new JsonObject { ["output"] = text, ["exitCode"] = done.Data.ShellExecution?.ExitCode, ["success"] = done.Data.Success });
                break;
            case AssistantMessageDeltaEvent d when !history && !string.IsNullOrEmpty(d.Data.DeltaContent):
                if (d.Data.MessageId is { } mid) _liveMessages.Add(mid);
                yield return Chunk("agent_message_chunk", d.Data.DeltaContent);
                break;
            case AssistantMessageEvent m:
                if (!string.IsNullOrEmpty(m.Data.Content) && !(m.Data.MessageId is { } id && _liveMessages.Contains(id)))
                {
                    if (!history && m.Data.MessageId is { } liveId) _liveMessages.Add(liveId);
                    yield return Chunk("agent_message_chunk", m.Data.Content);
                }
                break;
            case AssistantReasoningDeltaEvent rd when !history && !string.IsNullOrEmpty(rd.Data.DeltaContent):
                if (rd.Data.ReasoningId is { } rid) _streamedReasoning.Add(rid);
                yield return Chunk("agent_thought_chunk", rd.Data.DeltaContent);
                break;
            case AssistantReasoningEvent r when !history && !string.IsNullOrEmpty(r.Data.Content) && !(r.Data.ReasoningId is { } id2 && _streamedReasoning.Contains(id2)):
                yield return Chunk("agent_thought_chunk", r.Data.Content);
                break;
            case ToolExecutionStartEvent ts:
                if (ts.Data.ToolName == "sql" && ts.Data.ToolCallId is { } sqlId) _sqlCalls.Add(sqlId);
                yield return ToolStart(ts.Data);
                break;
            case ToolExecutionCompleteEvent tc:
                foreach (var o in ToolComplete(tc.Data)) yield return o;
                if (!history && tc.Data.ToolCallId is { } doneId && _sqlCalls.Remove(doneId)) yield return new TodosMaybeChanged();
                break;
            case SessionUsageInfoEvent ui when !history:
                _size = ui.Data.TokenLimit;
                yield return new Update(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = ui.Data.CurrentTokens, ["size"] = ui.Data.TokenLimit });
                break;
            case AssistantUsageEvent au when !history:
                _in += au.Data.InputTokens ?? 0; _out += au.Data.OutputTokens ?? 0; _reasoning += au.Data.ReasoningTokens ?? 0;
                _cacheRead += au.Data.CacheReadTokens ?? 0; _cacheWrite += au.Data.CacheWriteTokens ?? 0;
                _nanoAiu += (long)(au.Data.CopilotUsage?.TotalNanoAiu ?? 0);
                // The main agent's own call (not a subagent's): its tokens, as the CLI counts them (input with the cached ones).
                if (ev.AgentId is null && au.Data.ParentToolCallId is null)
                    yield return new Update(new JsonObject
                    {
                        ["sessionUpdate"] = "usage_update", ["used"] = (au.Data.InputTokens ?? 0) + (au.Data.OutputTokens ?? 0), ["size"] = _size,
                        ["_meta"] = new JsonObject { ["call"] = new JsonObject
                        {
                            ["input"] = au.Data.InputTokens ?? 0, ["cacheRead"] = au.Data.CacheReadTokens ?? 0, ["cacheWrite"] = au.Data.CacheWriteTokens ?? 0,
                            ["output"] = au.Data.OutputTokens ?? 0, ["reasoning"] = au.Data.ReasoningTokens ?? 0,
                        } },
                    });
                break;
            case SessionErrorEvent e when !history:
                yield return new Failure(e.Data.Message ?? e.Data.ErrorType ?? "Copilot reported an error");
                break;
            case SessionFusionRouteFailedEvent rf when !history:
                yield return new Failure(rf.Data.ErrorMessage ?? rf.Data.Reason ?? "fusion routing failed");
                break;
            case SessionIdleEvent idle when !history:
                yield return new Idle(idle.Data.Aborted == true, TakeUsage());
                break;
            case SessionCompactionStartEvent cs:
                var startTrigger = TriggerOf(cs.Data.Trigger?.Value);
                if (cs.Data.CompactionAttemptId is { } startId) _compactionTriggers[startId] = startTrigger;
                if (!history) yield return Compact("started", startTrigger, cs.Data.CurrentTokens, null, null, null);
                break;
            case SessionCompactionCompleteEvent cc:
                var cd = cc.Data;
                var trigger = cd.Trigger?.Value is { } own ? TriggerOf(own) : cd.CompactionAttemptId is { } cid && _compactionTriggers.TryGetValue(cid, out var t) ? t : null;
                yield return Compact(cd.Success ? "ended" : "failed", trigger, cd.PreCompactionTokens, cd.PostCompactionTokens,
                    string.IsNullOrWhiteSpace(cd.SummaryContent) ? null : cd.SummaryContent.Trim(),
                    cd.Success ? null : cd.Error ?? cd.FailureReason ?? "Copilot couldn't compact the session");
                break;
            case SessionFusionResolvedEvent fr:
                var resolved = new Fusion(new JsonObject
                {
                    ["event"] = "resolved", ["pattern"] = fr.Data.Pattern.Value, ["model"] = fr.Data.PrimaryModel, ["secondaryModel"] = fr.Data.SecondaryModel,
                    ["hint"] = fr.Data.Hint, ["status"] = "resolved", ["index"] = 0,
                });
                if (history) _heldResolved = resolved;
                else yield return resolved;
                break;
            case AssistantFusionPhaseStartedEvent ps when !history:
                var idx = IndexOf(ps.Data.PhaseId);
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_started", ["pattern"] = ps.Data.Pattern.Value, ["phase"] = ps.Data.PhaseKind.Value,
                    ["role"] = ps.Data.Role, ["model"] = ps.Data.Model, ["status"] = "running", ["index"] = idx,
                });
                break;
            case AssistantFusionPhaseCompletedEvent pc:
                if (TakeHeld() is { } h1) yield return h1;
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_completed", ["phase"] = pc.Data.PhaseKind.Value, ["role"] = pc.Data.Role, ["model"] = pc.Data.Model,
                    ["status"] = pc.Data.Status.Value, ["verdict"] = VerdictOf(pc.Data.Verdict, pc.Data.Content), ["durationMs"] = pc.Data.DurationMs,
                    ["credits"] = pc.Data.Usage is { } pu ? pu.TotalNanoAiu / 1e9 : null,
                    ["index"] = IndexOf(pc.Data.PhaseId),
                });
                break;
            case AssistantFusionPhaseFailedEvent pf:
                if (TakeHeld() is { } h2) yield return h2;
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_completed", ["phase"] = pf.Data.PhaseKind.Value, ["role"] = pf.Data.Role, ["model"] = pf.Data.Model,
                    ["status"] = "failed", ["reason"] = pf.Data.ErrorMessage ?? pf.Data.Reason, ["durationMs"] = pf.Data.DurationMs,
                    ["credits"] = pf.Data.Usage is { } fu ? fu.TotalNanoAiu / 1e9 : null,
                    ["index"] = IndexOf(pf.Data.PhaseId),
                });
                break;
            case SessionFusionCompletedEvent fc:
                if (TakeHeld() is { } h3) yield return h3;
                if (!history)
                    _fusion = new FusionTotals(fc.Data.InputTokens, fc.Data.OutputTokens, fc.Data.CachedTokens, fc.Data.CacheWriteTokens ?? 0, fc.Data.RequestCount, fc.Data.TotalNanoAiu);
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "completed", ["pattern"] = fc.Data.Pattern.Value, ["model"] = fc.Data.FinalSourceModel, ["status"] = fc.Data.Outcome,
                    ["durationMs"] = fc.Data.DurationMs, ["totalCredits"] = fc.Data.TotalNanoAiu / 1e9, ["index"] = Math.Max(0, _phaseIndex.Count - 1),
                    ["requests"] = fc.Data.RequestCount, ["inputTokens"] = fc.Data.InputTokens, ["outputTokens"] = fc.Data.OutputTokens,
                    ["degradedReason"] = fc.Data.DegradedReason,
                });
                _phaseIndex.Clear();
                break;
        }
    }

    /// <summary>One `_fleet/shell_command` notification; <paramref name="more"/> holds the phase's own fields.</summary>
    private static ShellCommand Shell(string toolCallId, string phase, DateTimeOffset at, JsonObject more)
    {
        more["toolCallId"] = toolCallId; more["phase"] = phase; more["at"] = at.ToString("O");
        return new ShellCommand(more);
    }

    private Fusion? TakeHeld() { var h = _heldResolved; _heldResolved = null; return h; }

    private bool IsLiveTwinSeen(SessionEvent ev) => ev switch
    {
        AssistantMessageEvent e => e.Data.MessageId is { } id && _liveMessages.Contains(id),
        ToolExecutionStartEvent e => e.Data.ToolCallId is { } id && _liveToolStarts.Contains(id),
        ToolExecutionCompleteEvent e => e.Data.ToolCallId is { } id && _liveToolDones.Contains(id),
        _ => false,
    };

    private static string? CommitId(SessionEvent ev) => ev switch
    {
        AssistantMessageEvent e => e.Data.Fusion?.CommitId,
        ToolExecutionStartEvent e => e.Data.Fusion?.CommitId,
        ToolExecutionCompleteEvent e => e.Data.Fusion?.CommitId,
        AssistantUsageEvent e => e.Data.Fusion?.CommitId,
        _ => null,
    };

    /// <summary>Phase ids are numbered in the order they are first seen; a replayed pass has no start event, so it is numbered when it ends.</summary>
    private int IndexOf(string phaseId)
    {
        if (!_phaseIndex.TryGetValue(phaseId, out var i)) _phaseIndex[phaseId] = i = _phaseIndex.Count;
        return i;
    }

    /// <summary>
    /// A judge's verdict as the CLI reports it ("accept"), or else a critic's assessment out of its JSON answer
    /// ("revise"), in the words the pass row shows.
    /// </summary>
    internal static string? VerdictOf(string? verdict, string? content)
    {
        var raw = verdict;
        if (string.IsNullOrEmpty(raw) && content is { Length: > 0 } && content.TrimStart().StartsWith('{'))
        {
            try { raw = System.Text.Json.Nodes.JsonNode.Parse(content)?.Str("assessment"); } catch (System.Text.Json.JsonException) { }
        }
        return raw?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "accept" or "accepted" or "approve" or "approved" or "pass" => "approved",
            "revise" or "repair" or "reject" or "rejected" or "fail" => "needs repair",
            var other => other,
        };
    }

    private sealed record FusionTotals(double Input, double Output, double Cached, double CacheWrite, double Requests, double NanoAiu);

    /// <summary>
    /// The turn's tokens and credits, once. A HydraFusion turn reports one total across all its passes, which wins over the
    /// per-call figures (its commit copies are dropped above). Input counts the cached tokens, as the CLI reports it.
    /// </summary>
    private JsonObject? TakeUsage()
    {
        JsonObject? u = null;
        if (_fusion is { } f)
        {
            u = new JsonObject
            {
                ["inputTokens"] = (long)f.Input, ["outputTokens"] = (long)f.Output, ["totalTokens"] = (long)(f.Input + f.Output),
                ["cachedReadTokens"] = (long)f.Cached, ["cachedWriteTokens"] = (long)f.CacheWrite, ["credits"] = f.NanoAiu / 1e9,
            };
        }
        else if (_in + _out > 0)
        {
            u = new JsonObject
            {
                ["inputTokens"] = _in, ["outputTokens"] = _out, ["totalTokens"] = _in + _out,
                ["thoughtTokens"] = _reasoning, ["cachedReadTokens"] = _cacheRead, ["cachedWriteTokens"] = _cacheWrite,
            };
            if (_nanoAiu > 0) u["credits"] = _nanoAiu / 1e9;
        }
        _in = _out = _cacheRead = _cacheWrite = _reasoning = _nanoAiu = 0;
        _fusion = null;
        return u;
    }

    /// <summary>
    /// The Copilot CLI keeps only an asset id, the type and the name of an image attached to a message, never its bytes, so a
    /// replayed message can't show the picture; it says one was attached instead.
    /// </summary>
    private static string ImagePlaceholders(UserMessageData data) => string.Concat((data.Attachments ?? [])
        .OfType<AttachmentBlob>()
        .Where(blob => blob.MimeType?.StartsWith("image/", StringComparison.Ordinal) == true)
        .Select(blob => $"\n\n[Image attached: {(string.IsNullOrEmpty(blob.DisplayName) ? blob.MimeType : blob.DisplayName)}]"));

    private static Update Chunk(string kind, string text) => new(new JsonObject
    {
        ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    });

    private Update ToolStart(ToolExecutionStartData d)
    {
        var id = d.ToolCallId ?? "";
        _toolsSeen.Add(id);
        _liveToolStarts.Add(id);
        var args = Json.FromElement(d.Arguments);
        var path = PathOf(args);
        var title = d.ToolTitle ?? d.ToolName ?? "Tool";
        if (path is not null && !title.Contains(path)) title += " " + Paths.Relative(path, cwd);
        var body = new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = title,
            ["kind"] = ToolKinds.Of(d.ToolName), ["status"] = "pending", ["rawInput"] = args,
        };
        if (path is not null) body["locations"] = new JsonArray(new JsonObject { ["path"] = path });
        // A tool ACP has no kind for (an MCP tool): Fleet reads the real name from _meta.
        if ((d.McpToolName ?? (body["kind"]!.GetValue<string>() == "other" ? d.ToolName : null)) is { } real)
            body["_meta"] = new JsonObject { ["toolName"] = real };
        if (DiffOf(args, path) is { } diff) body["content"] = new JsonArray(diff);
        return new Update(body);
    }

    private IEnumerable<AgentOutput> ToolComplete(ToolExecutionCompleteData d)
    {
        var id = d.ToolCallId ?? "";
        _liveToolDones.Add(id);
        if (_toolsSeen.Add(id))
            yield return new Update(new JsonObject { ["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = "Tool", ["kind"] = "other", ["status"] = "pending" });
        var body = new JsonObject
        {
            ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["status"] = d.Success ? "completed" : "failed",
        };
        var text = d.Success ? d.Result?.Content : d.Error?.Message ?? d.Result?.Content;
        if (!string.IsNullOrEmpty(text))
            body["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } });
        if (d.Result is { } r)
            body["rawOutput"] = new JsonObject { ["content"] = r.Content, ["detailedContent"] = r.DetailedContent };
        yield return new Update(body);
    }

    private static string? PathOf(JsonNode? args) => args is JsonObject o ? o.Str("path") ?? o.Str("file_path") ?? o.Str("fileName") : null;

    /// <summary>`edit` carries old_str/new_str and `create` carries file_text: both become an ACP diff block.</summary>
    private static JsonObject? DiffOf(JsonNode? args, string? path)
    {
        if (path is null || args is not JsonObject o) return null;
        var newText = o.Str("new_str") ?? o.Str("file_text");
        if (newText is null) return null;
        return new JsonObject { ["type"] = "diff", ["path"] = path, ["oldText"] = o.Str("old_str"), ["newText"] = newText };
    }
}

public static class ToolKinds
{
    public static string Of(string? tool) => tool switch
    {
        "view" or "read" or "read_file" => "read",
        "edit" or "create" or "write" or "apply_patch" or "str_replace" or "str_replace_editor" => "edit",
        "bash" or "shell" or "powershell" or "run" => "execute",
        "grep" or "glob" or "search" or "rg" => "search",
        "web_fetch" or "web_search" => "fetch",
        _ => "other",
    };
}

public static class Paths
{
    /// <summary>Shortens an absolute path under the session folder for display.</summary>
    public static string Relative(string path, string? cwd)
    {
        if (cwd is null) return path;
        // Either separator matches either (Windows paths can arrive with '/' or '\\').
        var root = cwd.TrimEnd('/', '\\') + "/";
        var norm = path.Replace('\\', '/');
        return norm.StartsWith(root.Replace('\\', '/'), StringComparison.Ordinal) ? norm[root.Length..] : path;
    }
}
