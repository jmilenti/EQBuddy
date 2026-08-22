using EQBuddy.Core;

namespace EQBuddy.Lite;

internal enum FeedKind { Melee, Spell, Dot, Aux, Heal, Taken, Miss, Kill, Resist, Fizzle }
internal enum FeedWho { You, Pet, Group }

/// <summary>One row of the live feed, captured at parse time with everything the
/// filters can ask about. Ability is the melee skill label or spell name; Note is the
/// log's trailing annotation (Riposte, Crippling Blow, Slay Undead, …) when present.</summary>
internal sealed record FeedEntry(DateTime Time, FeedWho Who, FeedKind Kind, string Actor,
    string Target, int Amount, string Ability, bool Crit, string? Note, bool Incoming)
{
    /// <summary>The log line this event was parsed from, message part only — what the
    /// feed actually displays. The parsed fields above stay the filters' material, so
    /// a row reads exactly as the game wrote it while still being filterable by who,
    /// kind, crit, and amount. Null only for entries captured before 1.68.1.</summary>
    public string? Raw { get; set; }

    /// <summary>Monotonic id, assigned on enqueue. The feed views render incrementally —
    /// each asks only for what arrived since the sequence it last drew — so a live feed
    /// costs one insert per new line instead of rebuilding two thousand rows.</summary>
    public long Seq { get; set; }
}

/// <summary>
/// The FEED section's engine: a rolling buffer of combat events from your own log,
/// filtered at render time so flipping a filter re-reads the recent past instead of
/// only changing what arrives next. Rides LogWatcher.Tap beside the group trackers;
/// like them it holds raw material only — presentation stays in MainWindow.
/// </summary>
internal sealed class DamageFeed
{
    /// <summary>Scrollback depth, user-set (the ⚙ dialog; 20k default is hours of the
    /// busiest AE fighting — a full day's log is ~20-30k combat events). Entries are
    /// ~300-byte records, so even 100k is ~30 MB; the working limit is the per-tick
    /// filter pass over the buffer, still comfortable at 100k. Oldest fall off first.</summary>
    private int _capacity = 20_000;

    /// <summary>Change the buffer depth, trimming immediately on a shrink.</summary>
    public void SetCapacity(int entries)
    {
        var cap = Math.Clamp(entries, 500, 200_000);
        lock (_lock)
        {
            _capacity = cap;
            Trim(_entries, cap, force: true);
            Trim(_raw, cap, force: true);
        }
    }

    /// <summary>Lists, not queues: a render walks the newest end backwards by index, and
    /// Queue.Reverse() would copy the whole buffer (up to 200k entries) to do that — once
    /// per feed window per frame. Dropping the oldest entries is a block move, so it
    /// happens in <see cref="TrimSlack"/>-sized batches rather than one item at a time.</summary>
    private const int TrimSlack = 512;

    private static void Trim<T>(List<T> buffer, int capacity, bool force = false)
    {
        var excess = buffer.Count - capacity;
        if (excess <= 0 || (!force && excess < TrimSlack)) return;
        buffer.RemoveRange(0, excess);
    }

    private readonly List<FeedEntry> _entries = [];

    /// <summary>Ids handed out to entries and raw lines alike — one counter, so a view
    /// holds a single cursor whichever buffer it happens to be reading.</summary>
    private long _seq;

    /// <summary>Entries made from the line currently being processed, waiting for that
    /// line's text. LogWatcher parses a line and fires Tap, THEN fires RawTap with the
    /// same line on the same thread — so whatever is sitting here when a raw line
    /// arrives came from it. (A line yields at most one event today; a list keeps that
    /// from being load-bearing.)</summary>
    private readonly List<FeedEntry> _awaitingRaw = [];

    /// <summary>Raw-mode buffer: every log line verbatim (message part), timestamped.
    /// MinValue marks a line whose prefix didn't parse — shown without a clock.</summary>
    private readonly List<RawLine> _raw = [];

    /// <summary>One verbatim log line in the raw-mode buffer.</summary>
    internal readonly record struct RawLine(long Seq, DateTime Time, string Text);

    private readonly object _lock = new();

    /// <summary>Current pet name, written by the UI tick, read on the watcher thread —
    /// how a third-party attacker is told apart from your own pet.</summary>
    public volatile string PetName = "";

