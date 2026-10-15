using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace WeaveCopilot.Acp;

/// <summary>What the mapper wants the agent to do. Pure data so the mapper is testable without the CLI.</summary>
public abstract record AgentOutput;
/// <summary>Body of a `session/update` notification.</summary>
public sealed record Update(JsonObject Body) : AgentOutput;
/// <summary>Params (minus sessionId) of the `_fleet/fusion` notification.</summary>
public sealed record Fusion(JsonObject Params) : AgentOutput;
/// <summary>The session went idle: the running prompt is over.</summary>
public sealed record Idle(bool Aborted, JsonObject? Usage) : AgentOutput;
/// <summary>The session reported an error: fail the running prompt with this text.</summary>
public sealed record Failure(string Message) : AgentOutput;

/// <summary>
/// Maps Copilot SDK session events to ACP updates. One instance per session; stateful only for
/// de-duplication (streamed ids, tool calls, fusion pass order) and per-turn token usage.
/// Live mode streams; history mode replays a finished transcript (no thoughts, no usage, no fusion).
/// </summary>
public sealed class EventMapper(string? cwd = null, bool history = false)
{
    private readonly HashSet<string> _liveMessages = [];       // message ids that already produced text live
    private readonly HashSet<string> _liveToolStarts = [];
    private readonly HashSet<string> _liveToolDones = [];
    private readonly HashSet<string> _streamedReasoning = [];
    private readonly HashSet<string> _toolsSeen = [];
    private readonly Dictionary<string, int> _phaseIndex = [];
    private long _in, _out, _cacheRead, _cacheWrite, _reasoning;

    public IEnumerable<AgentOutput> Map(SessionEvent ev)
    {
        // HydraFusion replays tool/message events at commit with a commit id. Drop a replay only when its live twin
        // was already emitted: with streaming on, an answer can reach us only on the commit copy.
        if (!history && CommitId(ev) is not null && IsLiveTwinSeen(ev)) yield break;
        if (!history && CommitId(ev) is not null && ev is AssistantUsageEvent) yield break;

        switch (ev)
        {
            case UserMessageEvent u when history && !string.IsNullOrEmpty(u.Data.Content):
                yield return Chunk("user_message_chunk", u.Data.Content);
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
                yield return ToolStart(ts.Data);
                break;
            case ToolExecutionCompleteEvent tc:
                foreach (var o in ToolComplete(tc.Data)) yield return o;
                break;
            case SessionUsageInfoEvent ui when !history:
                yield return new Update(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = ui.Data.CurrentTokens, ["size"] = ui.Data.TokenLimit });
                break;
            case AssistantUsageEvent au when !history:
                _in += au.Data.InputTokens ?? 0; _out += au.Data.OutputTokens ?? 0; _reasoning += au.Data.ReasoningTokens ?? 0;
                _cacheRead += au.Data.CacheReadTokens ?? 0; _cacheWrite += au.Data.CacheWriteTokens ?? 0;
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
            case SessionFusionResolvedEvent fr when !history:
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "resolved", ["pattern"] = fr.Data.Pattern.Value, ["model"] = fr.Data.PrimaryModel,
                    ["hint"] = fr.Data.Hint, ["status"] = "resolved", ["index"] = 0,
                });
                break;
            case AssistantFusionPhaseStartedEvent ps when !history:
                var idx = _phaseIndex.Count;
                _phaseIndex[ps.Data.PhaseId] = idx;
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_started", ["pattern"] = ps.Data.Pattern.Value, ["phase"] = ps.Data.PhaseKind.Value,
                    ["role"] = ps.Data.Role, ["model"] = ps.Data.Model, ["status"] = "running", ["index"] = idx,
                });
                break;
            case AssistantFusionPhaseCompletedEvent pc when !history:
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_completed", ["phase"] = pc.Data.PhaseKind.Value, ["role"] = pc.Data.Role, ["model"] = pc.Data.Model,
                    ["status"] = pc.Data.Status.Value, ["verdict"] = pc.Data.Verdict, ["durationMs"] = pc.Data.DurationMs,
                    ["credits"] = pc.Data.Usage is { } pu ? pu.TotalNanoAiu / 1e9 : null,
                    ["index"] = _phaseIndex.GetValueOrDefault(pc.Data.PhaseId),
                });
                break;
            case AssistantFusionPhaseFailedEvent pf when !history:
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "phase_completed", ["phase"] = pf.Data.PhaseKind.Value, ["role"] = pf.Data.Role, ["model"] = pf.Data.Model,
                    ["status"] = "failed", ["verdict"] = pf.Data.ErrorMessage ?? pf.Data.Reason, ["durationMs"] = pf.Data.DurationMs,
                    ["credits"] = pf.Data.Usage is { } fu ? fu.TotalNanoAiu / 1e9 : null,
                    ["index"] = _phaseIndex.GetValueOrDefault(pf.Data.PhaseId),
                });
                break;
            case SessionFusionCompletedEvent fc when !history:
                yield return new Fusion(new JsonObject
                {
                    ["event"] = "completed", ["pattern"] = fc.Data.Pattern.Value, ["model"] = fc.Data.FinalSourceModel, ["status"] = fc.Data.Outcome,
                    ["durationMs"] = fc.Data.DurationMs, ["totalCredits"] = fc.Data.TotalNanoAiu / 1e9, ["index"] = Math.Max(0, _phaseIndex.Count - 1),
                });
                _phaseIndex.Clear();
                break;
        }
    }

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

    private JsonObject? TakeUsage()
    {
        if (_in + _out == 0) return null;
        var u = new JsonObject
        {
            ["inputTokens"] = _in, ["outputTokens"] = _out, ["totalTokens"] = _in + _out,
            ["thoughtTokens"] = _reasoning, ["cachedReadTokens"] = _cacheRead, ["cachedWriteTokens"] = _cacheWrite,
        };
        _in = _out = _cacheRead = _cacheWrite = _reasoning = 0;
        return u;
    }

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
        var root = cwd.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    }
}
