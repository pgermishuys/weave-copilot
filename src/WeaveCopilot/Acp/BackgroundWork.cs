using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GitHub.Copilot;

namespace WeaveCopilot.Acp;

/// <summary>A work item the session started; <see cref="Id"/> is what the CLI cancels (an AgentId or a shellId).</summary>
public sealed record WorkItem(string WorkId, string Kind, string Id, string? ToolCallId, bool Detached)
{
    public bool Ended { get; set; }
}

/// <summary>One slice of an attached shell's output, by UTF-8 byte offset.</summary>
public sealed record OutputSlice(string Text, long NextOffset, long Size, bool Truncated);

/// <summary>
/// Subagents and background shells a session started, followed from its events (the CLI's task list can't be read: its
/// Tasks.ListAsync throws on a shell's pid). Tracks AgentId to task call, the running items, and the output of attached shells,
/// which only ever arrives as events. Feeds the `_fleet/work` notifications and answers `_fleet/work/stop` and `output`.
/// </summary>
public sealed partial class BackgroundWork
{
    private const int MaxBuffer = 1 << 20;

    private readonly Dictionary<string, string> _agentCalls = [];      // AgentId -> the parent's `task` tool call
    private readonly Dictionary<string, JsonNode?> _asyncCalls = [];   // bash calls started with mode "async", until they complete
    private readonly Dictionary<string, WorkItem> _items = [];         // workId -> item
    private readonly Dictionary<string, OutputBuffer> _buffers = [];   // toolCallId -> output of an attached shell
    private readonly object _gate = new();

    public string? CallOf(string agentId) { lock (_gate) return _agentCalls.GetValueOrDefault(agentId); }

    /// <summary>The CLI id to cancel for a work item that is still running; null if unknown or ended.</summary>
    public WorkItem? Running(string workId) { lock (_gate) return _items.GetValueOrDefault(workId) is { Ended: false } i ? i : null; }

    public WorkItem? Find(string workId) { lock (_gate) return _items.GetValueOrDefault(workId); }

    /// <summary>Takes in an event. Returns the `_fleet/work` reports it causes (none for a replay: the work is long over).</summary>
    public IEnumerable<Work> Track(SessionEvent ev, bool report)
    {
        Work? w = null;
        lock (_gate)
        {
            switch (ev)
            {
                case SubagentStartedEvent { AgentId: { } agent } s when s.Data.ToolCallId is { } call:
                    _agentCalls[agent] = call;
                    if (report && _items.TryAdd(call, new WorkItem(call, "subagent", agent, call, false)))
                        w = Report("started", call, "subagent", s.Data.AgentDisplayName ?? s.Data.AgentName, s.Data.AgentDescription, call, s.Data.ExecutionMode == "background", canRead: false);
                    break;
                case SubagentCompletedEvent c when c.Data.ToolCallId is { } call:
                    w = report ? End(call, c.Data.Cancelled == true ? "cancelled" : "completed", null) : null;
                    break;
                case SubagentFailedEvent f when f.Data.ToolCallId is { } call:
                    w = report ? End(call, "error", f.Data.Error) : null;
                    break;
                case ToolExecutionStartEvent { Data: { ToolName: "bash" or "shell" or "powershell", ToolCallId: { } call } d } when report:
                    var args = Json.FromElement(d.Arguments);
                    if (args.Str("mode") != "async") break;
                    _asyncCalls[call] = args;
                    _buffers[call] = new OutputBuffer();
                    break;
                case ToolExecutionCompleteEvent { Data.ToolCallId: { } call } c when report && _asyncCalls.Remove(call, out var a):
                    var shell = ShellIdOf(c.Data.Result?.Content) ?? a.Str("shellId");
                    if (!c.Data.Success || shell is null) { _buffers.Remove(call); break; }
                    _items[shell] = new WorkItem(shell, "shell", shell, call, a?["detach"] is JsonValue dv && dv.TryGetValue<bool>(out var detach) && detach);
                    w = Report("started", shell, "shell", a.Str("description") ?? "Shell", a.Str("command"), call, true, canRead: true);
                    break;
                case ToolShellOutputEvent { Data.ToolCallId: { } call } o when report && _buffers.TryGetValue(call, out var buf):
                    buf.Append(o.Data.Text);
                    break;
                case SystemNotificationEvent { Data.Kind: SystemNotificationShellCompleted done } when report:
                    w = End(done.ShellId, done.ExitCode == 0 ? "completed" : "error", done.ExitCode == 0 ? null : $"exit {done.ExitCode}");
                    break;
                case SystemNotificationEvent { Data.Kind: SystemNotificationShellDetachedCompleted done } when report:
                    w = End(done.ShellId, "completed", null);
                    break;
            }
        }
        if (w is not null) yield return w;
    }

    /// <summary>Ends a work item once; a second end (or an end for an unknown item) is nothing.</summary>
    public Work? End(string workId, string reason, string? detail)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(workId, out var i) || i.Ended) return null;
            i.Ended = true;
            return Report("ended", workId, i.Kind, null, null, i.ToolCallId, null, null, reason, detail);
        }
    }

    public OutputSlice? Read(WorkItem item, long offset)
    {
        lock (_gate) return item.ToolCallId is { } c && _buffers.TryGetValue(c, out var b) ? b.Read(offset) : null;
    }

    private static Work Report(string ev, string workId, string kind, string? title, string? label, string? call, bool? background, bool? canRead, string? reason = null, string? detail = null) =>
        new(new JsonObject
        {
            ["event"] = ev, ["workId"] = workId, ["kind"] = kind, ["title"] = title, ["label"] = label, ["toolCallId"] = call,
            ["background"] = background, ["canStop"] = ev == "started" ? true : null, ["canReadOutput"] = canRead, ["endedReason"] = reason, ["detail"] = detail,
        });

    private static string? ShellIdOf(string? result) => result is null ? null : ShellId().Match(result) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"shellId:\s*([^\s>]+)")]
    private static partial Regex ShellId();

    /// <summary>The last <see cref="MaxBuffer"/> bytes of a shell's output; offsets count bytes since its start.</summary>
    private sealed class OutputBuffer
    {
        private readonly List<byte> _bytes = [];
        private long _dropped;

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _bytes.AddRange(Encoding.UTF8.GetBytes(text));
            // Trimmed in steps, not per chunk: a chatty dev server would otherwise move a mebibyte each line.
            if (_bytes.Count <= 2 * MaxBuffer) return;
            var over = _bytes.Count - MaxBuffer;
            _bytes.RemoveRange(0, over);
            _dropped += over;
        }

        public OutputSlice Read(long offset)
        {
            var size = _dropped + _bytes.Count;
            var from = (int)Math.Clamp(offset - _dropped, 0, _bytes.Count);
            while (from < _bytes.Count && (_bytes[from] & 0xC0) == 0x80) from++;   // never start inside a character
            return new OutputSlice(Encoding.UTF8.GetString(_bytes.GetRange(from, _bytes.Count - from).ToArray()), size, size, offset < _dropped);
        }
    }
}
