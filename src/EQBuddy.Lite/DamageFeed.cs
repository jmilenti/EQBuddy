using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>What a feed row IS. Everything the log says now lands in one of these — the
/// combat kinds it always had, plus <see cref="Cast"/> and <see cref="Other"/> so that no
/// line is ever dropped on the floor, and <see cref="Summary"/> for the synthetic
/// per-kill lines the feed writes itself.</summary>
internal enum FeedKind
{
    Melee, Spell, Dot, Aux, Heal, Taken, Miss, Kill, Resist, Fizzle,
    /// <summary>Casting lifecycle: begin casting, interrupted, "You regain your
    /// concentration", a buff wearing off, someone else's cast landing.</summary>
    Cast,
    /// <summary>A mez LANDING — "X has been mesmerized." and every other verb the spells
    /// use. Split from <see cref="Cast"/> (1.79) so a mezzer can watch their locks
    /// without the casting chatter.</summary>
    Mez,
    /// <summary>A mez BREAK — "X has been awakened by Y." Its own kind purely so it can
    /// carry its own colour (1.80): a landing is good news and a break is the one that
    /// needs you NOW, and they read alike in one colour. Rides the same <c>mez</c> pill.
    /// Before 1.79 it had no bucket at all and fell to Other.</summary>
    MezBreak,
    /// <summary>NPC consider lines — "Lekab judges you amiable -- he appears to be quite
    /// formidable. (Lvl: 25)". Their own kind (1.80) rather than the Other catch-all:
    /// conning a camp is a deliberate activity, and its lines are worth a pill of their
    /// own instead of arriving mixed with every emote in the zone.</summary>
    Consider,
    /// <summary>Combat state you declared: "Auto attack is on/off.", stance and
    /// invocation changes, "You will now use X while auto attacking."</summary>
    Attack,
    /// <summary>Loot, vendor sales, crafting results.</summary>
    Loot,
    /// <summary>Corpse coin and splits — same filter pill as Loot, its own colour: the
    /// game draws money green where loot is blue, and the feed matches the game.</summary>
    Money,
    /// <summary>Progress: experience, AA gains and purchases, levels, skill-ups.</summary>
    Xp,
    /// <summary>Faction standing changes — split from <see cref="Xp"/> (1.77): the game
    /// draws them as plain text with the faction NAME in red, not xp yellow, and a
    /// grind that watches xp does not necessarily want the faction spam.</summary>
    Faction,
    /// <summary>Zone changes and /loc lines.</summary>
    Zone,
    /// <summary>Player talk: tells, says, shouts, group/guild/channel chat, auctions.</summary>
    Chat,
    /// <summary>Everything still left — emotes, mob flavor ("X staggers."), system
    /// messages. Off by default, but reachable: the point is that a line the feed has no
    /// better bucket for is FILTERED, never silently missing.</summary>
    Other,
    /// <summary>A line the feed composed itself: the damage summary printed under a
    /// mob's death.</summary>
    Summary,
}

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
    /// kind, crit, and amount. Null only for <see cref="FeedKind.Summary"/> rows, which
    /// no log line stands behind.</summary>
    public string? Raw { get; set; }

    /// <summary>Monotonic id, assigned on enqueue. The feed views render incrementally —
    /// each asks only for what arrived since the sequence it last drew — so a live feed
    /// costs one insert per new line instead of rebuilding two thousand rows.</summary>
    public long Seq { get; set; }
}

/// <summary>
/// The FEED section's engine: a rolling buffer of EVERY line from your own log, each
/// classified into a <see cref="FeedKind"/> and filtered at render time, so flipping a
/// filter re-reads the recent past instead of only changing what arrives next. Rides
/// LogWatcher.Tap/RawTap beside the group trackers; like them it holds raw material only —
/// presentation stays in <see cref="FeedView"/>.
/// </summary>
internal sealed class DamageFeed
{
    /// <summary>Scrollback depth, user-set (the ⚙ dialog; 20k default is hours of the
    /// busiest AE fighting — a full day's log is ~20-30k lines). Entries are ~300-byte
    /// records, so even 100k is ~30 MB; the working limit is the per-render filter pass
    /// over the buffer, still comfortable at 100k. Oldest fall off first.</summary>
    private int _capacity = 20_000;

    /// <summary>Change the buffer depth, trimming immediately on a shrink.</summary>
    public void SetCapacity(int entries)
    {
        var cap = Math.Clamp(entries, 500, 200_000);
        lock (_lock)
        {
            _capacity = cap;
            Trim(_entries, cap, force: true);
        }
    }

