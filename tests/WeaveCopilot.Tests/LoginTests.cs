using System.Diagnostics;
using System.Text.Json.Nodes;
using WeaveCopilot.Acp;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public sealed class LoginTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("wc-login").FullName;

    public void Dispose() { try { Directory.Delete(_home, true); } catch (IOException) { } }

    /// <summary>A stand-in for the CLI: a script that prints the device-code lines, then waits as the real one does.</summary>
    private string FakeCli(string body)
    {
        var path = Path.Combine(_home, "fake-copilot-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void The_code_and_page_are_read_from_the_cli_output()
    {
        Assert.Equal(("https://github.com/login/device", "3911-1D98"),
            CopilotLogin.ParseCodeLine("To authenticate, visit https://github.com/login/device and enter code 3911-1D98")!.Value);
        Assert.Null(CopilotLogin.ParseCodeLine("Waiting for authorization..."));
    }

    [Fact]
    public async Task A_sign_in_that_is_cancelled_stops_the_cli_and_leaves_no_sign_in_behind()
    {
        if (OperatingSystem.IsWindows()) return;
        var cli = FakeCli("echo 'To authenticate, visit https://github.com/login/device and enter code AB12-CD34'\necho 'Waiting for authorization...'\nexec sleep 60");
        using var login = new CopilotLogin(cli, _home);
        var a = await login.StartAsync(null, default);
        Assert.Equal("AB12-CD34", a.Code);
        Assert.Equal("pending", login.Get(a.Id).State);
        login.Cancel(a.Id);
        await Until(() => login.Get(a.Id).State == "gone");
        Assert.False(File.Exists(Path.Combine(_home, "config.json")));
    }

    [Fact]
    public async Task A_sign_in_the_cli_finishes_reads_complete_and_one_it_refuses_reads_failed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var done = new CopilotLogin(FakeCli("echo 'visit https://github.com/login/device and enter code AAAA-BBBB'\nsleep 1\nexit 0"), _home);
        var a = await done.StartAsync(null, default);
        await Until(() => done.Get(a.Id).State == "complete");

        using var refused = new CopilotLogin(FakeCli("echo 'visit https://github.com/login/device and enter code AAAA-BBBB'\nsleep 1\necho 'Authorization was denied.'\nexit 1"), _home);
        var b = await refused.StartAsync(null, default);
        await Until(() => refused.Get(b.Id).State == "failed");
        Assert.Equal("Authorization was denied.", refused.Get(b.Id).Message);
    }

    [Fact]
    public async Task A_cli_that_never_shows_a_code_fails_the_start()
    {
        if (OperatingSystem.IsWindows()) return;
        using var login = new CopilotLogin(FakeCli("exit 3"), _home);
        await Assert.ThrowsAsync<InvalidOperationException>(() => login.StartAsync(null, default));
    }

    [Fact]
    public void Signing_out_removes_only_that_login_and_keeps_the_rest_of_the_config()
    {
        var path = Path.Combine(_home, "config.json");
        File.WriteAllText(path, """
            // This file is managed automatically.
            {
              "appTipShown": true,
              "authTokens": { "https://github.com:octo-demo": { "token": "x" }, "https://github.com:octo-demo:github": { "token": "x" }, "https://github.com:other": { "token": "x" } },
              "lastLoggedInUser": { "host": "https://github.com", "login": "octo-demo" },
              "loggedInUsers": [ { "host": "https://github.com", "login": "octo-demo" }, { "host": "https://github.com", "login": "other" } ]
            }
            """);
        using var login = new CopilotLogin("unused", _home);
        Assert.True(login.SignOut("octo-demo"));
        var text = File.ReadAllText(path);
        Assert.StartsWith("// This file is managed automatically.", text);
        var config = (JsonObject)JsonNode.Parse(text, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
        Assert.True(config["appTipShown"]!.GetValue<bool>());
        Assert.Null(config["lastLoggedInUser"]);
        Assert.Equal("other", ((JsonArray)config["loggedInUsers"]!).Single()!.Str("login"));
        Assert.Equal(["https://github.com:other"], ((JsonObject)config["authTokens"]!).Select(t => t.Key));
        Assert.False(login.SignOut("octo-demo"));
    }

    [Fact]
    public async Task The_agent_reports_who_is_signed_in_without_the_token_and_refuses_to_sign_out_an_environment_token()
    {
        var wire = new Wire();
        var host = new FakeHost();
        using var login = new CopilotLogin("unused", _home);
        new AcpAgent(wire.Rpc, host, false, "0.1.0", _ => { }, login).Attach();
        var status = await wire.ResultAsync("_fleet/auth/status", new JsonObject());
        Assert.True(status["authenticated"]!.GetValue<bool>());
        Assert.Equal("octo-demo", status.Str("login"));
        Assert.DoesNotContain("token", status.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        host.Auth = new AuthStatus(true, "env", "https://github.com", "octo-demo", null);
        var refused = await wire.CallAsync("_fleet/auth/logout", new JsonObject());
        Assert.Contains("environment variable", refused["error"]!.Str("message"));
        await wire.CloseAsync();
    }

    private static async Task Until(Func<bool> done)
    {
        var stop = Stopwatch.StartNew();
        while (!done()) { Assert.True(stop.Elapsed < TimeSpan.FromSeconds(10), "timed out"); await Task.Delay(50); }
    }
}
