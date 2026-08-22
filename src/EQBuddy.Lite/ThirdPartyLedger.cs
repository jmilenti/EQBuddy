using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>
/// A rolling record of third-party damage events (who hit what, when, for how much),
/// so a finished fight's popup can answer "what did the group do on THIS mob" from
/// your own log. Approximate by nature — the log only reports fights near you, and
/// other players' output incompletely. Rides the LogWatcher Tap beside GroupDpsTracker.
/// </summary>
public sealed class ThirdPartyLedger
{
    private readonly record struct Entry(DateTime Time, string Attacker, string Target, int Amount);

    /// <summary>Enough for hours of busy grouping; oldest entries fall off first.</summary>
    private const int MaxEntries = 50_000;

    private readonly Queue<Entry> _entries = new();
    private readonly object _lock = new();

    public void Apply(GameEvent e)
    {
        var (attacker, target, amount, time) = e switch
        {
            ThirdMeleeEvent tm => (tm.Attacker, tm.Target, tm.Amount, tm.Time),
            ThirdDotEvent td => (td.Caster, td.Target, td.Amount, td.Time),
            ThirdSchoolEvent ts => (ts.Attacker, ts.Target, ts.Amount, ts.Time),
            _ => ("", "", 0, default(DateTime)),
        };
        if (amount <= 0 || !GroupDpsTracker.LooksLikePlayer(attacker)) return;
        lock (_lock)
        {
            _entries.Enqueue(new Entry(time, attacker, LogParser.Normalize(target), amount));
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
    }

    /// <summary>Per-player damage against <paramref name="target"/> during a fight's
    /// window (small tail buffer for DoT ticks landing after the kill line), biggest
    /// first. <paramref name="excludePet"/> keeps your own pet out — it already has
    /// its rows in your ability breakdown.</summary>
    public List<(string Name, int Hits, long Total)> DamageOn(
        string target, DateTime start, TimeSpan duration, string? excludePet)
    {
        var end = start + duration + TimeSpan.FromSeconds(3);
        var byPlayer = new Dictionary<string, (int Hits, long Total)>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (entry.Time < start || entry.Time > end) continue;
                if (!string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (excludePet is { Length: > 0 } &&
                    string.Equals(entry.Attacker, excludePet, StringComparison.OrdinalIgnoreCase)) continue;
                var agg = byPlayer.GetValueOrDefault(entry.Attacker);
                byPlayer[entry.Attacker] = (agg.Hits + 1, agg.Total + entry.Amount);
            }
        }
        return byPlayer
            .OrderByDescending(kv => kv.Value.Total)
            .Select(kv => (kv.Key, kv.Value.Hits, kv.Value.Total))
            .ToList();
    }

    public void Reset()
    {
        lock (_lock) _entries.Clear();
    }
}
