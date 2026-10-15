using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace WeaveCopilot.Sdk;

public sealed record ModelEntry(string Id, string Name, string? Usage);

public sealed record AuthStatus(bool Authenticated, string? AuthType, string? Host, string? Login, string? Message);

public delegate Task<PermissionDecision> PermissionCallback(string sessionId, PermissionRequest request);

/// <summary>Small seam around one Copilot SDK session so the ACP agent is testable without the CLI.</summary>
public interface ICopilotSession : IAsyncDisposable
{
    string Id { get; }
    IDisposable Subscribe(Action<SessionEvent> handler);
    Task SendAsync(string prompt, bool immediate);
    Task AbortAsync();
    Task SetModelAsync(string modelId);
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync();
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
    Task<ICopilotSession> CreateAsync(string cwd, string? model, PermissionCallback permission);
    Task<ICopilotSession> ResumeAsync(string sessionId, string cwd, PermissionCallback permission);
    /// <summary>Cheap create-only probe: can a session be created with the synthetic hydrafusion model?</summary>
    Task<bool> ProbeHydraFusionAsync(string cwd);
}
