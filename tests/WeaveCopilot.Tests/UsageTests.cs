using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public sealed class UsageTests : IDisposable
{
    private const string Token = "gho_SECRETSECRETSECRET123456";
    private readonly string _home = Directory.CreateTempSubdirectory("wc-usage").FullName;
    private readonly HttpListener _http = new();
    private readonly string _url;
    private int _status = 200;
    private string _body = "{}";
    private string? _auth;
    private string? _userAgent;
    private string? _path;

    public UsageTests()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        _url = $"http://127.0.0.1:{port}/copilot_internal/user";
        _http.Prefixes.Add($"http://127.0.0.1:{port}/");
        _http.Start();
        _ = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext c;
                try { c = await _http.GetContextAsync(); } catch { return; }
                _auth = c.Request.Headers["Authorization"];
                _userAgent = c.Request.UserAgent;
                _path = c.Request.Url!.AbsolutePath;
                var bytes = Encoding.UTF8.GetBytes(_body);
                c.Response.StatusCode = _status;
                c.Response.ContentType = "application/json";
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        });
        File.WriteAllText(Path.Combine(_home, "config.json"),
            $$"""{ "authTokens": { "https://github.com:octo-demo": { "token": "{{Token}}" } } }""");
    }

    public void Dispose() { _http.Close(); try { Directory.Delete(_home, true); } catch (IOException) { } }

    private Wire Agent(FakeHost host, bool withToken = true)
    {
        var wire = new Wire();
        var usage = new CopilotUsage(_home, null, _url, name => null);
        if (!withToken) File.Delete(Path.Combine(_home, "config.json"));
        new AcpAgent(wire.Rpc, host, false, "0.1.0", _ => { }, null, usage).Attach();
        return wire;
    }

    private const string Normal = """
        {"login":"octo-demo","copilot_plan":"individual_pro","quota_reset_date":"2026-11-01",
         "quota_snapshots":{
           "premium_interactions":{"entitlement":300,"remaining":240,"percent_remaining":80.0,"unlimited":false},
           "chat":{"entitlement":0,"remaining":0,"percent_remaining":100,"unlimited":true},
           "completions":{"entitlement":0,"remaining":0,"percent_remaining":100,"unlimited":true}}}
        """;

    [Fact]
    public async Task Usage_maps_plan_reset_and_quotas_and_sends_the_token_upstream_only()
    {
        _body = Normal;
        var wire = Agent(new FakeHost());
        var r = await wire.ResultAsync("_fleet/usage", new JsonObject());
        Assert.True(r["authenticated"]!.GetValue<bool>());
        Assert.Equal("octo-demo", r.Str("login"));
        Assert.Equal("individual_pro", r.Str("plan"));
        Assert.Equal("2026-11-01", r.Str("resetAt"));
        var q = (JsonArray)r["quotas"]!;
        Assert.Equal(["premium_interactions", "chat", "completions"], q.Select(x => x!.Str("id")));
        Assert.Equal(300, q[0]!["entitlement"]!.GetValue<int>());
        Assert.Equal(240, q[0]!["remaining"]!.GetValue<int>());
        Assert.Equal(80.0, q[0]!["percentRemaining"]!.GetValue<double>());
        Assert.False(q[0]!["unlimited"]!.GetValue<bool>());
        Assert.True(q[1]!["unlimited"]!.GetValue<bool>());
        Assert.Equal("token " + Token, _auth);
        Assert.False(string.IsNullOrEmpty(_userAgent));
        Assert.Equal("/copilot_internal/user", _path);
        Assert.DoesNotContain(Token, r.ToJsonString());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Unlimited_quotas_without_numbers_come_back_as_null_numbers()
    {
        _body = """{"copilot_plan":"business","quota_snapshots":{"chat":{"unlimited":true}}}""";
        var wire = Agent(new FakeHost());
        var r = await wire.ResultAsync("_fleet/usage", new JsonObject());
        var chat = ((JsonArray)r["quotas"]!).Single()!;
        Assert.True(chat["unlimited"]!.GetValue<bool>());
        Assert.Null(chat["entitlement"]);
        Assert.Null(chat["remaining"]);
        Assert.Equal("octo-demo", r.Str("login"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task Not_signed_in_answers_authenticated_false_without_calling_github()
    {
        _auth = null;
        var host = new FakeHost { Auth = new AuthStatus(false, null, null, null, "Not signed in") };
        var wire = Agent(host);
        var r = await wire.ResultAsync("_fleet/usage", new JsonObject());
        Assert.False(r["authenticated"]!.GetValue<bool>());
        Assert.Empty((JsonArray)r["quotas"]!);
        Assert.Null(_auth);
        await wire.CloseAsync();
    }

    [Theory]
    [InlineData(401, "refused")]
    [InlineData(500, "HTTP 500")]
    public async Task An_upstream_failure_is_an_error_with_a_plain_message_and_no_token(int status, string expected)
    {
        _status = status;
        var wire = Agent(new FakeHost());
        var reply = await wire.CallAsync("_fleet/usage", new JsonObject());
        var message = reply["error"]!.Str("message")!;
        Assert.Contains(expected, message);
        Assert.DoesNotContain(Token, reply.ToJsonString());
        await wire.CloseAsync();
    }

    [Fact]
    public async Task An_answer_of_the_wrong_shape_is_an_error()
    {
        _body = "[1,2]";
        var wire = Agent(new FakeHost());
        var reply = await wire.CallAsync("_fleet/usage", new JsonObject());
        Assert.Contains("expected", reply["error"]!.Str("message"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task A_token_that_cannot_be_found_is_an_error_not_a_guess()
    {
        var wire = Agent(new FakeHost(), withToken: false);
        var reply = await wire.CallAsync("_fleet/usage", new JsonObject());
        Assert.Contains("COPILOT_GITHUB_TOKEN", reply["error"]!.Str("message"));
        await wire.CloseAsync();
    }

    [Fact]
    public async Task The_address_can_be_replaced_with_an_environment_variable()
    {
        _body = Normal;
        var usage = new CopilotUsage(_home, null, null, name => null);
        Environment.SetEnvironmentVariable(CopilotUsage.UrlVariable, _url);
        try { Assert.Equal("individual_pro", (await usage.GetAsync("octo-demo", "https://github.com", default))!.Plan); }
        finally { Environment.SetEnvironmentVariable(CopilotUsage.UrlVariable, null); }
    }

    private const string Enterprise = """
        {"copilot_plan":"enterprise","quota_reset_date":"2026-11-01","quota_reset_date_utc":"2026-11-01T00:00:00.000Z","token_based_billing":true,"quota_snapshots":{
         "chat":{"percent_remaining":100.0,"quota_id":"chat","unlimited":true,"has_quota":true,"token_based_billing":true,"credits_used":0,"remaining":0,"entitlement":0},
         "completions":{"percent_remaining":100.0,"quota_id":"completions","unlimited":true,"has_quota":true,"token_based_billing":true,"credits_used":0,"remaining":0,"entitlement":0},
         "premium_interactions":{"percent_remaining":100.0,"quota_id":"premium_interactions","unlimited":true,"has_quota":false,"token_based_billing":true,"credits_used":307846,"remaining":0,"entitlement":0}}}
        """;

    [Fact]
    public void Parse_reads_credits_used_has_quota_and_token_based_billing()
    {
        var r = CopilotUsage.Parse(Enterprise);
        var premium = r.Quotas.Single(x => x.Id == "premium_interactions");
        Assert.Equal(307846, premium.CreditsUsed);
        Assert.False(premium.HasQuota);
        Assert.True(premium.TokenBasedBilling);
        var chat = r.Quotas.Single(x => x.Id == "chat");
        Assert.Equal(0, chat.CreditsUsed);
        Assert.True(chat.HasQuota);
    }

    [Fact]
    public void Parse_gives_nulls_for_an_older_answer_and_bad_credits()
    {
        var old = CopilotUsage.Parse(Normal).Quotas;
        Assert.All(old, x => { Assert.Null(x.CreditsUsed); Assert.Null(x.HasQuota); Assert.Null(x.TokenBasedBilling); });
        var bad = CopilotUsage.Parse("""{"quota_snapshots":{"chat":{"credits_used":-1,"has_quota":"yes"},"completions":{"credits_used":"5"}}}""").Quotas;
        Assert.All(bad, x => { Assert.Null(x.CreditsUsed); Assert.Null(x.HasQuota); });
    }

    [Fact]
    public void Parse_falls_back_to_the_top_level_token_based_billing()
    {
        var r = CopilotUsage.Parse("""{"token_based_billing":true,"quota_snapshots":{"chat":{"unlimited":true},"completions":{"token_based_billing":false}}}""");
        Assert.True(r.Quotas.Single(x => x.Id == "chat").TokenBasedBilling);
        Assert.False(r.Quotas.Single(x => x.Id == "completions").TokenBasedBilling);
    }

    [Fact]
    public async Task Usage_through_the_agent_carries_the_credit_facts_and_null_when_absent()
    {
        _body = Enterprise;
        var wire = Agent(new FakeHost());
        var q = (JsonArray)(await wire.ResultAsync("_fleet/usage", new JsonObject()))["quotas"]!;
        var premium = q.Single(x => x!.Str("id") == "premium_interactions")!;
        Assert.Equal(307846, premium["creditsUsed"]!.GetValue<double>());
        Assert.False(premium["hasQuota"]!.GetValue<bool>());
        Assert.True(premium["tokenBasedBilling"]!.GetValue<bool>());
        await wire.CloseAsync();

        _body = Normal;
        wire = Agent(new FakeHost());
        var old = ((JsonArray)(await wire.ResultAsync("_fleet/usage", new JsonObject()))["quotas"]!)[0]!.AsObject();
        Assert.Null(old["creditsUsed"]);
        Assert.Null(old["hasQuota"]);
        Assert.Null(old["tokenBasedBilling"]);
        await wire.CloseAsync();
    }
}
