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
    /// second headline. (There is no scope dropdown: the headline is always the
    /// current fight, and the two GROUP boards are each permanently one scope.
    /// "DpsScope" in old files is ignored.)</summary>
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

    /// <summary>LEGACY (pre-1.68): the single FEED's filters and rows. Still read once
    /// to seed <see cref="FeedPanes"/> on first run of a multi-feed build; the panes
    /// list is the live model after that.</summary>
    public FeedFilters FeedFilters { get; set; } = new();
    public int FeedRows { get; set; } = 12;

    /// <summary>Every FEED window: the original plus any the user spawned with the +
    /// on a feed heading. One entry per window — filters, viewport rows, collapse
    /// state — keyed by its section key ("feed", "feed2", …; keys are never renumbered
    /// so widths/docks saved under them stay attached to the right window).</summary>
    public List<FeedPane> FeedPanes { get; set; } = [];

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

    /// <summary>Audio cues (the ⚙ dialog): "off", "sound", or "voice". Voice falls back
    /// to the cue's sound on a machine with no voice. These three MODE keys shipped in
    /// 1.73 as plain strings and stay strings — changing their shape would fail to
    /// deserialize an existing file, and LiteUiSettings.Load answers a parse failure by
    /// handing back defaults, i.e. silently wiping every other setting too.</summary>
    public string CuePetBreak { get; set; } = "off";
    public string CueMezBreak { get; set; } = "off";
    public string CueInvisBreak { get; set; } = "off";

    /// <summary>Which alert sound each cue plays, from the palette shared with the full
    /// app (<c>AlertSoundCatalog.Names</c>). A hand-edited path also works.</summary>
    public string CuePetSound { get; set; } = "Chimes";
    public string CueMezSound { get; set; } = "Exclamation";
    public string CueInvisSound { get; set; } = "Notify";

    /// <summary>What the voice SAYS. Editable because the Windows voice reads "mez" as
    /// "may" — spelling it "mezz" fixes it, and the same trick handles any other word
    /// the voice mangles. Empty falls back to "alert".</summary>
    public string CuePetPhrase { get; set; } = "pet break";
    public string CueMezPhrase { get; set; } = "mezz break";
    public string CueInvisPhrase { get; set; } = "invis break";

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

    /// <summary>Which side of its host a section is docked on: "right" or "left" for the
    /// windows that form a second column, absent for the ordinary under-the-stack dock
    /// (which is every dock a file written before 1.69 can describe).</summary>
    public Dictionary<string, string> SectionDockSides { get; set; } = new();

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

/// <summary>One FEED pane's persisted state — a window of its own, or a tab inside
/// another pane's window. Rows is the viewport height in text rows (the vertical half of
/// the ◢ grip); Show is the ▸/▾ collapse toggle of the WINDOW, so only a host pane's
/// copy of it is read.</summary>
public sealed class FeedPane
{
    public string Key { get; set; } = "feed";
    public FeedFilters Filters { get; set; } = new();
    public int Rows { get; set; } = 12;
    public bool Show { get; set; } = true;

    /// <summary>The pane whose WINDOW draws this one, as a tab. Empty (or its own key)
    /// means it is a window in its own right — the only case before 1.70. A host is
    /// always a pane that hosts itself, so tabs never chain.</summary>
    public string Host { get; set; } = "";

    /// <summary>Position among its host's tabs, low to high.</summary>
    public int Order { get; set; }

    /// <summary>Closed by the user, but REMEMBERED: filters, colours, and size stay here
    /// so "reopen closed feed" brings back the window that was tuned, not a fresh one.
    /// Closing used to delete the pane outright, which threw the tuning away.</summary>
    public bool Closed { get; set; }

    /// <summary>Per-window row colours (see <see cref="FeedColors"/>).</summary>
    public FeedColors Colors { get; set; } = new();

    /// <summary>Lay the feed out like a conversation: rows for damage coming AT you hug
    /// the right edge, everything you and yours do stays left, and the side changing
    /// leaves a gap. Off by default — it trades the timestamp column for legibility of
    /// who did what to whom, which is a taste.</summary>
    public bool SplitSides { get; set; }

    /// <summary>The tab's label. Null derives "FEED" / "FEED 2"… from the key; set by
    /// right-click ▸ Rename.</summary>
    public string? Title { get; set; }

    /// <summary>Row text size for the WINDOW this pane names (like Rows, it is a
    /// window-level property — tabs sharing a window share it, or the window would
    /// resize on every tab click). Read off the HOST pane only.</summary>
    public double FontSize { get; set; } = 11;

