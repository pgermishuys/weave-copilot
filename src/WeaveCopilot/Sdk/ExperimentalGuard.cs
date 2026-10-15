using System.Text.Json.Nodes;

namespace WeaveCopilot.Sdk;

/// <summary>
/// The Copilot CLI persists `"experimental": true` to settings.json when started with `--experimental`, and HydraFusion
/// needs that flag. So with --hydrafusion we remember the user's original value, restore it on exit, and also on the
/// next start if we crashed (a small backup file in the same folder marks an unrestored run).
/// </summary>
public sealed class ExperimentalGuard(string home)
{
    private string Settings => Path.Combine(home, "settings.json");
    private string Backup => Path.Combine(home, ".weave-copilot-experimental-backup.json");

    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

    /// <summary>Call before the CLI starts. Restores a leftover backup from a crashed run first, then snapshots.</summary>
    public void Snapshot()
    {
        Restore();
        Directory.CreateDirectory(home);
        var original = ReadSettings();
        var backup = new JsonObject { ["fileExisted"] = File.Exists(Settings), ["hadKey"] = original?.ContainsKey("experimental") == true, ["value"] = original?["experimental"]?.DeepClone() };
        File.WriteAllText(Backup, backup.ToJsonString());
    }

    /// <summary>Puts the user's `experimental` value back (and removes a settings file we caused) and deletes the backup.</summary>
    public void Restore()
    {
        if (!File.Exists(Backup)) return;
        try
        {
            var backup = JsonNode.Parse(File.ReadAllText(Backup)) as JsonObject;
            var settings = ReadSettings() ?? new JsonObject();
            if (backup?["hadKey"]?.GetValue<bool>() == true) settings["experimental"] = backup["value"]?.DeepClone();
            else settings.Remove("experimental");
            if (settings.Count == 0 && backup?["fileExisted"]?.GetValue<bool>() != true) File.Delete(Settings);
            else File.WriteAllText(Settings, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            File.Delete(Backup);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { /* leave the backup for the next start */ }
    }

    private JsonObject? ReadSettings()
    {
        try { return File.Exists(Settings) ? JsonNode.Parse(File.ReadAllText(Settings)) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
