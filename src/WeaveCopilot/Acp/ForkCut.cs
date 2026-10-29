using GitHub.Copilot;

namespace WeaveCopilot.Acp;

/// <summary>The newest Fleet message a fork keeps: keyed like a replayed message, plus how far into it the copy reaches.</summary>
public sealed record ForkBoundary(string Id, string At, int Offset);

public static class ForkCut
{
    /// <summary>
    /// The last Fleet message among the events before <paramref name="cutId"/> (all of them when null): a user message
    /// (offset 1 once the agent started replying to it, else 0) or a `!` command (offset 0). Null when there is none.
    /// </summary>
    public static ForkBoundary? Boundary(IEnumerable<SessionEvent> events, string? cutId)
    {
        ForkBoundary? last = null;
        var onUser = false;
        foreach (var ev in events)
        {
            if (cutId is not null && ev.Id.ToString() == cutId) break;
            switch (ev)
            {
                case UserMessageEvent u when !string.IsNullOrEmpty(u.Data.Content):
                    last = new ForkBoundary(u.Id.ToString(), u.Timestamp.ToString("O"), 0);
                    onUser = true;
                    break;
                case ToolUserRequestedEvent { Data.ToolCallId: { } call } ur:
                    last = new ForkBoundary(call, ur.Timestamp.ToString("O"), 0);
                    onUser = false;
                    break;
                case AssistantMessageEvent { Data.Content: { Length: > 0 } } or ToolExecutionStartEvent when onUser && last is not null:
                    last = last with { Offset = 1 };
                    break;
            }
        }
        return last;
    }

}