    /// <summary>Row typeface, a window property like <see cref="FontSize"/>. "Consolas"
    /// (the default, columns line up) or "Arial" (what the game's own chat window uses —
    /// the "Classic EQ" choice). Any installed family name works if hand-edited.</summary>
    public string FontFamily { get; set; } = "Consolas";

    /// <summary>Everything a brand-new pane starts with — one place, so the + button and
    /// "reset filters" cannot drift apart.</summary>
    public static FeedFilters DefaultFilters() => new();

    public static FeedColors DefaultColors() => new();
}

/// <summary>Row colours for one feed window, as #RRGGBB strings so the file stays
/// hand-editable. Unset or unparseable falls back to the default, which is what these
/// defaults are: the palette the feed always used, plus the spell/ability pair added in
/// 1.70. Kept as strings rather than brushes because this type is serialised.</summary>
public sealed class FeedColors
{
    /// <summary>Your own damage.</summary>
    public string You { get; set; } = "#CFE3F5";
    /// <summary>Your pet's damage.</summary>
    public string Pet { get; set; } = "#8FD4C8";
    /// <summary>Other players near you.</summary>
    public string Group { get; set; } = "#B9A7E8";
    /// <summary>Damage you take.</summary>
    public string Incoming { get; set; } = "#E89C9C";
    /// <summary>Heals, cast and received.</summary>
    public string Heal { get; set; } = "#8BE28B";
    /// <summary>Critical hits (overrides the who-colour).</summary>
    public string Crit { get; set; } = "#E8CE9C";
    /// <summary>Killing blows.</summary>
    public string Kill { get; set; } = "#D9C46B";
    /// <summary>Spell, DoT, and proc/damage-shield lines — the gold base.</summary>
    public string Spell { get; set; } = "#E8B24A";
    /// <summary>The ability, spell, or item named INSIDE a row, picked out of the line.</summary>
    public string Ability { get; set; } = "#FF8FC7";
    /// <summary>Casting chatter: begin casting, interrupted, concentration, buffs fading.</summary>
    public string Cast { get; set; } = "#9FB6D0";
    /// <summary>Everything else the log wrote — chat, loot, xp, zone lines.</summary>
    public string Other { get; set; } = "#78838F";
    /// <summary>The feed's own per-kill damage summaries.</summary>
    public string Summary { get; set; } = "#7FD9E8";
    /// <summary>Experience, AA, levels, skill-ups, faction — drawn BOLD as well: a ding
    /// is a headline, not a log line. The yellow, and the three below, match the game's
    /// own chat colours (user screenshot, 2026-08-23).</summary>
    public string Xp { get; set; } = "#F2E33D";
    /// <summary>Loot and sold-loot lines — the game draws these blue.</summary>
    public string Loot { get; set; } = "#4A8CFF";
    /// <summary>Corpse coin — the game draws money green.</summary>
    public string Money { get; set; } = "#33CC33";
    /// <summary>Auto attack on/off, stance and invocation changes — the game draws the
    /// stance lines in the same blue family.</summary>
    public string Attack { get; set; } = "#4A8CFF";
    /// <summary>Misses, resists, fizzles — and every row's timestamp.</summary>
    public string Dim { get; set; } = "#7B8794";
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
    /// <summary>Casting chatter — "You begin casting X", interrupts, "You regain your
    /// concentration", buffs wearing off. On by default: these are lines about what YOU
    /// are doing, and before 1.70 the combat view could not show them at all.</summary>
    public bool Casts { get; set; } = true;
    /// <summary>"Auto attack is on/off.", stance and invocation changes. On by default —
    /// two short lines per pull that say what state you flipped into.</summary>
    public bool Attack { get; set; } = true;
    /// <summary>Loot, corpse coin, vendor sales, crafting.</summary>
    public bool Loot { get; set; }
    /// <summary>Experience, AA, levels, skill-ups, faction.</summary>
    public bool Xp { get; set; }
    /// <summary>Zone changes and /loc lines.</summary>
    public bool Zone { get; set; }
    /// <summary>Tells, says, shouts, channel chat, auctions.</summary>
    public bool Chat { get; set; }
    /// <summary>Every line still left: emotes, mob flavor, system messages. Off by
    /// default — but its existence is the guarantee that nothing is dropped, only
    /// filtered.</summary>
    public bool Other { get; set; }

    // -- the feed's own summary rows, printed under a mob's death --
    /// <summary>Your damage to the mob that just died.</summary>
    public bool SummaryYou { get; set; } = true;
    /// <summary>Your pet's damage to it.</summary>
    public bool SummaryPet { get; set; } = true;
    /// <summary>Everyone else's damage to it.</summary>
    public bool SummaryGroup { get; set; } = true;

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
