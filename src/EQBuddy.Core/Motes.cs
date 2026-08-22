using System.Text.RegularExpressions;

namespace EQBuddy.Core;

public sealed record MoteTierCount(string Item, int Count);

public sealed record MotesSummary(int Total, double PerHour, IReadOnlyList<MoteTierCount> Tiers)
{
    public static readonly MotesSummary Empty = new(0, 0, []);
}

/// <summary>
/// The "Mote of X Potential" upgrade-currency family, pulled out of the loot stream for
/// its own card (discussions #24, #44, #49 — flipwon: "more important than Travels &amp;
/// Deaths"). Only the Potential ladder counts here: named motes like Crystallized Fire
/// Mote are ordinary items and stay in Loot. Both log shapes land in the loot stream
/// already — "--You have looted a Mote...--" and the currency-stored variant — so this
/// is a pure derivation, no new parsing.
/// </summary>
public static partial class Motes
{
    [GeneratedRegex(@"^Mote of (?:(?<tier>\w+) )?Potential$", RegexOptions.IgnoreCase)]
    private static partial Regex MotePattern();

    /// <summary>Ladder order, lowest first (eqlwiki tier pages, 2026-08-07). A tier the
    /// wiki hasn't taught us sorts after the known ladder rather than vanishing.</summary>
    private static readonly string[] Ladder =
        ["Infinitesimal", "Minor", "Lesser", "Greater", "Major",
         "Superior", "Grand", "Ascendant", "Infinite"];

    public static bool IsMote(string itemName) => MotePattern().IsMatch(itemName.Trim());

    /// <summary>Ladder position for a SHORT tier name ("Greater", "Major"); "Base" (the
    /// bare mote) sorts below the ladder and an unknown name above it — the same rules
    /// <see cref="Summarize"/> applies. For callers laying tiers from several players
    /// side by side, where first-seen order stops being rank order.</summary>
    public static int LadderRank(string tier)
    {
        if (tier.Equals("Base", StringComparison.OrdinalIgnoreCase)) return -1;
        var rank = Array.FindIndex(Ladder, t => t.Equals(tier, StringComparison.OrdinalIgnoreCase));
        return rank < 0 ? Ladder.Length : rank;
    }

    public static MotesSummary Summarize(IEnumerable<LootDetail> loot, TimeSpan elapsed)
    {
        var rows = new List<(int Rank, string Item, int Count)>();
        foreach (var l in loot)
        {
            var m = MotePattern().Match(l.Item.Trim());
            if (!m.Success) continue;
            var tier = m.Groups["tier"].Value;
            var rank = Array.FindIndex(Ladder,
                t => t.Equals(tier, StringComparison.OrdinalIgnoreCase));
            // Bare "Mote of Potential" ranks below the ladder (the base token); unknown
            // named tiers rank above it (newer content trends upward).
            if (tier.Length == 0) rank = -1;
            else if (rank < 0) rank = Ladder.Length;
            rows.Add((rank, l.Item, l.Count));
        }
        if (rows.Count == 0) return MotesSummary.Empty;

        var tiers = rows
            .OrderBy(r => r.Rank).ThenBy(r => r.Item, StringComparer.OrdinalIgnoreCase)
            .Select(r => new MoteTierCount(r.Item, r.Count)).ToList();
        var total = rows.Sum(r => r.Count);
        var perHour = total / Math.Max(elapsed.TotalHours, 1.0 / 60);
        return new MotesSummary(total, perHour, tiers);
    }
}
