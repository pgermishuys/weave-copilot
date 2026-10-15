using System.Text.Json.Nodes;
using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class ExperimentalGuardTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "wc-guard-" + Guid.NewGuid().ToString("N"));
    private string Settings => Path.Combine(_home, "settings.json");
    public void Dispose() { if (Directory.Exists(_home)) Directory.Delete(_home, true); }

    [Fact]
    public void A_missing_settings_file_is_removed_again()
    {
        var g = new ExperimentalGuard(_home);
        g.Snapshot();
        File.WriteAllText(Settings, """{"experimental":true}""");  // what the CLI does for --experimental
        g.Restore();
        Assert.False(File.Exists(Settings));
        Assert.Empty(Directory.GetFiles(_home));
    }

    [Fact]
    public void An_existing_value_and_other_keys_survive()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Settings, """{"experimental":false,"theme":"dark"}""");
        var g = new ExperimentalGuard(_home);
        g.Snapshot();
        File.WriteAllText(Settings, """{"experimental":true,"theme":"dark","banner":"x"}""");
        g.Restore();
        var s = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.False(s["experimental"]!.GetValue<bool>());
        Assert.Equal("dark", s["theme"]!.GetValue<string>());
    }

    [Fact]
    public void A_crashed_run_is_repaired_by_the_next_start()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Settings, """{"theme":"dark"}""");
        new ExperimentalGuard(_home).Snapshot();
        File.WriteAllText(Settings, """{"experimental":true,"theme":"dark"}""");   // crash: no Restore
        var next = new ExperimentalGuard(_home);
        next.Snapshot();                                                              // repairs, then snapshots again
        Assert.Null(JsonNode.Parse(File.ReadAllText(Settings))!["experimental"]);
        next.Restore();
        Assert.Equal("dark", JsonNode.Parse(File.ReadAllText(Settings))!["theme"]!.GetValue<string>());
    }
}