    public void Apply(GameEvent e)
    {
        var entry = e switch
        {
            DamageDealtEvent d => new FeedEntry(d.Time, FeedWho.You,
                d.IsAux ? FeedKind.Aux : d.OverTime ? FeedKind.Dot
                    : d.Kind == DamageKind.Melee ? FeedKind.Melee : FeedKind.Spell,
                "you", d.Target, d.Amount, d.Source, d.Critical, d.Note, Incoming: false),

            // Self-damage (HP-cost casting, falls) isn't a fight — same rule Core uses.
            DamageTakenEvent { Self: false } t => new FeedEntry(t.Time, FeedWho.You,
                FeedKind.Taken, t.Attacker, "you", t.Amount, t.Ability, false, null, Incoming: true),

            HealEvent h => new FeedEntry(h.Time, FeedWho.You, FeedKind.Heal,
                h.Outgoing ? "you" : (h.Healer.Length > 0 ? h.Healer : "?"),
                h.Outgoing ? h.Target : "you", h.Amount, h.Spell, false, null, Incoming: !h.Outgoing),

            MissEvent m => new FeedEntry(m.Time, FeedWho.You, FeedKind.Miss,
                m.Outgoing ? "you" : "?", m.Outgoing ? "?" : "you", 0, "", false, null,
                Incoming: !m.Outgoing),

            KillEvent k => new FeedEntry(k.Time, WhoIs(k.Killer), FeedKind.Kill,
                k.Killer, k.Target, 0, "", false, null, Incoming: false),

            ResistEvent r => new FeedEntry(r.Time, FeedWho.You, FeedKind.Resist,
                "you", "", 0, r.Spell, false, null, Incoming: false),

            FizzleEvent z => new FeedEntry(z.Time, FeedWho.You, FeedKind.Fizzle,
                "you", "", 0, z.Spell, false, null, Incoming: false),

            ThirdMeleeEvent tm when Interesting(tm.Attacker) => new FeedEntry(tm.Time,
                WhoIs(tm.Attacker), FeedKind.Melee, tm.Attacker, tm.Target, tm.Amount,
                tm.Skill.Length > 0 ? tm.Skill : "melee", tm.Critical, tm.Note, Incoming: false),

            ThirdDotEvent td when Interesting(td.Caster) => new FeedEntry(td.Time,
                WhoIs(td.Caster), FeedKind.Dot, td.Caster, td.Target, td.Amount,
                td.Spell, td.Critical, td.Note, Incoming: false),

            ThirdSchoolEvent ts when Interesting(ts.Attacker) => new FeedEntry(ts.Time,
                WhoIs(ts.Attacker), FeedKind.Spell, ts.Attacker, ts.Target, ts.Amount,
                ts.Spell, ts.Critical, ts.Note, Incoming: false),

            _ => null,
        };
        if (entry is null) return;
        lock (_lock)
        {
            entry.Seq = ++_seq;
            _entries.Add(entry);
            _awaitingRaw.Add(entry);
            Trim(_entries, _capacity);
        }
    }

    /// <summary>Raw-mode capture, straight off LogWatcher.RawTap. The "[Sat Aug 22
    /// 17:20:01 2026] " prefix is split off here once rather than at render time.</summary>
    public void ApplyRaw(string line)
    {
        var time = DateTime.MinValue;
        var text = line;
        if (line.Length > 27 && line[0] == '[' && line[25] == ']')
        {
            if (DateTime.TryParseExact(line.AsSpan(1, 24), "ddd MMM dd HH:mm:ss yyyy",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var t))
                time = t;
            text = line[27..];
        }
        lock (_lock)
        {
            // Hand this line's text to the entry parsed from it (see _awaitingRaw), and
            // clear the slate either way — an unclaimed entry must not adopt the NEXT
            // line's text.
            foreach (var pending in _awaitingRaw) pending.Raw = text;
            _awaitingRaw.Clear();
            if (text.Length == 0) return;
            _raw.Add(new RawLine(++_seq, time, text));
            Trim(_raw, _capacity);
        }
    }

