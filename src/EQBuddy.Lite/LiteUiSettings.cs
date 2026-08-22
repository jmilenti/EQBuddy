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

    /// <summary>The fight-scope GROUP board expanded. Default ON — it's the panel's
    /// second headline. (There is no scope dropdown any more: the two boards below are
    /// each permanently one scope. "DpsScope" in old files is ignored.)</summary>
    public bool ShowGroup { get; set; } = true;

    /// <summary>The session-scope GROUP board expanded.</summary>
    public bool ShowGroup2 { get; set; } = true;

    /// <summary>Live damage feed expanded. Off by default — it's a firehose by design,
    /// and the header is always there to opt in.</summary>
    public bool ShowFeed { get; set; }

    /// <summary>GROUP board reads group sync when available (exact numbers). Off = the
    /// board always shows your own log's ~ rows, even while sync is running — sync still
    /// publishes YOUR numbers either way, this is only what you look at. The ⚙ dialog.</summary>
    public bool GroupBoardUseSync { get; set; } = true;

    /// <summary>Group members' mote hauls shown in the MOTES section. Sync is the only
    /// possible source (your log never sees anyone else's loot), so off simply hides the
    /// GROUP · motes block. The ⚙ dialog.</summary>
    public bool ShowGroupMotes { get; set; } = true;

    /// <summary>The FEED section's filters, persisted so a curated view survives a
    /// restart.</summary>
    public FeedFilters FeedFilters { get; set; } = new();

    /// <summary>Rows the FEED shows at once — the vertical half of its resize grip.</summary>
    public int FeedRows { get; set; } = 12;

    /// <summary>How many events/lines the feed's buffers hold (the ⚙ dialog). Applies
    /// to the combat buffer and the raw-log buffer alike; clamped 500–200,000 on use.</summary>
    public int FeedHistory { get; set; } = 20_000;

    /// <summary>Explicit width per section window, set by its ◢ grip; a missing key
    /// means auto (size to content). Height is deliberately NOT here: these windows
    /// size to their content, so height is content — the feed's grip maps vertical
    /// drag to FeedRows instead.</summary>
    public Dictionary<string, double> SectionWidths { get; set; } = new();

    /// <summary>Sections removed from the UI entirely (the ⚙ dialog's tick boxes) —
    /// window hidden, dock chain bridged over it. Different from the collapse toggles
    /// above, which keep the one-line heading visible.</summary>
    public List<string> HiddenSections { get; set; } = [];

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

/// <summary>What the FEED section shows. Who-toggles and kind-toggles are ANDed: a row
/// must pass one of each. CritsOnly and the Only* annotation toggles narrow further, MinDamage floors
/// the amount, and MeleeType restricts melee rows to one physical damage type.</summary>
public sealed class FeedFilters
{
    // -- who --
    public bool You { get; set; } = true;
    public bool Pet { get; set; } = true;
    public bool Group { get; set; }
    public bool Incoming { get; set; }

    // -- kind --
    public bool Melee { get; set; } = true;
    public bool Spells { get; set; } = true;
    public bool Dots { get; set; } = true;
    /// <summary>Damage-shield / automatic damage (the parser's IsAux).</summary>
    public bool DamageShields { get; set; }
    public bool Heals { get; set; }
    public bool Misses { get; set; }
    public bool Kills { get; set; } = true;
    public bool ResistsFizzles { get; set; }

    // -- narrowing --
    public bool CritsOnly { get; set; }
    /// <summary>The special-hit annotations, one toggle each. Turning any on narrows the
    /// feed to rows whose note matches one of the ENABLED kinds (they OR together) —
    /// all off means no annotation filtering at all.</summary>
    public bool OnlySlays { get; set; }
    public bool OnlyRipostes { get; set; }
    public bool OnlyCrippling { get; set; }
    /// <summary>0 = everything; otherwise hide rows below this amount.</summary>
    public int MinDamage { get; set; }

    /// <summary>Free-text search chips: a row must contain at least one of these (they
    /// OR together) anywhere in its actor, ability, target, note, kind, or crit-ness.
    /// Empty = no text filtering. In raw mode chips match the whole log line.</summary>
    public List<string> SearchTerms { get; set; } = [];

    /// <summary>The "all" button: show every raw log line — chat, emotes, system,
    /// everything — instead of the curated combat view. The who/kind pills don't apply
    /// there (they describe parsed events); chips still filter by text.</summary>
    public bool RawMode { get; set; }
    /// <summary>"all", "slash", "pierce", "blunt", or "archery" — melee rows only.</summary>
    public string MeleeType { get; set; } = "all";
}
