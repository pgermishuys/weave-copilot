using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace WeaveCopilot.Sdk;

/// <summary>The real host: runs the Copilot CLI as a child process over stdio (never in-process) and adapts the SDK to the seam.</summary>
public sealed class SdkHost(string cliPath, bool hydraFusion) : ICopilotHost
{
    public string CliPath { get; } = cliPath;

    private readonly CopilotClient _client = new(new CopilotClientOptions
    {
        // COPILOT_HOME is also inherited by the child through the environment.
        BaseDirectory = Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } home ? home : null,
        Connection = RuntimeConnection.ForStdio(cliPath, hydraFusion ? ["--experimental"] : null),
    });

    public static string ResolveCli(string? explicitPath)
    {
        var candidate = explicitPath ?? Environment.GetEnvironmentVariable("WEAVE_COPILOT_CLI");
        if (!string.IsNullOrEmpty(candidate))
            return File.Exists(candidate) ? candidate : throw new FileNotFoundException($"Copilot CLI not found at '{candidate}'");
        if (FindOnPath(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATH"), File.Exists) is { } found) return found;
        throw new FileNotFoundException("Copilot CLI not found: pass --copilot-cli <path>, set WEAVE_COPILOT_CLI, or put `copilot` on PATH");
    }

    /// <summary>First `copilot` on PATH: on Windows copilot.exe, copilot.cmd or copilot.bat (npm installs a .cmd shim), per directory in PATH order.</summary>
    public static string? FindOnPath(bool windows, string? path, Func<string, bool> exists)
    {
        string[] names = windows ? ["copilot.exe", "copilot.cmd", "copilot.bat"] : ["copilot"];
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
                if (Path.Combine(dir, name) is var full && exists(full)) return full;
        return null;
    }

    private readonly ExperimentalGuard? _guard = hydraFusion ? new ExperimentalGuard(ExperimentalGuard.DefaultHome()) : null;

    public Task StartAsync() { _guard?.Snapshot(); return _client.StartAsync(); }

    /// <summary>Puts the user's settings.json `experimental` value back; safe to call more than once.</summary>
    public void RestoreSettings() => _guard?.Restore();

    public async Task<string?> VersionAsync() { try { return (await _client.GetStatusAsync()).Version; } catch { return null; } }

    public async Task PingAsync() => await _client.GetStatusAsync();

    public async Task<IReadOnlyList<ModelEntry>> ListModelsAsync() =>
        (await _client.ListModelsAsync()).Select(m => new ModelEntry(m.Id, m.Name ?? m.Id, m.Billing?.Multiplier is { } x ? $"{x:0.##}x" : null)).ToList();

    public async Task<AuthStatus> AuthStatusAsync()
    {
        var s = await _client.GetAuthStatusAsync();
        return new AuthStatus(s.IsAuthenticated, s.AuthType, s.Host, s.Login, s.StatusMessage);
    }

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> Wrap(PermissionCallback cb) =>
        (req, inv) => cb(inv.SessionId, req);

    public async Task<ICopilotSession> CreateAsync(string cwd, string? model, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null)
    {
        var config = new SessionConfig { Model = model, WorkingDirectory = cwd, Streaming = true, OnPermissionRequest = Wrap(permission) };
        Gate(config);
        config.McpServers = ToSdk(mcp);
        return new SdkSession(await _client.CreateSessionAsync(config));
    }

    public async Task<ICopilotSession> ResumeAsync(string sessionId, string cwd, PermissionCallback permission, IReadOnlyList<AcpMcpServer>? mcp = null)
    {
        var config = new ResumeSessionConfig { WorkingDirectory = cwd, Streaming = true, OnPermissionRequest = Wrap(permission) };
        Gate(config);
        config.McpServers = ToSdk(mcp);
        return new SdkSession(await _client.ResumeSessionAsync(sessionId, config));
    }

    private static Dictionary<string, McpServerConfig>? ToSdk(IReadOnlyList<AcpMcpServer>? servers) =>
        servers is { Count: > 0 }
            ? servers.ToDictionary(s => s.Name, s => (McpServerConfig)new McpHttpServerConfig { Url = s.Url, Headers = s.Headers.ToDictionary(h => h.Key, h => h.Value) })
            : null;

    /// <summary>HydraFusion is opt-in: only with --hydrafusion do we turn on experimental mode and its single rollout flag.</summary>
    private void Gate(SessionConfigBase config)
    {
        if (!hydraFusion) return;
        config.EnableExperimentalMode = true;
        config.FeatureFlags = new Dictionary<string, bool> { ["HYDRAFUSION_ROLLOUT"] = true };
    }

    public async Task<string> ForkAsync(string sessionId, string? toEventId) => (await _client.Rpc.Sessions.ForkAsync(sessionId, toEventId, null)).SessionId;

    public async Task<bool> ProbeHydraFusionAsync(string cwd)
    {
        try
        {
            var config = new SessionConfig { Model = "hydrafusion", WorkingDirectory = cwd, OnPermissionRequest = (_, _) => Task.FromResult(PermissionDecision.Reject("probe")) };
            Gate(config);
            await using var s = await _client.CreateSessionAsync(config);
            var id = s.SessionId;
            await s.DisposeAsync();
            try { await _client.DeleteSessionAsync(id); } catch { /* probe leftovers are harmless */ }
            return true;
        }
        catch { return false; }
    }

    public async ValueTask DisposeAsync() { try { await _client.StopAsync(); } catch { } await _client.DisposeAsync(); RestoreSettings(); }

    private sealed class SdkSession(CopilotSession s) : ICopilotSession
    {
        public string Id => s.SessionId;
        public IDisposable Subscribe(Action<SessionEvent> handler) => s.On(handler);
        public Task SendAsync(string prompt, bool immediate, IReadOnlyList<ImageData>? images = null) =>
            s.SendAsync(new MessageOptions
            {
                Prompt = prompt,
                Mode = immediate ? "immediate" : null,
                Attachments = images is { Count: > 0 } ? [.. images.Select(i => (Attachment)new AttachmentBlob { Data = i.Base64, MimeType = i.MimeType, DisplayName = i.Name })] : null,
            });
        public async Task<IReadOnlyList<TodoRow>> ReadTodosAsync() =>
            (await s.Rpc.Plan.ReadSqlTodosAsync()).Rows.Select(r => new TodoRow(r.Title ?? r.Description ?? r.Id ?? "", r.Status ?? "pending")).ToList();
        public Task AbortAsync() => s.AbortAsync();
        public Task SetModelAsync(string modelId) => s.SetModelAsync(modelId);
        public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync() => await s.GetEventsAsync();
        public async Task<string?> AskAsync(string question) => (await s.Rpc.Ui.EphemeralQueryAsync(question)).Answer;
        public async Task<ShellResult> RunShellAsync(string requestId, string command)
        {
            var r = await s.Rpc.Shell.ExecuteUserRequestedAsync(requestId, command);
            return new ShellResult(r.ToolCallId, r.Success, r.ExitCode, r.Output, r.Error);
        }
        public ValueTask DisposeAsync() => s.DisposeAsync();
    }
}
