using EQBuddy.Core;

namespace EQBuddy.Lite;

internal enum FeedKind { Melee, Spell, Dot, Aux, Heal, Taken, Miss, Kill, Resist, Fizzle }
internal enum FeedWho { You, Pet, Group }

/// <summary>One row of the live feed, captured at parse time with everything the
/// filters can ask about. Ability is the melee skill label or spell name; Note is the
/// log's trailing annotation (Riposte, Crippling Blow, Slay Undead, …) when present.</summary>
internal sealed record FeedEntry(DateTime Time, FeedWho Who, FeedKind Kind, string Actor,
    string Target, int Amount, string Ability, bool Crit, string? Note, bool Incoming);

/// <summary>
/// The FEED section's engine: a rolling buffer of combat events from your own log,
/// filtered at render time so flipping a filter re-reads the recent past instead of
/// only changing what arrives next. Rides LogWatcher.Tap beside the group trackers;
/// like them it holds raw material only — presentation stays in MainWindow.
/// </summary>
internal sealed class DamageFeed
{
    /// <summary>A couple of minutes of the busiest fight; oldest fall off first.</summary>
    private const int MaxEntries = 600;

    private readonly Queue<FeedEntry> _entries = new();
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
                tm.Skill.Length > 0 ? tm.Skill : "melee", tm.Critical, null, Incoming: false),

            ThirdDotEvent td when Interesting(td.Caster) => new FeedEntry(td.Time,
                WhoIs(td.Caster), FeedKind.Dot, td.Caster, td.Target, td.Amount,
                td.Spell, td.Critical, null, Incoming: false),

            ThirdSchoolEvent ts when Interesting(ts.Attacker) => new FeedEntry(ts.Time,
                WhoIs(ts.Attacker), FeedKind.Spell, ts.Attacker, ts.Target, ts.Amount,
                ts.Spell, ts.Critical, null, Incoming: false),

            _ => null,
        };
        if (entry is null) return;
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
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

    /// <summary>Newest-first rows passing the filters, at most <paramref name="max"/>.</summary>
    public List<FeedEntry> Snapshot(FeedFilters f, int max)
    {
        lock (_lock)
        {
            var rows = new List<FeedEntry>(max);
            foreach (var e in _entries.Reverse())
            {
                if (rows.Count >= max) break;
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

        // narrowing — only damage rows are subject to these; a kill or resist row has
        // no amount or crit flag to judge
        var isDamage = e.Kind is FeedKind.Melee or FeedKind.Spell or FeedKind.Dot
            or FeedKind.Aux or FeedKind.Taken;
        if (isDamage)
        {
            if (f.CritsOnly && !e.Crit) return false;
            if (f.SpecialsOnly && string.IsNullOrEmpty(e.Note)) return false;
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
