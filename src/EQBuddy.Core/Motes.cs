using System.Text.RegularExpressions;

namespace EQBuddy.Core;

public sealed record MoteTierCount(string Item, int Count);

/// <summary>One mote actually dropping: which mote, off which corpse, when. The loot
/// line already names the corpse, so this is the same derivation the summary does — but
/// kept per-drop instead of tallied, which is the only form that can answer "which mobs
/// drop these?" and "when did that Greater land?".</summary>
public sealed record MoteDrop(DateTime Time, string Item, string Source, int Count);

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

    /// <summary>Ladder order, lowest first — user-decreed from in-game (2026-08-24):
    /// the bare "Mote of Potential" is the NORMAL tier between Lesser and Major, and
    /// Major comes BEFORE Greater (the wiki pages had them the other way around). The
    /// three ranks past Superior are the old wiki guesses and unconfirmed — the table
    /// shows them as "tier 8/9/10" until one drops and brings its real name. A tier
    /// this ladder has never heard of sorts after it rather than vanishing.</summary>
    private static readonly string[] Ladder =
        ["Infinitesimal", "Minor", "Lesser", "Normal", "Major",
         "Greater", "Superior", "Grand", "Ascendant", "Infinite"];

    /// <summary>The bare mote's slot — "Normal" never appears in an item name (the base
    /// mote has no tier word), it is the DISPLAY name of that rank.</summary>
    private static readonly int NormalRank = Array.IndexOf(Ladder, "Normal");

    /// <summary>The canonical tier names in ladder order, for a UI laying the whole
    /// ladder out as fixed columns.</summary>
    public static IReadOnlyList<string> LadderTiers => Ladder;

    public static bool IsMote(string itemName) => MotePattern().IsMatch(itemName.Trim());

    /// <summary>The SHORT tier name for a full item name — "Mote of Greater Potential"
    /// is "Greater", and the bare "Mote of Potential" is "Normal" (its rank has a
    /// display name even though the item has no tier word). Empty for a non-mote, so
    /// callers can use it as the test as well as the answer.</summary>
    public static string TierOf(string itemName)
    {
        var m = MotePattern().Match(itemName.Trim());
        if (!m.Success) return "";
        var tier = m.Groups["tier"].Value;
        return tier.Length == 0 ? "Normal" : tier;
    }

    /// <summary>Ladder position for a SHORT tier name ("Greater", "Major"); the bare
    /// mote ("Normal", or its pre-1.78 short name "Base") sits at the Normal slot and
    /// an unknown name sorts above the ladder — the same rules <see cref="Summarize"/>
    /// applies. For callers laying tiers from several players side by side, where
    /// first-seen order stops being rank order.</summary>
    public static int LadderRank(string tier)
    {
        if (tier.Equals("Base", StringComparison.OrdinalIgnoreCase)) return NormalRank;
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
            // Bare "Mote of Potential" is the Normal tier, mid-ladder; unknown named
            // tiers rank above the whole ladder (newer content trends upward).
            if (tier.Length == 0) rank = NormalRank;
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