    /// <summary>A List, not a Queue: a render walks the newest end backwards by index, and
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

    /// <summary>ONE buffer for everything. Before 1.70 the combat view and the raw view
    /// had a buffer each, and a line that made no combat event existed only in the raw
    /// one — which is why "You regain your concentration and continue your casting" could
    /// not be shown in the combat view under any filter. Now every line becomes an entry
    /// (classified <see cref="FeedKind.Cast"/> or <see cref="FeedKind.Other"/> when it is
    /// nothing more specific) and the raw view is simply the unfiltered read of it.</summary>
    private readonly List<FeedEntry> _entries = [];

    /// <summary>Ids handed out to every entry — a view holds one cursor into this.</summary>
    private long _seq;

    /// <summary>Entries made from the line currently being processed, waiting for that
    /// line's text. LogWatcher parses a line and fires Tap, THEN fires RawTap with the
    /// same line on the same thread — so whatever is sitting here when a raw line
    /// arrives came from it. (A line yields at most one event today; a list keeps that
    /// from being load-bearing.)</summary>
    private readonly List<FeedEntry> _awaitingRaw = [];

    /// <summary>The event parsed from the line currently being processed, whether or not
    /// the feed made a row of it. Same one-thread hand-off as <see cref="_awaitingRaw"/>:
    /// it lets ApplyRaw classify a line the combat view has no row for — a cast, a loot
    /// line, a zone change — instead of guessing from the text.</summary>
    private GameEvent? _lastEvent;

    private readonly object _lock = new();

    /// <summary>Current pet name, written by the UI tick, read on the watcher thread —
    /// how a third-party attacker is told apart from your own pet.</summary>
    public volatile string PetName = "";

    /// <summary>Whether YOUR auto attack is on, straight from the log's own "Auto attack
    /// is on/off." lines — the game states it every time it flips. This is what the feed
    /// windows' combat outline follows: not "blows landed recently" (which lingers after
    /// a kill and misses the wind-up before the first swing) but the switch you actually
    /// threw. Replayed with the rest of the log at startup, so it comes up in the state
    /// the character was left in.</summary>
    public volatile bool AttackOn;

    public void Apply(GameEvent e)
    {
        var entry = e switch
        {
            DamageDealtEvent d => new FeedEntry(d.Time, FeedWho.You,
                d.IsAux ? FeedKind.Aux : d.OverTime ? FeedKind.Dot
                    : d.Kind == DamageKind.Melee ? FeedKind.Melee : FeedKind.Spell,
                "you", d.Target, d.Amount, d.Source, d.Critical, d.Note, Incoming: false),

            // Self-damage (HP-cost casting, falls) isn't a fight — same rule Core uses.
            // The bare non-melee form ("YOU are pierced by thorns for 2 points of
            // non-melee damage!") is a damage shield burning YOU — the mirror of the
            // outgoing IsAux rows — so it rides the ds pill like they do (reported:
            // "ds off but I'm seeing YOU are pierced"). Spells and DoTs aimed at you
            // name their spell or their line shape and stay Taken.
            DamageTakenEvent { Self: false, Melee: false, OverTime: false, Ability: "" } t
                => new FeedEntry(t.Time, FeedWho.You, FeedKind.Aux, t.Attacker, "you",
                    t.Amount, "", false, null, Incoming: true),
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
        // The attack-order tell is addressed to US — no bystander's pet ever sends it —
        // so the feed can claim the pet the instant it appears instead of waiting for
        // the next 1 s snapshot tick to copy Core's claim over. Without this, a pet's
        // first swing after the claim landed in Other (PetName still empty here). The
        // tick keeps overwriting with Core's authoritative name, so the two can never
        // drift for more than a second.
        if (e is PetClaimEvent { Leader: null } claim)
            PetName = LogParser.Normalize(claim.PetName);
        lock (_lock)
        {
            _lastEvent = e;
            if (entry is null) return;
            Enqueue(entry);
            _awaitingRaw.Add(entry);
            Track(entry);
            if (e is KillEvent kill) Summarise(kill);
        }
    }

    /// <summary>Add an entry to the buffer under the lock, stamping its id.</summary>
    private void Enqueue(FeedEntry entry)
    {
        entry.Seq = ++_seq;
        _entries.Add(entry);
        Trim(_entries, _capacity);
    }

    /// <summary>Raw capture, straight off LogWatcher.RawTap — every line, in order. The
    /// "[Sat Aug 22 17:20:01 2026] " prefix is split off here once rather than at render
    /// time. A line the combat view made no row for becomes one HERE, classified from the
    /// event the parser did or didn't make of it, so it can be filtered rather than
    /// silently missing.</summary>
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
        // Exact-prefix, not Contains: chat quoting the words ("my pet does not auto
        // attack") must not flip the state.
        if (text.StartsWith("Auto attack is on", StringComparison.Ordinal)) AttackOn = true;
        else if (text.StartsWith("Auto attack is off", StringComparison.Ordinal)) AttackOn = false;

        lock (_lock)
        {
            // Hand this line's text to the entries parsed from it (see _awaitingRaw), and
            // clear the slate either way — an unclaimed entry must not adopt the NEXT
            // line's text.
            var claimed = _awaitingRaw.Count > 0;
            foreach (var pending in _awaitingRaw) pending.Raw = text;
            _awaitingRaw.Clear();
            var evt = _lastEvent;
            _lastEvent = null;
            if (claimed || text.Length == 0) return;

            // Nothing combat-shaped came of this line, so make the row here. Time from the
            // event when the parser read one (it agrees with the prefix), else the prefix,
            // else now — a row with no clock at all reads as broken.
            var stamp = evt?.Time ?? (time == DateTime.MinValue ? DateTime.Now : time);
            Enqueue(new FeedEntry(stamp, FeedWho.You, KindOf(evt, text), "", "", 0,
                AbilityOf(evt), false, null, Incoming: false)
            {
                Raw = text,
            });
        }
    }

