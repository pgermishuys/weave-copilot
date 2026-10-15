using WeaveCopilot.Sdk;
using Xunit;

namespace WeaveCopilot.Tests;

public class CliResolveTests
{
    private static readonly char Sep = Path.PathSeparator;

    [Fact]
    public void Windows_finds_exe_cmd_or_bat_in_path_order()
    {
        var path = $"{Path.Combine("a", "x")}{Sep}{Path.Combine("b", "y")}";
        var present = new HashSet<string> { Path.Combine("a", "x", "copilot.cmd"), Path.Combine("b", "y", "copilot.exe") };
        Assert.Equal(Path.Combine("a", "x", "copilot.cmd"), SdkHost.FindOnPath(true, path, present.Contains));
        Assert.Equal(Path.Combine("b", "y", "copilot.exe"), SdkHost.FindOnPath(true, Path.Combine("b", "y"), present.Contains));
        Assert.Equal(Path.Combine("a", "x", "copilot.bat"), SdkHost.FindOnPath(true, Path.Combine("a", "x"), f => f.EndsWith("copilot.bat")));
    }

    [Fact]
    public void Non_windows_only_accepts_the_bare_name_and_missing_gives_null()
    {
        var dir = Path.Combine("a", "x");
        Assert.Null(SdkHost.FindOnPath(false, dir, f => f.EndsWith("copilot.exe")));
        Assert.Equal(Path.Combine(dir, "copilot"), SdkHost.FindOnPath(false, dir, f => f.EndsWith("copilot")));
        Assert.Null(SdkHost.FindOnPath(true, null, _ => true));
    }
}
