using EQBuddy.Core;

namespace EQBuddy.Tests;

public class MotesTests
{
    private static LootDetail L(string item, int count) => new(item, count, "an orc");

    private static SessionStats Replay(params string[] lines)
    {
        var stats = new SessionStats { CharacterName = "Xastazi", ServerName = "freeport" };
        foreach (var line in lines)
            if (LogParser.Parse(line) is { } e) stats.Apply(e);
        return stats;
    }

    /// <summary>"Which mob dropped this, and when" needs the drops kept individually —
    /// the tier tallies can only say how many. The loot line already names the corpse,
    /// so the ledger is a record of what was parsed, not new parsing. Line shape is the
    /// currency-stored variant, verbatim from a live log (2026-08-28).</summary>
    [Fact]
    public void EveryMoteDropIsRecordedWithItsCorpseAndTime()
    {
        var s = Replay(
            "[Fri Aug 28 21:52:12 2026] You looted a Mote of Major Potential from a wan ghoul knight's corpse and stored it in your currency",
            "[Fri Aug 28 21:54:02 2026] You looted a Mote of Potential from a ghoul sage's corpse and stored it in your currency",
            "[Fri Aug 28 21:55:18 2026] You looted a Mote of Major Potential from a wan ghoul knight's corpse and stored it in your currency",
            // Ordinary loot must not enter the mote ledger.
            "[Fri Aug 28 21:56:00 2026] --You have looted a Fine Steel Rapier.--").Snapshot();

        Assert.Equal(3, s.MoteDrops.Count);
        Assert.All(s.MoteDrops, d => Assert.True(Motes.IsMote(d.Item)));
        // Oldest first, so a UI showing newest-first reverses deliberately.
        Assert.Equal(new TimeSpan(21, 52, 12), s.MoteDrops[0].Time.TimeOfDay);
        Assert.Equal(new TimeSpan(21, 55, 18), s.MoteDrops[2].Time.TimeOfDay);
        // Two tiers, and the same corpse named twice — that repetition IS the answer to
        // "which mobs drop motes".
        Assert.Equal(["Major", "Normal", "Major"],
            s.MoteDrops.Select(d => Motes.TierOf(d.Item)).ToArray());
        Assert.Equal(2, s.MoteDrops.Count(d => d.Source.Contains("ghoul knight",
            StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A session reset clears the ledger with everything else — the drops belong
    /// to the session that recorded them.</summary>
    [Fact]
    public void SessionResetClearsTheDropLedger()
    {
        var s = Replay(
            "[Fri Aug 28 21:52:12 2026] You looted a Mote of Major Potential from a wan ghoul knight's corpse and stored it in your currency");
        Assert.Single(s.Snapshot().MoteDrops);
        s.Reset();
        Assert.Empty(s.Snapshot().MoteDrops);
    }

    /// <summary>The short tier name the ledger and the board's columns share. The bare
    /// mote has no tier word but does have a rank, and "Normal" is its display name.</summary>
    [Fact]
    public void TierOfNamesTheRankNotTheItem()
    {
        Assert.Equal("Greater", Motes.TierOf("Mote of Greater Potential"));
        Assert.Equal("Normal", Motes.TierOf("Mote of Potential"));
        Assert.Equal("", Motes.TierOf("Crystallized Fire Mote"));
    }

    [Fact]
    public void OnlyThePotentialFamilyCounts()
    {
        Assert.True(Motes.IsMote("Mote of Minor Potential"));
        Assert.True(Motes.IsMote("Mote of Potential"));
        Assert.True(Motes.IsMote("mote of GRAND potential"));   // log case drift
        Assert.False(Motes.IsMote("Crystallized Fire Mote"));
        Assert.False(Motes.IsMote("Faint Mote of Shadow"));
        Assert.False(Motes.IsMote("Remote of Potential"));      // anchored, not substring
        Assert.False(Motes.IsMote("Mote of Utter Potentiality"));
    }

    [Fact]
    public void TiersSortByLadderNotAlphabet()
    {
        var s = Motes.Summarize(
            [L("Mote of Major Potential", 1), L("Mote of Infinitesimal Potential", 4),
             L("Mote of Lesser Potential", 2), L("Mote of Minor Potential", 3)],
            TimeSpan.FromHours(2));
        Assert.Equal(
            ["Mote of Infinitesimal Potential", "Mote of Minor Potential",
             "Mote of Lesser Potential", "Mote of Major Potential"],
            s.Tiers.Select(t => t.Item).ToArray());
        Assert.Equal(10, s.Total);
        Assert.Equal(5.0, s.PerHour, 3);
    }

    [Fact]
    public void UnknownTierSurvivesAfterTheLadderAndBareSitsAtNormal()
    {
        // The bare mote is the NORMAL tier (between Lesser and Major, user-decreed
        // 2026-08-24), and a tier the ladder has never heard of sorts after everything.
        var s = Motes.Summarize(
            [L("Mote of Zenith Potential", 1), L("Mote of Infinite Potential", 1),
             L("Mote of Potential", 5), L("Mote of Lesser Potential", 2),
             L("Mote of Major Potential", 1)],
            TimeSpan.FromHours(1));
        Assert.Equal(
            ["Mote of Lesser Potential", "Mote of Potential", "Mote of Major Potential",
             "Mote of Infinite Potential", "Mote of Zenith Potential"],
            s.Tiers.Select(t => t.Item).ToArray());
    }

    [Fact]
    public void ShortSessionsDoNotExplodeThePerHourRate()
    {
        // 2 motes in 30 seconds is not "240/hr" on the card — the rate floors at a
        // one-minute basis until the session has any length to speak of.
        var s = Motes.Summarize([L("Mote of Minor Potential", 2)], TimeSpan.FromSeconds(30));
        Assert.Equal(120, s.PerHour, 0);
        Assert.Equal(MotesSummary.Empty, Motes.Summarize([], TimeSpan.FromHours(1)));
    }

    /// <summary>The table view lays several players' tiers side by side, so rank must be
    /// answerable for a short name alone: the bare mote at the Normal slot (whether it
    /// arrives as "Normal" or the pre-1.78 "Base"), Major BEFORE Greater, unknowns after
    /// the whole ladder.</summary>
    [Fact]
    public void LadderRankOrdersShortTierNames()
    {
        string[] shortNames = ["Superior", "Base", "Greater", "Major", "Weird"];
        Assert.Equal(["Base", "Major", "Greater", "Superior", "Weird"],
            shortNames.OrderBy(Motes.LadderRank).ToArray());
        Assert.Equal(Motes.LadderRank("Base"), Motes.LadderRank("Normal"));
    }
}