    /// <summary>Which bucket a line with no combat row belongs in. The parsed event
    /// decides when there is one; otherwise a few text shapes the parser has no event
    /// for are recognised directly, and everything else is Other.</summary>
    private static FeedKind KindOf(GameEvent? evt, string text) => evt switch
    {
        SpellCastEvent or SpellInterruptedEvent or SpellWornOffEvent or BuffFadeEvent
            or OtherCastEvent or ItemProcEvent or CharmedEvent => FeedKind.Cast,
        MezzedEvent => FeedKind.Mez,
        ConsiderEvent => FeedKind.Consider,
        DeathEvent => FeedKind.Kill,
        RegenTickEvent => FeedKind.Heal,
        RuneBlockEvent or ThirdMissEvent => FeedKind.Miss,
        StanceEvent or InvocationEvent or SkillSubstitutionEvent => FeedKind.Attack,
        MoneyEvent => FeedKind.Money,
        LootEvent or AutoSellEvent or ItemDestroyedEvent or CraftEvent => FeedKind.Loot,
        XpEvent or AaEvent or AaPurchaseEvent or LevelEvent or SkillUpEvent => FeedKind.Xp,
        FactionEvent => FeedKind.Faction,
        ZoneEvent or LocationEvent => FeedKind.Zone,
        // The break line makes no parser event (AudioCues reads it off RawTap the same
        // way). Ordinal Contains, like the chat shapes — the phrase is the game's own.
        null when text.Contains("has been awakened by", StringComparison.Ordinal) => FeedKind.MezBreak,
        null when LooksLikeConsider(text) => FeedKind.Consider,
        null when LooksLikeCasting(text) => FeedKind.Cast,
        null when text.StartsWith("Auto attack is ", StringComparison.Ordinal) => FeedKind.Attack,
        null when LooksLikeChat(text) => FeedKind.Chat,
        _ => FeedKind.Other,
    };

    /// <summary>Player talk, by the shapes the log actually uses (verified against a real
    /// 80k-line log: channel tells dominate — "X tells General:2, '…'"). Ordinal, not
    /// word-boundary clever: every one of these carries the quoting comma-apostrophe or a
    /// fixed verb the flavor lines don't.</summary>
    private static bool LooksLikeChat(string text) =>
        text.Contains(" tells ", StringComparison.Ordinal) ||
        text.Contains(" told you,", StringComparison.Ordinal) ||
        text.Contains(" says, ", StringComparison.Ordinal) ||
        text.Contains(" says '", StringComparison.Ordinal) ||
        text.Contains(" shouts,", StringComparison.Ordinal) ||
        text.Contains(" auctions,", StringComparison.Ordinal) ||
        text.StartsWith("You told ", StringComparison.Ordinal) ||
        text.StartsWith("You say", StringComparison.Ordinal) ||
        text.StartsWith("You tell ", StringComparison.Ordinal) ||
        text.StartsWith("You shout", StringComparison.Ordinal) ||
        text.StartsWith("You auction", StringComparison.Ordinal);

