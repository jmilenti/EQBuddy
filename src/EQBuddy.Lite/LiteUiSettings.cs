using System.IO;
using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>Lite-only UI preferences. Their own file (like lite-sync.json) because
/// settings.json is shared with the full app, whose loader drops unknown keys.</summary>
public sealed class LiteUiSettings
{
    /// <summary>Own damage breakdown expanded under the DPS line. Off by default —
    /// the summary is always there; detail is opt-in.</summary>
    public bool ShowBreakdown { get; set; }

    /// <summary>Session loot list expanded under its heading. Same opt-in default.</summary>
    public bool ShowLoot { get; set; }

    /// <summary>Mote tier list expanded under the MOTES heading. Same opt-in default.</summary>
    public bool ShowMotes { get; set; }

    /// <summary>Recent-fights list expanded under the FIGHTS heading. Same opt-in default.</summary>
    public bool ShowFights { get; set; }

    /// <summary>Spawn timers expanded. Default ON — a countdown you can't see is a
    /// countdown you camp past.</summary>
    public bool ShowSpawns { get; set; } = true;

    /// <summary>Group board expanded. Default ON — it's the panel's second headline.</summary>
    public bool ShowGroup { get; set; } = true;

    private static readonly string SettingsPath = AppPaths.File("lite-ui.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static LiteUiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<LiteUiSettings>(File.ReadAllText(SettingsPath), JsonOpts) is { } s)
                return s;
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
        }
        return new LiteUiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
        }
    }
}
