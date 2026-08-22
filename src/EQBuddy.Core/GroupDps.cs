namespace EQBuddy.Core;

/// <summary>One row of the group board: a nearby player and their recent output.
/// WindowDps is damage over the sliding 60-second window (the number to glance at
/// mid-fight); SessionDamage is their total this session.</summary>
public sealed record GroupMemberDps(string Name, double WindowDps, long WindowDamage,
    long SessionDamage, DateTime LastSeen)
{
    /// <summary>Session damage by source (melee skill or spell name), biggest first —
    /// as complete as YOUR log reports it, i.e. approximate by nature.</summary>
    public IReadOnlyList<SourceDamage> Breakdown { get; init; } = [];
}

/// <summary>
/// Group DPS from your own log, no network: EQ Legends writes nearby players' melee
/// ("Lizzid slashes orc centurion for 13 points of damage.") and spell damage
/// ("Orc centurion has taken 40 damage from Ignite by Lizzid.") into YOUR log, so a
/// board of who's doing what needs nothing sent anywhere. Rides the LogWatcher's Tap
/// hook; SessionStats stays untouched.
///
/// Limits, by the log's nature: only fights near you are visible, and other players'
/// output is reported less completely than your own — treat rows as "roughly", not
/// gospel. Your own pet also appears as a third-party attacker here, so callers pass
/// the pet's name to Snapshot to keep it off the board (it has its own row in the UI).
/// </summary>
public sealed class GroupDpsTracker
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>A member drops off the board after this long without a hit — they
    /// zoned, camped, or were never a groupmate at all (a passer-by's one fight).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private sealed class Member
    {
        public long Session;
        public DateTime LastSeen;
        public readonly Queue<(DateTime Time, int Damage)> Recent = new();
        /// <summary>Session damage by melee skill / spell name, as far as the log says.</summary>
        public readonly Dictionary<string, (int Hits, long Total)> Sources =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Member> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Consume one parsed event (wired to LogWatcher.Tap, which calls from its
    /// poll thread — hence the lock).</summary>
    public void Apply(GameEvent e)
    {
        var (name, dmg, time, source) = e switch
        {
            ThirdMeleeEvent tm => (tm.Attacker, tm.Amount, tm.Time,
                tm.Skill.Length > 0 ? tm.Skill : "melee"),
            ThirdDotEvent td => (td.Caster, td.Amount, td.Time, td.Spell),
            ThirdSchoolEvent ts => (ts.Attacker, ts.Amount, ts.Time, ts.Spell),
            _ => ("", 0, default(DateTime), ""),
        };
        if (dmg <= 0 || !LooksLikePlayer(name)) return;
        lock (_lock)
        {
            if (!_members.TryGetValue(name, out var m)) _members[name] = m = new Member();
            m.Session += dmg;
            m.LastSeen = time;
            m.Recent.Enqueue((time, dmg));
            var key = source.Length > 0 ? source : "melee";
            var agg = m.Sources.TryGetValue(key, out var cur) ? cur : (0, 0L);
            m.Sources[key] = (agg.Item1 + 1, agg.Item2 + dmg);
            Prune(m, time);
        }
    }

    private static void Prune(Member m, DateTime now)
    {
        while (m.Recent.Count > 0 && now - m.Recent.Peek().Time > Window)
            m.Recent.Dequeue();
    }

    /// <summary>Player names in EQ Legends are one capitalized word, letters only.
    /// Creatures are lowercase with articles ("an orc centurion"), multi-word named
    /// mobs ("Fippy Darkpaw"), and charmed pets keep their multi-word creature names —
    /// all rejected here. Single-word named mobs ("Asaka") slip through; a wrong row
    /// is glanceable and harmless, so the cheap rule wins over a mob catalog.</summary>
    public static bool LooksLikePlayer(string name)
    {
        var n = name.Trim();
        if (n.Length is < 3 or > 15) return false;
        if (!char.IsUpper(n[0])) return false;
        foreach (var c in n)
            if (!char.IsLetter(c)) return false;
        return true;
    }

    /// <summary>Current board, best window-DPS first. <paramref name="excludePet"/> keeps
    /// your own pet (a legitimate third-party attacker in the log) off the group rows.</summary>
    public IReadOnlyList<GroupMemberDps> Snapshot(DateTime now, string? excludePet = null)
    {
        var rows = new List<GroupMemberDps>();
        lock (_lock)
        {
            foreach (var pair in _members)
            {
                var (name, m) = (pair.Key, pair.Value);
                if (excludePet is { Length: > 0 } &&
                    string.Equals(name, excludePet, StringComparison.OrdinalIgnoreCase)) continue;
                if (now - m.LastSeen > StaleAfter) continue;
                Prune(m, now);
                long windowDamage = 0;
                foreach (var (_, d) in m.Recent) windowDamage += d;
                // DPS over the observed span inside the window, floored at 6s so a single
                // opening hit doesn't read as an absurd spike.
                var span = m.Recent.Count > 0
                    ? Math.Max(6.0, (now - m.Recent.Peek().Time).TotalSeconds)
                    : 6.0;
                rows.Add(new GroupMemberDps(name, windowDamage / span, windowDamage, m.Session, m.LastSeen)
                {
                    Breakdown = m.Sources
                        .OrderByDescending(kv => kv.Value.Total)
                        .Select(kv => new SourceDamage(kv.Key, kv.Value.Hits, kv.Value.Total))
                        .ToList(),
                });
            }
        }
        rows.Sort((a, b) => b.WindowDps.CompareTo(a.WindowDps));
        return rows;
    }

    public void Reset()
    {
        lock (_lock) _members.Clear();
    }
}