    /// <summary>An NPC consider line the parser made no event of. Core's ConsiderRx wants
    /// the verb list it has observed AND an exact "(Lvl: N)" tail, so a shape it misses —
    /// another faction phrase, a differently-cased tail — would land in Other with no way
    /// to filter it. BOTH halves must hold here: the verb phrases alone would catch chat
    /// quoting them, and "lvl:" alone would catch anyone typing a level in a tell.</summary>
    private static bool LooksLikeConsider(string text) =>
        text.Contains("lvl:", StringComparison.OrdinalIgnoreCase) &&
        (text.Contains(" scowls at you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" regards you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" glares at you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" glowers at you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" judges you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" kindly considers you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" looks upon you", StringComparison.OrdinalIgnoreCase) ||
         text.Contains(" looks your way", StringComparison.OrdinalIgnoreCase));

    /// <summary>Casting messages the parser makes no event of — the interruption and
    /// recovery chatter that belongs beside "You begin casting" rather than in with the
    /// zone lines and the guild chat.</summary>
    private static bool LooksLikeCasting(string text) =>
        text.Contains("regain your concentration", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("lose your concentration", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Your spell is interrupted", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Your spell did not take hold", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Your target resisted", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("spell would not have taken hold", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Insufficient Mana", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("You must first select a target", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Your spell fizzles", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("You begin casting", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("You begin singing", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("You begin to sing", StringComparison.OrdinalIgnoreCase);

    /// <summary>The spell or item a non-combat row is ABOUT, so the accent colour has
    /// something to pick out of the line.</summary>
    private static string AbilityOf(GameEvent? evt) => evt switch
    {
        SpellCastEvent c => c.Spell,
        SpellInterruptedEvent i => i.Spell,
        SpellWornOffEvent w => w.Spell,
        OtherCastEvent o => o.Spell,
        BuffFadeEvent b => b.Label,
        ItemProcEvent p => p.Item,
        LootEvent l => l.Item,
        // The OTHER loot shapes name their item too (reported: "some items are not
        // showing up in the pink colour" — only plain looted/stored/create lines were;
        // auto-sold, destroyed, crafted, and vendor-sale lines parse to different
        // events, and the accent never saw their items).
        AutoSellEvent s => s.Item,
        ItemDestroyedEvent d => d.Item,
        CraftEvent cr => cr.Item,
        MoneyEvent { Item: { Length: > 0 } } m => m.Item!,
        // Not an ability, but the same job: the accent picks the faction NAME out of
        // the line, red like the game draws it.
        FactionEvent fa => fa.Faction,
        // Likewise the NPC's name in a consider line — the word you are actually
        // scanning for. Normalize drops the leading article, so the name still occurs
        // inside the line the game wrote ("an orc pawn scowls…" contains "orc pawn").
        ConsiderEvent c => c.Name,
        _ => "",
    };

    // ---- per-mob tallies, for the summary line under a kill ----

    private sealed class Tally
    {
        public DateTime First, Last;
        public long You, Pet;
        public readonly Dictionary<string, long> Others = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Damage done to each creature since it was first hit, so the moment it dies
    /// the feed can say what the pull was worth. Keyed by target name — the same
    /// same-named-mobs caveat the rest of the app carries. Cleared per kill; capped and
    /// aged out so a mob that wanders off never accumulates.</summary>
    private readonly Dictionary<string, Tally> _tallies = new(StringComparer.OrdinalIgnoreCase);

    private void Track(FeedEntry e)
    {
        if (e.Incoming || e.Amount <= 0 || e.Target.Length == 0) return;
        if (e.Kind is not (FeedKind.Melee or FeedKind.Spell or FeedKind.Dot or FeedKind.Aux)) return;

        if (!_tallies.TryGetValue(e.Target, out var tally))
        {
            PruneTallies(e.Time);
            _tallies[e.Target] = tally = new Tally { First = e.Time };
        }
        else if (e.Time - tally.Last > PullGap)
        {
            // Same name, different pull: a cave bear that went quiet three minutes ago
            // and is being hit again is the RESPAWN, not the same fight. Without this a
            // lingering tally poisoned the kill summary — "group (1467s): Grumpy 0 dps"
            // measured one pull's damage over twenty minutes of wall clock.
            _tallies[e.Target] = tally = new Tally { First = e.Time };
        }
        tally.Last = e.Time;
        switch (e.Who)
        {
            case FeedWho.You: tally.You += e.Amount; break;
            case FeedWho.Pet: tally.Pet += e.Amount; break;
            default:
                tally.Others.TryGetValue(e.Actor, out var had);
                tally.Others[e.Actor] = had + e.Amount;
                break;
        }
    }

    /// <summary>Quiet this long = the fight it was part of is over; the next hit on the
    /// name starts a fresh tally. Generous next to real swing gaps (seconds).</summary>
    private static readonly TimeSpan PullGap = TimeSpan.FromMinutes(3);

    /// <summary>Mobs that stopped taking damage ten minutes ago are gone — they fled, you
    /// zoned, or someone else finished them out of sight. Also caps the dictionary, so a
    /// long session of runners can't grow it without bound.</summary>
    private void PruneTallies(DateTime now)
    {
        if (_tallies.Count < 64) return;
        foreach (var (name, t) in _tallies.ToList())
            if (now - t.Last > TimeSpan.FromMinutes(10)) _tallies.Remove(name);
        while (_tallies.Count >= 128)
        {
            var oldest = _tallies.OrderBy(kv => kv.Value.Last).First().Key;
            _tallies.Remove(oldest);
        }
    }

    /// <summary>The synthetic rows under a death line: what you, your pet, and everyone
    /// else did to that mob over the pull. Written as entries like any other row, so they
    /// filter, colour, and scroll the same — each behind its own toggle.</summary>
    private void Summarise(KillEvent kill)
    {
        if (!_tallies.Remove(kill.Target, out var tally)) return;
        var seconds = Math.Max(1, (tally.Last - tally.First).TotalSeconds);

        void Row(FeedWho who, string label, long damage, string? detail = null)
        {
            if (damage <= 0) return;
            var text = $"⤷ {label} {damage:N0} in {seconds:0}s · {damage / seconds:N0} dps"
                + (detail is { Length: > 0 } d ? $" · {d}" : "");
            Enqueue(new FeedEntry(kill.Time, who, FeedKind.Summary, label, kill.Target,
                (int)Math.Min(int.MaxValue, damage), label, false, null, Incoming: false)
            {
                Raw = text,
            });
        }

        Row(FeedWho.You, "you", tally.You);
        Row(FeedWho.Pet, PetName.Length > 0 ? PetName : "pet", tally.Pet);
        // The group line names the PLAYERS with each one's rate — "group 240 in 41s"
        // told nobody anything they would act on; "who carried the pull" is the
        // question the line exists to answer.
        if (tally.Others.Count > 0)
        {
            var players = string.Join(" · ", tally.Others
                .OrderByDescending(kv => kv.Value)
                .Take(6)
                .Select(kv => $"{kv.Key} {kv.Value / seconds:N0} dps ({Compact(kv.Value)})"));
            Enqueue(new FeedEntry(kill.Time, FeedWho.Group, FeedKind.Summary, "group",
                kill.Target, (int)Math.Min(int.MaxValue, tally.Others.Values.Sum()),
                "group", false, null, Incoming: false)
            {
                Raw = $"⤷ group ({seconds:0}s): {players}",
            });
        }
    }

    /// <summary>"12.4k" / "830" — a damage amount short enough to sit inside a list of
    /// six players without the line outgrowing every window.</summary>
    private static string Compact(long amount) =>
        amount >= 10_000 ? $"{amount / 1000.0:0.#}k" : amount.ToString("N0");

    /// <summary>Third-party attackers worth a feed row: your pet, or something that
    /// looks like a player. Mob-on-mob and mob-on-others noise stays out of the combat
    /// kinds — it still reaches the feed as <see cref="FeedKind.Other"/>.</summary>
    private bool Interesting(string attacker) =>
        IsPet(attacker) || GroupDpsTracker.LooksLikePlayer(attacker);

    /// <summary>Same normalization Core applies: the snapshot's PetName is normalized
    /// ("Imp protector") while the log's events carry the article ("An imp protector"),
    /// so a plain compare NEVER matched a charmed pet — its damage failed the
    /// Interesting() gate and fell all the way to Other, which is why "pet" showed
    /// nothing and "other" showed the pet.</summary>
    private bool IsPet(string name)
    {
        var normalized = LogParser.Normalize(name);
        if (string.Equals(normalized, "Your pet", StringComparison.OrdinalIgnoreCase))
            return true;
        return PetName.Length > 0 &&
            string.Equals(normalized, PetName, StringComparison.OrdinalIgnoreCase);
    }

    private FeedWho WhoIs(string actor) =>
        string.Equals(actor, "you", StringComparison.OrdinalIgnoreCase) ? FeedWho.You
        : IsPet(actor) ? FeedWho.Pet
        : FeedWho.Group;

    /// <summary>Oldest-first rows passing the filters, at most <paramref name="max"/>,
    /// and only what arrived after <paramref name="since"/> (0 = the whole buffer, i.e. a
    /// full rebuild). <paramref name="cursor"/> comes back as the sequence this snapshot
    /// has caught up to — hand it back next time to be given only the newer rows.
    ///
    /// The cursor stops SHORT of an entry still waiting for its raw text (see
    /// <see cref="_awaitingRaw"/>): the watcher fires Tap and RawTap under two separate
    /// lock acquisitions, so a render landing between them would otherwise publish the row
    /// with no text at all and — being incremental — never redraw it.</summary>
    public List<FeedEntry> Snapshot(FeedFilters f, int max, long since, out long cursor)
    {
        lock (_lock)
        {
            var pending = long.MaxValue;
            foreach (var e in _awaitingRaw) pending = Math.Min(pending, e.Seq);
            cursor = Math.Max(since, pending == long.MaxValue ? _seq : pending - 1);

            // Walk backwards from the newest (that is where "the last N matching rows"
            // lives), then flip: the list reads oldest at the top, newest at the bottom,
            // the way a chat window does.
            var rows = new List<FeedEntry>();
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Seq <= since || rows.Count >= max) break;
                if (e.Seq >= pending) continue;
                if (Matches(e, f)) rows.Add(e);
            }
            rows.Reverse();
            return rows;
        }
    }

    internal static bool Matches(FeedEntry e, FeedFilters f)
    {
        // Everything below is ANDed, and the search chips are checked last so that a chip
        // never widens what the pills allow. Raw mode is the log verbatim — no kind or
        // who gating at all — except that the feed's OWN summary rows still answer to
        // their toggles, since no log line stands behind them to be shown "as written".
        if (f.RawMode)
        {
            if (e.Kind == FeedKind.Summary && !SummaryAllowed(e, f)) return false;
        }
        else if (!KindAllowed(e, f)) return false;

        // Search chips: OR within, AND against everything else. The haystack carries
        // the words a player would type — "slay" hits the note, "heal" the kind,
        // "crit" both the flag and a "(Critical)" note, and the displayed line itself
        // so a chip matches what the reader can actually see on the row.
        if (f.SearchTerms is { Count: > 0 } terms)
        {
            var hay = $"{e.Actor} {e.Ability} {e.Target} {e.Note} {e.Kind} {e.Raw}"
                + (e.Crit ? " critical" : "");
            var any = false;
            foreach (var term in terms)
                if (hay.Contains(term, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }

    private static bool KindAllowed(FeedEntry e, FeedFilters f)
    {
        // Cast and Other describe the log talking, not somebody hitting something, so the
        // who-pills don't apply to them — gating "You regain your concentration" behind a
        // pill called "pet" would be nonsense.
        switch (e.Kind)
        {
            case FeedKind.Cast: return f.Casts;
            case FeedKind.Mez or FeedKind.MezBreak: return f.Mez;
            case FeedKind.Consider: return f.Consider;
            case FeedKind.Attack: return f.Attack;
            case FeedKind.Loot or FeedKind.Money: return f.Loot;
            case FeedKind.Xp: return f.Xp;
            case FeedKind.Faction: return f.Faction;
            case FeedKind.Zone: return f.Zone;
            case FeedKind.Chat: return f.Chat;
            case FeedKind.Other: return f.Other;
        }

        // who — incoming rows ride their own toggle, not the actor's
        if (e.Incoming)
        {
            if (!f.Incoming) return false;
        }
        else if (e.Who switch { FeedWho.You => !f.You, FeedWho.Pet => !f.Pet, _ => !f.Group })
        {
            return false;
        }

        if (e.Kind == FeedKind.Summary) return SummaryAllowed(e, f);

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

    /// <summary>A kill summary shows when its own toggle is on — one per subject, so a
    /// window can carry your own numbers without the group's.</summary>
    private static bool SummaryAllowed(FeedEntry e, FeedFilters f) => e.Who switch
    {
        FeedWho.You => f.SummaryYou,
        FeedWho.Pet => f.SummaryPet,
        _ => f.SummaryGroup,
    };

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
