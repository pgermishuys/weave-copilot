using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WeaveCopilot.Sdk;

/// <summary>A device-code sign-in the Copilot CLI started: the page to open and the code to enter there.</summary>
public sealed record LoginAttempt(string Id, string Url, string Code, DateTimeOffset ExpiresAt);

/// <summary>Where a sign-in is: <c>pending</c>, <c>complete</c>, <c>failed</c> (with why) or <c>gone</c> (cancelled, or never known).</summary>
public sealed record LoginProgress(string State, string? Message = null);

/// <summary>Signs in and out with GitHub's own Copilot CLI. This process never sees the token: the CLI writes it where it keeps its sign-in.</summary>
public interface ICopilotLogin : IDisposable
{
    Task<LoginAttempt> StartAsync(string? host, CancellationToken ct);
    LoginProgress Get(string attemptId);
    void Cancel(string attemptId);

    /// <summary>Removes the CLI's stored sign-in for <paramref name="login"/> (all of them when null); false when there was none to remove.</summary>
    bool SignOut(string? login);
}

/// <summary>Runs <c>copilot login --device-code</c> as a child process and reads the code and page from its output.</summary>
public sealed partial class CopilotLogin(string cliPath, string home, TimeSpan? codeTimeout = null) : ICopilotLogin
{
    // GitHub's device codes last 15 minutes; the CLI polls for that long.
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private sealed class Run(Process process)
    {
        public Process Process { get; } = process;
        public TaskCompletionSource<(string Url, string Code)> Code { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile string State = "pending";
        public string? Message;
        public bool Cancelled;
        public string Last = "";
        public DateTimeOffset Expires;
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new();

    [GeneratedRegex(@"visit\s+(https?://\S+)\s+and enter(?: the)? code\s+([A-Z0-9]{4}-[A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex CodeLine();

    /// <summary>Finds the page and code in one line of the CLI's output; the sign-in token never appears in it.</summary>
    public static (string Url, string Code)? ParseCodeLine(string line)
        => CodeLine().Match(line) is { Success: true } m ? (m.Groups[1].Value.TrimEnd('.', ','), m.Groups[2].Value.ToUpperInvariant()) : null;

    public async Task<LoginAttempt> StartAsync(string? host, CancellationToken ct)
    {
        // One sign-in at a time: a second start replaces the first.
        foreach (var earlier in _runs.Keys) Cancel(earlier);

        var start = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("login");
        start.ArgumentList.Add("--device-code");
        if (!string.IsNullOrWhiteSpace(host)) { start.ArgumentList.Add("--host"); start.ArgumentList.Add(host); }
        // A token in the environment would make `login` reuse it instead of asking; the sign-in here is the CLI's own.
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) start.Environment.Remove(name);
        start.Environment["COPILOT_HOME"] = home;
        // The CLI tries to open a browser on this machine; Fleet shows the page itself, so don't.
        start.Environment["BROWSER"] = "true";

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var run = new Run(process);
        void Line(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            run.Last = line.Trim();
            if (ParseCodeLine(line) is { } found) run.Code.TrySetResult(found);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Exited += (_, _) =>
        {
            // Let the last output lines arrive before reading the exit code.
            try { process.WaitForExit(); } catch (InvalidOperationException) { }
            if (run.Cancelled) run.State = "gone";
            else if (process.ExitCode == 0) run.State = "complete";
            else { run.Message = SafeLine(run.Last) ?? "The Copilot CLI's sign-in didn't finish."; run.State = "failed"; }
            run.Code.TrySetException(new InvalidOperationException(run.Message ?? "The Copilot CLI ended before showing a code."));
        };
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var id = Guid.NewGuid().ToString("N");
        run.Expires = DateTimeOffset.UtcNow + Lifetime;
        _runs[id] = run;
        try
        {
            var (url, code) = await run.Code.Task.WaitAsync(codeTimeout ?? TimeSpan.FromSeconds(30), ct);
            return new LoginAttempt(id, url, code, run.Expires);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            Cancel(id);
            _runs.TryRemove(id, out _);
            throw e is TimeoutException ? new InvalidOperationException("The Copilot CLI didn't show a sign-in code.") : e;
        }
        catch { _runs.TryRemove(id, out _); throw; }
    }

    // A failure line quoting a token would be unfortunate; the CLI doesn't, but never pass on anything token-shaped.
    private static string? SafeLine(string line)
        => line.Length == 0 || Regex.IsMatch(line, @"(gh[opsur]_|github_pat_)[A-Za-z0-9_]+") ? null : line.Length > 300 ? line[..300] : line;

    public LoginProgress Get(string attemptId)
    {
        if (!_runs.TryGetValue(attemptId, out var run)) return new LoginProgress("gone");
        if (run.State == "pending" && DateTimeOffset.UtcNow > run.Expires) { Cancel(attemptId); return new LoginProgress("expired"); }
        return new LoginProgress(run.State, run.Message);
    }

    public void Cancel(string attemptId)
    {
        if (!_runs.TryGetValue(attemptId, out var run)) return;
        run.Cancelled = true;
        try { if (!run.Process.HasExited) run.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    public bool SignOut(string? login)
    {
        var path = Path.Combine(home, "config.json");
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        if (JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip }) is not JsonObject config) return false;

        var removed = false;
        if (config["loggedInUsers"] is JsonArray users)
            for (var i = users.Count - 1; i >= 0; i--)
                if (Matches(users[i]?["login"], login)) { users.RemoveAt(i); removed = true; }
        if (config["lastLoggedInUser"] is JsonObject last && Matches(last["login"], login)) { config.Remove("lastLoggedInUser"); removed = true; }
        if (config["authTokens"] is JsonObject tokens)
            foreach (var key in tokens.Select(t => t.Key).ToList())
                if (login is null || key.Split(':') is [.., var name] && name.Equals(login, StringComparison.OrdinalIgnoreCase)
                    || key.Contains($":{login}:", StringComparison.OrdinalIgnoreCase))
                { tokens.Remove(key); removed = true; }
        if (!removed) return false;

        // The CLI's own header comments stay; the file is replaced in one move so a reader never sees half of it.
        var header = string.Concat(text.Split('\n').TakeWhile(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r') + "\n"));
        var temp = path + ".weave-copilot.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(header + config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private static bool Matches(JsonNode? value, string? login)
        => login is null || value is JsonValue v && v.TryGetValue<string>(out var s) && s.Equals(login, StringComparison.OrdinalIgnoreCase);

    public void Dispose() { foreach (var id in _runs.Keys) Cancel(id); }
}
