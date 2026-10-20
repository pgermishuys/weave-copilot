using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveCopilot.Acp;

namespace WeaveCopilot.Sdk;

public sealed record UsageQuota(string Id, int? Entitlement, int? Remaining, double? PercentRemaining, bool Unlimited);

public sealed record CopilotUsageResult(string? Plan, string? ResetAt, string? Login, IReadOnlyList<UsageQuota> Quotas);

/// <summary>Reads the plan and quota snapshots GitHub keeps for the signed-in Copilot user.</summary>
public interface ICopilotUsage
{
    /// <summary>Null when no token could be found for <paramref name="login"/>. Throws <see cref="UsageException"/> with a plain message when GitHub says no.</summary>
    Task<CopilotUsageResult?> GetAsync(string? login, string? host, CancellationToken ct);
}

/// <summary>A failure whose message is safe to show: it never contains the token.</summary>
public sealed class UsageException(string message) : Exception(message);

/// <summary>Calls GitHub's <c>copilot_internal/user</c> with the token the Copilot CLI already holds. The token stays in this class: it is never returned, logged or put in an error.</summary>
public sealed class CopilotUsage(string home, HttpClient? http = null, string? url = null, Func<string, string?>? env = null) : ICopilotUsage
{
    public const string UrlVariable = "WEAVE_COPILOT_USAGE_URL";
    private const string DefaultUrl = "https://api.github.com/copilot_internal/user";
    private static readonly string[] QuotaIds = ["premium_interactions", "chat", "completions"];
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<CopilotUsageResult?> GetAsync(string? login, string? host, CancellationToken ct)
    {
        var token = FindToken(login, host);
        if (token is null) return null;

        var target = url ?? (Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } o ? o : DefaultUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("Authorization", "token " + token);
        request.Headers.TryAddWithoutValidation("User-Agent", "weave-copilot");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        string body;
        int status;
        try
        {
            using var response = await (http ?? Shared).SendAsync(request, ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new UsageException("Couldn't reach GitHub to read Copilot usage.");
        }
        if (status is 401 or 403) throw new UsageException($"GitHub refused the Copilot sign-in when reading usage (HTTP {status}); sign in again.");
        if (status != 200) throw new UsageException($"GitHub answered HTTP {status} when reading Copilot usage.");
        return Parse(body);
    }

    public static CopilotUsageResult Parse(string body)
    {
        JsonObject root;
        try { root = JsonNode.Parse(body) as JsonObject ?? throw new UsageException("GitHub's Copilot usage answer wasn't what Fleet expected."); }
        catch (JsonException) { throw new UsageException("GitHub's Copilot usage answer wasn't what Fleet expected."); }

        var quotas = new List<UsageQuota>();
        if (root["quota_snapshots"] is JsonObject snapshots)
            foreach (var id in QuotaIds)
                if (snapshots[id] is JsonObject q)
                    quotas.Add(new UsageQuota(id, Int(q["entitlement"]), Int(q["remaining"]), Num(q["percent_remaining"]), q["unlimited"] is JsonValue u && u.TryGetValue<bool>(out var b) && b));
        var plan = Text(root["copilot_plan"]);
        if (quotas.Count == 0 && plan is null) throw new UsageException("GitHub's Copilot usage answer wasn't what Fleet expected.");
        return new CopilotUsageResult(plan, Text(root["quota_reset_date_utc"]) ?? Text(root["quota_reset_date"]), Text(root["login"]), quotas);
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    private static int? Int(JsonNode? n) => Num(n) is { } d ? (int)Math.Round(d) : null;

    /// <summary>The same sign-in the CLI uses: a token in the environment first, then the one in its config file.</summary>
    private string? FindToken(string? login, string? host)
    {
        var getenv = env ?? Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            if (getenv(name) is { Length: > 0 } t) return t;
        try
        {
            var path = Path.Combine(home, "config.json");
            if (!File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) is not JsonObject { } config
                || config["authTokens"] is not JsonObject tokens) return null;
            var wanted = login is null ? null : (host ?? "https://github.com") + ":" + login;
            foreach (var (key, value) in tokens)
                if ((wanted is null || key.Equals(wanted, StringComparison.OrdinalIgnoreCase) || key.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase))
                    && value?["token"] is JsonValue v && v.TryGetValue<string>(out var token) && token.Length > 0) return token;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }
}
