using System.IO;
using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>Lite-only UI preferences. Their own file (like lite-sync.json) because
/// settings.json is shared with the full app, whose loader drops unknown keys.</summary>
public sealed class LiteUiSettings
{
    /// <summary>DPS scope for every rate the panel shows — headline, breakdown, pet
    /// line, and the group board (synced and log-inferred rows alike): "fight" (current
    /// fight, or the last one when idle) or "session" (accumulating totals).</summary>
    public string DpsScope { get; set; } = "fight";

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

    /// <summary>Where in the log your last session reset happened — the log file and the
    /// byte offset reached at that moment. Replayed at the next launch so a restart
    /// resumes the session you started instead of re-reading everything you cleared.
    /// Ignored when the file no longer matches or has since been emptied.</summary>
    public string? ResetLogPath { get; set; }
    public long ResetLogOffset { get; set; }

    /// <summary>Sections torn off into their own floating windows ("motes", "loot",
    /// "fights", "spawns", "group"), restored detached at the next launch.</summary>
    public List<string> DetachedSections { get; set; } = [];

    /// <summary>Last position of each detached section window, keyed by section, [x, y].</summary>
    public Dictionary<string, double[]> SectionPositions { get; set; } = new();

    /// <summary>What each section is magnetised under: "main" for the main panel, another
    /// section's key for one of its windows, or "" for free-floating. Remembered rather
    /// than re-derived, because geometry cannot recover it: a section whose content shrank
    /// since the last run leaves the window below it sitting too far from its host to look
    /// docked, and that window would then never follow anything again. A key missing here
    /// is a settings file written before docks were saved.</summary>
    public Dictionary<string, string> SectionDocks { get; set; } = new();

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
