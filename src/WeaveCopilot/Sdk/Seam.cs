using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace WeaveCopilot.Sdk;

public sealed record ModelEntry(string Id, string Name, string? Usage, string? PriceTier = null, string? Category = null);

public sealed record AuthStatus(bool Authenticated, string? AuthType, string? Host, string? Login, string? Message);

public sealed record TodoRow(string Title, string Status);

/// <summary>What a manual compaction reported.</summary>
public sealed record CompactResult(bool Success, long TokensRemoved, long MessagesRemoved);

/// <summary>A slash command the session knows; <c>Kind</c> is builtin, skill or client.</summary>
public sealed record CommandEntry(string Name, string? Description, string? Hint, string Kind);

/// <summary>What invoking a command gave: <c>prompt</c> (send <c>Text</c> to the model), <c>text</c> (show <c>Text</c>, null for nothing), or <c>unsupported</c> (needs Copilot's own terminal).</summary>
public sealed record CommandResult(string Kind, string? Text);

public sealed record AgentEntry(string Name, string? DisplayName, string? Description, string? Source);

public sealed record ImageData(string Base64, string MimeType, string? Name);

/// <summary>How a `!` shell command ended, as the CLI reports it.</summary>
public sealed record ShellResult(string? ToolCallId, bool Success, double? ExitCode, string? Output, string? Error);
/// <summary>What a background shell has printed since a cursor; <see cref="Exited"/> once it ended.</summary>
public sealed record TaskOutput(string Text, string? Cursor, bool Exited, int? ExitCode);

public delegate Task<PermissionDecision> PermissionCallback(string sessionId, PermissionRequest request);

/// <summary>An HTTP MCP server the client asked for in <c>session/new</c> or <c>session/load</c>. Headers can carry a credential: never log them.</summary>
public sealed record AcpMcpServer(string Name, string Url, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The <c>http</c> servers of an ACP <c>mcpServers</c> array (<c>{type, name, url, headers: [{name, value}]}</c>). Stdio servers and malformed entries are skipped.</summary>
    public static IReadOnlyList<AcpMcpServer> Parse(System.Text.Json.Nodes.JsonNode? servers)
    {
        var list = new List<AcpMcpServer>();
        if (servers is not System.Text.Json.Nodes.JsonArray array) return list;
        foreach (var entry in array)
        {
            if (entry?["type"]?.GetValue<string>() != "http") continue;
            if (entry["name"]?.GetValue<string>() is not { Length: > 0 } name || entry["url"]?.GetValue<string>() is not { Length: > 0 } url) continue;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry["headers"] is System.Text.Json.Nodes.JsonArray pairs)
                foreach (var h in pairs)
                    if (h?["name"]?.GetValue<string>() is { Length: > 0 } hn && h["value"]?.GetValue<string>() is { } hv) headers[hn] = hv;
            list.RemoveAll(s => s.Name == name);
            list.Add(new AcpMcpServer(name, url, headers));
        }
        return list;
    }
}

/// <summary>Small seam around one Copilot SDK session so the ACP agent is testable without the CLI.</summary>
public interface ICopilotSession : IAsyncDisposable
{
    string Id { get; }
    IDisposable Subscribe(Action<SessionEvent> handler);
    /// <summary>Sends a prompt; <paramref name="displayPrompt"/> is what the transcript shows when the model is sent more than the user typed.</summary>
    Task SendAsync(string prompt, bool immediate, IReadOnlyList<ImageData>? images = null, string? displayPrompt = null);
    /// <summary>Manual compaction; returns when it is done.</summary>
    Task<CompactResult> CompactAsync(string? instructions);
    /// <summary>The rows of the session's `todos` table, which is where Copilot keeps its todo list.</summary>
    Task<IReadOnlyList<TodoRow>> ReadTodosAsync();
    /// <summary>The session's slash commands: builtin, skill and client.</summary>
    Task<IReadOnlyList<CommandEntry>> ListCommandsAsync();
    /// <summary>Expands a command without starting a turn. Throws for an unknown name.</summary>
    Task<CommandResult> InvokeCommandAsync(string name, string? input);
    /// <summary>Runs later turns as the named custom agent; null or empty goes back to Copilot's own.</summary>
    Task SelectAgentAsync(string? name);
    Task AbortAsync();
    Task SetModelAsync(string modelId);
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync();
    /// <summary>An off-the-record question: the model sees the whole conversation, and nothing is added to it. Null when there is no answer.</summary>
    Task<string?> AskAsync(string question);
    /// <summary>Runs a command the user typed (a `!` command) in the session's folder; returns when it ends. The model sees its output on the next turn.</summary>
    Task<ShellResult> RunShellAsync(string requestId, string command);
    /// <summary>Cancels a background task (a subagent's AgentId or a shellId); false if the CLI knew no such running task.</summary>
    Task<bool> CancelTaskAsync(string id);
    /// <summary>A detached shell's output from a cursor (null: from the start). Don't use the task list: the SDK can't parse it.</summary>
    Task<TaskOutput> ReadTaskOutputAsync(string id, string? cursor);
}

/// <summary>Small seam around the Copilot client (one CLI child process).</summary>
public interface ICopilotHost : IAsyncDisposable
{
    Task StartAsync();
    Task<string?> VersionAsync();
    /// <summary>Throws if the CLI child process is gone.</summary>
    Task PingAsync();
    Task<IReadOnlyList<ModelEntry>> ListModelsAsync();
    /// <summary>Who the CLI is signed in as, asked of the CLI so it reflects a sign-in made a moment ago.</summary>
    Task<AuthStatus> AuthStatusAsync();
    Task<ICopilotSession> CreateAsync(string cwd, string? model, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null, IReadOnlyList<string>? skillDirectories = null);
    Task<ICopilotSession> ResumeAsync(string sessionId, string cwd, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null, IReadOnlyList<string>? skillDirectories = null);
    /// <summary>Custom agents Copilot finds for a folder, without a session.</summary>
    Task<IReadOnlyList<AgentEntry>> ListAgentsAsync(string cwd);
    /// <summary>Copies a session (up to, not including, the event <paramref name="toEventId"/>; all of it when null) into a new one and returns the new id.</summary>
    Task<string> ForkAsync(string sessionId, string? toEventId);
    /// <summary>Cheap create-only probe: can a session be created with the synthetic hydrafusion model?</summary>
    Task<bool> ProbeHydraFusionAsync(string cwd);
}
