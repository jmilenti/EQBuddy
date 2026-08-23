using EQBuddy.Core;

namespace EQBuddy.Tests;

public class MotesTests
{
    private static LootDetail L(string item, int count) => new(item, count, "an orc");

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