    /// <summary>Raw mode's view: newest-first lines containing ANY search term (all of
    /// them when no chips are set), at most <paramref name="max"/>, and only what arrived
    /// after <paramref name="since"/> (0 = the whole buffer). <paramref name="cursor"/>
    /// comes back as the sequence this snapshot has caught up to.</summary>
    public List<RawLine> SnapshotRaw(IReadOnlyList<string> terms, int max, long since, out long cursor)
    {
        lock (_lock)
        {
            cursor = _seq;
            var rows = new List<RawLine>();
            for (var i = _raw.Count - 1; i >= 0; i--)
            {
                var line = _raw[i];
                if (line.Seq <= since || rows.Count >= max) break;
                if (terms.Count > 0)
                {
                    var any = false;
                    foreach (var term in terms)
                        if (line.Text.Contains(term, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                    if (!any) continue;
                }
                rows.Add(line);
            }
            return rows;
        }
    }

    /// <summary>Third-party attackers worth a feed row: your pet, or something that
    /// looks like a player. Mob-on-mob and mob-on-others noise stays out.</summary>
    private bool Interesting(string attacker) =>
        IsPet(attacker) || GroupDpsTracker.LooksLikePlayer(attacker);

    private bool IsPet(string name) =>
        PetName.Length > 0 && string.Equals(name, PetName, StringComparison.OrdinalIgnoreCase);

    private FeedWho WhoIs(string actor) =>
        string.Equals(actor, "you", StringComparison.OrdinalIgnoreCase) ? FeedWho.You
        : IsPet(actor) ? FeedWho.Pet
        : FeedWho.Group;

    /// <summary>Newest-first rows passing the filters, at most <paramref name="max"/>,
    /// and only what arrived after <paramref name="since"/> (0 = the whole buffer, i.e. a
    /// full rebuild). <paramref name="cursor"/> comes back as the sequence this snapshot
    /// has caught up to — hand it back next time to be given only the newer rows.
    ///
    /// The cursor stops SHORT of an entry still waiting for its raw text (see
    /// <see cref="_awaitingRaw"/>): the watcher fires Tap and RawTap under two separate
    /// lock acquisitions, so a render landing between them would otherwise publish the row
    /// in its fallback shape and — being incremental — never redraw it.</summary>
    public List<FeedEntry> Snapshot(FeedFilters f, int max, long since, out long cursor)
    {
        lock (_lock)
        {
            var pending = long.MaxValue;
            foreach (var e in _awaitingRaw) pending = Math.Min(pending, e.Seq);
            cursor = Math.Max(since, pending == long.MaxValue ? _seq : pending - 1);

            var rows = new List<FeedEntry>();
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Seq <= since || rows.Count >= max) break;
                if (e.Seq >= pending) continue;
                if (Matches(e, f)) rows.Add(e);
            }
            return rows;
        }
    }

    internal static bool Matches(FeedEntry e, FeedFilters f)
    {
        // who — incoming rows ride their own toggle, not the actor's
        if (e.Incoming)
        {
            if (!f.Incoming) return false;
        }
        else if (e.Who switch { FeedWho.You => !f.You, FeedWho.Pet => !f.Pet, _ => !f.Group })
        {
            return false;
        }

        // kind
        var kindOn = e.Kind switch
        {
            FeedKind.Melee => f.Melee,
            FeedKind.Spell => f.Spells,
            FeedKind.Dot => f.Dots,
            FeedKind.Aux => f.DamageShields,
            FeedKind.Heal => f.Heals,
            FeedKind.Taken => true,        // gated by Incoming above
            FeedKind.Miss => f.Misses,
            FeedKind.Kill => f.Kills,
            FeedKind.Resist or FeedKind.Fizzle => f.ResistsFizzles,
            _ => false,
        };
        if (!kindOn) return false;

        // Search chips: OR within, AND against everything else. The haystack carries
        // the words a player would type — "slay" hits the note, "heal" the kind,
        // "crit" both the flag and a "(Critical)" note.
        if (f.SearchTerms is { Count: > 0 } terms)
        {
            // The haystack carries the displayed line too, so a chip matches what the
            // reader can actually see on the row.
            var hay = $"{e.Actor} {e.Ability} {e.Target} {e.Note} {e.Kind} {e.Raw}"
                + (e.Crit ? " critical" : "");
            var any = false;
            foreach (var term in terms)
                if (hay.Contains(term, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            if (!any) return false;
        }

        // narrowing — only damage rows are subject to these; a kill or resist row has
        // no amount or crit flag to judge
        var isDamage = e.Kind is FeedKind.Melee or FeedKind.Spell or FeedKind.Dot
            or FeedKind.Aux or FeedKind.Taken;
        if (isDamage)
        {
            if (f.CritsOnly && !e.Crit) return false;
            // The special-annotation toggles OR together: any on = only rows whose
            // note matches one of the enabled kinds.
            if (f.OnlySlays || f.OnlyRipostes || f.OnlyCrippling)
            {
                var note = e.Note ?? "";
                var matched =
                    (f.OnlySlays && note.Contains("Slay", StringComparison.OrdinalIgnoreCase)) ||
                    (f.OnlyRipostes && note.Contains("Riposte", StringComparison.OrdinalIgnoreCase)) ||
                    (f.OnlyCrippling && note.Contains("Crippling", StringComparison.OrdinalIgnoreCase));
                if (!matched) return false;
            }
            if (f.MinDamage > 0 && e.Amount < f.MinDamage) return false;
        }
        if (e.Kind == FeedKind.Melee && f.MeleeType != "all"
            && MeleeTypeOf(e.Ability) != f.MeleeType) return false;

        return true;
    }

    /// <summary>The physical damage type behind a melee skill label — the parser's
    /// VerbToSkill vocabulary bucketed the way EQ players talk about it.</summary>
    internal static string MeleeTypeOf(string skill) => skill switch
    {
        "Slash" or "Slice" or "Cleave" or "Rend" or "Reave" => "slash",
        "Pierce" or "Sting" or "Bite" or "Gore" or "Backstab" => "pierce",
        "Shoot" => "archery",
        _ => "blunt",   // Hit, Crush, Bash, Kick, Punch, Smash, Slam, Maul, Strike, Frenzy…
    };
}
