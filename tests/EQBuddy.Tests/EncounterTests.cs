using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>Releases C/D/E: encounters, reward correlation, mob farming, stance, watch kinds.</summary>
public class EncounterTests
{
    private static string At(int mm, int ss, string msg) =>
        $"[Sat Jul 18 15:{mm:D2}:{ss:D2} 2026] {msg}";

    private static SessionStats Replay(params string[] lines)
    {
        var stats = new SessionStats { CharacterName = "Kaybek", ServerName = "freeport" };
        foreach (var line in lines)
        {
            var evt = LogParser.Parse(line);
            if (evt is not null) stats.Apply(evt);
        }
        return stats;
    }

    /// <summary>Slay Undead is a different attack, not an annotation: the paladin proc
    /// multiplies the swing hard enough that folding it into the plain skill hides both
    /// numbers — the base rate reads too high and the slay's share is invisible. So
    /// "Punch" and "Punch (Slay)" are separate breakdown rows, in the session list and
    /// in the fight's own. Notes COMBINE in real logs ("Riposte Slay Undead" appears),
    /// so the split is by substring, and modifier-only notes must NOT split.</summary>
    [Fact]
    public void SlayUndeadGetsItsOwnBreakdownRow()
    {
        var s = Replay(
            At(0, 0, "You punch a ghoul for 10 points of damage."),
            At(0, 1, "You punch a ghoul for 900 points of damage. (Slay Undead)"),
            At(0, 2, "You punch a ghoul for 12 points of damage."),
            // Compound note: still a slay, and still one row with the plain slay above.
            At(0, 3, "You punch a ghoul for 800 points of damage. (Riposte Slay Undead)"),
            // Modifier-only notes annotate the SAME attack and must not split it off.
            At(0, 4, "You punch a ghoul for 30 points of damage. (Critical)"),
            At(0, 5, "You punch a ghoul for 11 points of damage. (Riposte)")).Snapshot();

        var plain = Assert.Single(s.DamageBySource, d => d.Name == "Punch");
        var slay = Assert.Single(s.DamageBySource, d => d.Name == "Punch (Slay)");
        Assert.Equal((4, 63L), (plain.Hits, plain.Total));    // 10 + 12 + 30 + 11
        Assert.Equal((2, 1700L), (slay.Hits, slay.Total));    // 900 + 800
    }

    /// <summary>The same split reaches the per-fight breakdown, which is a separate
    /// dictionary filled from the same label.</summary>
    [Fact]
    public void SlayUndeadSplitsInsideTheFightBreakdown()
    {
        var s = Replay(
            At(0, 0, "You punch a ghoul for 10 points of damage."),
            At(0, 1, "You punch a ghoul for 900 points of damage. (Slay Undead)"),
            At(0, 2, "You have slain a ghoul!")).Snapshot();

        var fight = Assert.Single(s.RecentEncounters);
        Assert.Contains(fight.ByAbility, a => a.Name == "Punch (Slay)" && a.Total == 900);
        Assert.Contains(fight.ByAbility, a => a.Name == "Punch" && a.Total == 10);
    }

    [Fact]
    public void RewardsLoggedBeforeKillLineCorrelate()
    {
        // Live EQL order: experience → coin → "You have slain X!", all the same second.
        var s = Replay(
            At(0, 0, "You slash a ghoul for 10 points of damage."),
            At(0, 5, "You gain experience! (1.580%)"),
            At(0, 5, "You receive 5 silver and 2 copper from the corpse."),
            At(0, 5, "You have slain a ghoul!")).Snapshot();
        var mob = s.Mobs.Single(m => m.Name == "Ghoul");
        Assert.Equal(1.58, mob.XpPercent, 3);
        Assert.Equal(52, mob.Copper);
    }

    [Fact]
    public void StaleRewardsAreNotClaimedByALaterKill()
    {
        // Quest xp / old coin outside the window must not stick to the next kill.
        var s = Replay(
            At(0, 0, "You gain experience! (2.000%)"),
            At(1, 0, "You slash a ghoul for 10 points of damage."),
            At(1, 5, "You have slain a ghoul!")).Snapshot();
        Assert.Equal(0, s.Mobs.Single(m => m.Name == "Ghoul").XpPercent);
    }

    [Fact]
    public void SequentialSameNameKillsAreDistinctEncounters()
    {
        var s = Replay(
            At(0, 0, "You slash orc pawn for 10 points of damage."),
            At(0, 5, "You have slain orc pawn!"),
            At(0, 20, "You slash orc pawn for 10 points of damage."),
            At(0, 30, "You have slain orc pawn!"),
            At(0, 40, "You slash orc pawn for 10 points of damage."),
            At(0, 45, "You have slain orc pawn!")).Snapshot();

        Assert.Equal(3, s.EncounterCount);
        Assert.All(s.RecentEncounters, e => Assert.Equal("Killed", e.Outcome));
        Assert.Equal(3, s.Mobs.Single(m => m.Name == "Orc pawn").Encounters);
    }

    [Fact]
    public void AbandonedFightTimesOut()
    {
        var s = Replay(
            At(0, 0, "You slash a ghoul for 10 points of damage."),
            At(0, 2, "You slash a ghoul for 10 points of damage."),
            At(5, 0, "You have entered West Commonlands.")).Snapshot();   // 5 min later, no kill

        var enc = Assert.Single(s.RecentEncounters);
        Assert.Equal(("Ghoul", "Timeout"), (enc.Name, enc.Outcome));
        Assert.Equal(20, enc.DamageOut);
    }

    [Fact]
    public void EncounterDpsAndDamageInTracked()
    {
        var s = Replay(
            At(0, 0, "You slash orc centurion for 30 points of damage."),
            At(0, 5, "Orc centurion hits YOU for 7 points of damage."),
            At(0, 10, "You slash orc centurion for 30 points of damage."),
            At(0, 10, "You have slain orc centurion!")).Snapshot();

        var enc = Assert.Single(s.RecentEncounters);
        Assert.Equal(60, enc.DamageOut);
        Assert.Equal(7, enc.DamageIn);
        Assert.Equal(6, enc.Dps, 0);   // 60 over 10s
    }

    [Fact]
    public void RewardsCorrelateToTheKilledCreature()
    {
        var s = Replay(
            At(0, 0, "You slash a ghoul for 10 points of damage."),
            At(0, 5, "You have slain a ghoul!"),
            At(0, 6, "You gain party experience! (0.5%)"),
            At(0, 7, "You receive 2 gold from the corpse."),
            At(0, 8, "--You have looted a Research Page from a ghoul's corpse.--"),
            // Unrelated coin a minute later must NOT correlate (window is 3 s).
            At(1, 30, "You receive 9 platinum from the corpse.")).Snapshot();

        var mob = s.Mobs.Single(m => m.Name == "Ghoul");
        Assert.Equal(1, mob.Kills);
        Assert.Equal(0.5, mob.XpPercent, 2);
        Assert.Equal(200, mob.Copper);
        var loot = Assert.Single(mob.Loot);
        Assert.Equal(("Research Page", 1, 100.0), (loot.Item, loot.Count, loot.DropRatePct!.Value));
    }

    [Fact]
    public void DropRateUsesKillDenominator()
    {
        var s = Replay(
            At(0, 0, "You have slain a ghoul!"),
            At(0, 10, "You have slain a ghoul!"),
            At(0, 20, "You have slain a ghoul!"),
            At(0, 30, "You have slain a ghoul!"),
            At(0, 31, "--You have looted a Research Page from a ghoul's corpse.--")).Snapshot();

        var mob = s.Mobs.Single(m => m.Name == "Ghoul");
        Assert.Equal(4, mob.Kills);
        Assert.Equal(25.0, Assert.Single(mob.Loot).DropRatePct!.Value, 1);
    }

    [Fact]
    public void LootFromUnkilledCreatureHasNoRate()
    {
        // Group killed it; we only looted — rate denominator is 0 → no percentage claimed.
        var s = Replay(
            At(0, 0, "--You have looted a Fine Steel Long Sword from a ghoul knight's corpse.--")).Snapshot();
        var mob = s.Mobs.Single(m => m.Name == "Ghoul knight");
        Assert.Equal(0, mob.Kills);
        Assert.Null(Assert.Single(mob.Loot).DropRatePct);
    }

    [Fact]
    public void StanceWindowsAttributeDamageAndCombatTime()
    {
        var s = Replay(
            At(0, 0, "You assume an offensive stance."),
            At(0, 5, "You slash orc pawn for 40 points of damage."),
            At(0, 6, "You slash orc pawn for 40 points of damage."),
            At(1, 0, "You assume a defensive stance."),
            At(1, 5, "You slash orc pawn for 10 points of damage."),
            At(1, 6, "You slash orc pawn for 10 points of damage."),
            At(5, 0, "You have entered West Commonlands.")).Snapshot();

        Assert.Equal("Defensive", s.CurrentStance);
        var off = s.Stances.Single(x => x.Name == "Offensive");
        var def = s.Stances.Single(x => x.Name == "Defensive");
        Assert.Equal(80, off.Damage);
        Assert.Equal(20, def.Damage);
        Assert.True(off.CombatSeconds >= 1);
    }

    [Fact]
    public void InvocationWindowsAttributeDamageLikeStances()
    {
        // Real line shapes from eqlog_Hugzee_qeynos 2026-08-03 — the first invocation
        // evidence ever observed. The "begin to change" precursor must parse to nothing
        // (like the knocked-unconscious line) or every swap would double-fire.
        var s = Replay(
            At(0, 0, "You begin to change your invocation."),
            At(0, 0, "You begin reciting the empowering invocation."),
            At(0, 5, "You slash orc pawn for 40 points of damage."),
            At(1, 0, "You begin reciting the unyielding invocation."),
            At(1, 5, "You slash orc pawn for 10 points of damage."),
            At(5, 0, "You have entered West Commonlands.")).Snapshot();

        Assert.Equal("Unyielding", s.CurrentInvocation);
        Assert.Equal(40, s.Invocations.Single(x => x.Name == "Empowering").Damage);
        Assert.Equal(10, s.Invocations.Single(x => x.Name == "Unyielding").Damage);
        // Stances and invocations are independent axes — one didn't leak into the other.
        Assert.Empty(s.Stances);
    }

    [Fact]
    public void FightBreakdownIncludesPetRowsAndWhatItHitYouWith()
    {
        var s = Replay(
            At(0, 0, "Jibekn told you, 'Attacking orc pawn Master.'"),
            At(0, 1, "Jibekn slashes orc pawn for 20 points of damage."),
            At(0, 2, "You slash orc pawn for 30 points of damage."),
            At(0, 3, "Orc pawn hits YOU for 5 points of damage."),
            At(0, 4, "orc pawn hit you for 12 points of magic damage by Shock of Blades."),
            At(0, 5, "You have slain orc pawn!")).Snapshot();

        var f = s.LastFight!;
        Assert.Equal(30, f.ByAbility.Single(x => x.Name == "Slash").Total);
        Assert.Equal(20, f.ByAbility.Single(x => x.Name == "Pet (Jibekn)").Total);
        Assert.Equal(5, f.ByIncoming.Single(x => x.Name == "Hit").Total);
        Assert.Equal(12, f.ByIncoming.Single(x => x.Name == "Shock of Blades").Total);

        // The archived encounter carries the same breakdown for the History review.
        var e = s.Encounters.Single();
        Assert.Equal(f.ByAbility.Select(x => (x.Name, x.Total)), e.ByAbility.Select(x => (x.Name, x.Total)));
        Assert.Equal(f.ByIncoming.Select(x => (x.Name, x.Total)), e.ByIncoming.Select(x => (x.Name, x.Total)));

        // And it survives the snapshot JSON round-trip (history.db path).
        var restored = System.Text.Json.JsonSerializer.Deserialize<StatsSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.Equal(12, restored.Encounters.Single().ByIncoming.Single(x => x.Name == "Shock of Blades").Total);
    }

    [Fact]
    public void ATwinHittingYouDoesNotDropABusyPet()
    {
        // Charm camps are full of same-named creatures. The pet claim used to be
        // dropped by ANY hit from a pet-named attacker — so charming "a will sapper"
        // with a second will sapper in camp lost the pet on the twin's first swing
        // (the "pet tracking keeps dropping" report). A pet that dealt damage within
        // the grace window is visibly still ours; the hit is the twin.
        var s = Replay(
            At(0, 0, "A will sapper told you, 'Attacking orc pawn Master.'"),
            At(0, 10, "A will sapper slashes orc pawn for 20 points of damage."),
            At(0, 12, "A will sapper hits YOU for 15 points of damage."),
            At(0, 13, "A will sapper slashes orc pawn for 18 points of damage.")).Snapshot();

        Assert.Equal("Will sapper", s.PetName);
        // Both outgoing swings stayed credited to the pet.
        Assert.Equal(38, s.DamageBySource.Single(x => x.Name == "Pet (Will sapper)").Total);
    }

    [Fact]
    public void APetNamedHitAfterThePetGoesQuietDropsTheClaim()
    {
        // The real break: charm snaps, the pet stops helping and starts hitting you.
        // Its outgoing stream ends at the same moment, so a pet-named hit with the pet
        // idle past the grace window is the pet itself, hostile again.
        var s = Replay(
            At(0, 0, "A will sapper told you, 'Attacking orc pawn Master.'"),
            At(0, 10, "A will sapper slashes orc pawn for 20 points of damage."),
            At(0, 20, "A will sapper hits YOU for 15 points of damage.")).Snapshot();

        Assert.Equal("", s.PetName);
    }

    [Fact]
    public void FightCarriesThePetsOwnAbilitySplit()
    {
        // The pet stays ONE row in ByAbility; the per-fight split by the pet's own ability
        // lives beside it (Pet breakout window / History, 2026-08-06). Session-wide
        // PetAbilities already existed — this pins the per-fight counterpart.
        var s = Replay(
            At(0, 0, "Jibekn told you, 'Attacking orc pawn Master.'"),
            At(0, 1, "Jibekn slashes orc pawn for 20 points of damage."),
            At(0, 2, "Jibekn slashes orc pawn for 22 points of damage. (Critical)"),
            At(0, 3, "Jibekn hit orc pawn for 15 points of fire damage by Burst of Flame."),
            At(0, 4, "You slash orc pawn for 30 points of damage."),
            At(0, 5, "You have slain orc pawn!")).Snapshot();

        var f = s.LastFight!;
        Assert.Equal(57, f.ByAbility.Single(x => x.Name == "Pet (Jibekn)").Total);
        var slash = f.PetAbilities.Single(x => x.Name == "Slash");
        Assert.Equal(42, slash.Total);
        Assert.Equal(2, slash.Hits);
        Assert.Equal(1, slash.Crits);
        Assert.Equal(15, f.PetAbilities.Single(x => x.Name == "Burst of Flame").Total);
        // The pet split sums to the pet's single labeled row — no hit counted twice.
        Assert.Equal(f.ByAbility.Single(x => x.Name == "Pet (Jibekn)").Total,
            f.PetAbilities.Sum(x => x.Total));

        // Archived encounter carries it, and it survives the snapshot JSON round-trip.
        var restored = System.Text.Json.JsonSerializer.Deserialize<StatsSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(s))!;
        Assert.Equal(42, restored.Encounters.Single().PetAbilities.Single(x => x.Name == "Slash").Total);
    }

    /// <summary>Target-drops target pool (David's spec + live reports, 2026-08-06): open
    /// fights always win and ALL of them are the pool — the log can't say which one is
    /// targeted, and picking one made the window cycle with whoever swung last.
    /// Between fights: the NEWER of the last finished fight and the last /consider,
    /// each within the 45s linger.</summary>
    [Fact]
    public void ConsideringACreatureMakesItTheCurrentTarget()
    {
        var s = Replay(
            At(0, 0, "Orc pawn scowls at you, ready to attack -- looks like a reasonably safe opponent. (Lvl: 3)"))
            .Snapshot();
        Assert.Equal(["Orc pawn"], s.CurrentTargets);

        // A consider AFTER a kill retargets; an open fight still outranks both.
        var s2 = Replay(
            At(0, 0, "You slash a ghoul for 10 points of damage."),
            At(0, 4, "You have slain a ghoul!"),
            At(0, 10, "Orc pawn scowls at you, ready to attack -- looks like a reasonably safe opponent. (Lvl: 3)"),
            At(0, 20, "You slash a spite golem for 10 points of damage.")).Snapshot();
        Assert.Equal(["Spite golem"], s2.CurrentTargets);

        var s3 = Replay(
            At(0, 0, "You slash a ghoul for 10 points of damage."),
            At(0, 4, "You have slain a ghoul!"),
            At(0, 10, "Orc pawn scowls at you, ready to attack -- looks like a reasonably safe opponent. (Lvl: 3)"),
            At(0, 12, "You gain experience! (0.1%)")).Snapshot();
        Assert.Equal(["Orc pawn"], s3.CurrentTargets);
    }

    [Fact]
    public void AMultiCreaturePullPoolsEveryOpenFightInStableOrder()
    {
        // Both creatures stay in the pool, oldest fight first — the order must not
        // follow whoever swung most recently (that was the cycling bug).
        var s = Replay(
            At(0, 0, "You slash orc pawn for 10 points of damage."),
            At(0, 2, "Orc centurion hits YOU for 5 points of damage."),
            At(0, 4, "Orc centurion hits YOU for 5 points of damage.")).Snapshot();
        Assert.Equal(["Orc pawn", "Orc centurion"], s.CurrentTargets);
    }

    [Fact]
    public void AaPurchasesBuildALedgerThatSurvivesSessionResets()
    {
        // Verbatim shapes from Dranak/Hugzee logs 2026-08-06: rank 1 is "gained the
        // ability" with the name quoted, later ranks are "improved <name> <rank>".
        var stats = Replay(
            At(0, 0, "You have gained the ability \"Combat Fury\" at a cost of 1 ability points."),
            At(0, 5, "You have improved Combat Fury 2 at a cost of 2 ability points."),
            At(0, 9, "You have improved Combat Fury 3 at a cost of 3 ability points."),
            At(1, 0, "You have gained the ability \"Innate Divine Healing\" at a cost of 0 ability points."),
            At(2, 0, "You have improved Symphonic Aura: Enabled 4 at a cost of 0 ability points."));
        var s = stats.Snapshot();
        Assert.Equal(3, s.AaAbilities.Single(a => a.Name == "Combat Fury").Rank);
        Assert.Equal(1, s.AaAbilities.Single(a => a.Name == "Innate Divine Healing").Rank);
        Assert.Equal(4, s.AaAbilities.Single(a => a.Name == "Symphonic Aura: Enabled").Rank);

        // The ledger is character state: a session-gap reset keeps it (the full-log replay
        // crosses gap resets, and purchases before the gap must not be forgotten)...
        stats.Apply(LogParser.Parse(
            "[Sat Jul 18 17:30:00 2026] You slash a rat for 1 points of damage.")!);
        Assert.Equal(3, stats.Snapshot().AaAbilities.Single(a => a.Name == "Combat Fury").Rank);

        // ...while a character switch wipes it.
        stats.ClearCharacterState();
        Assert.Empty(stats.Snapshot().AaAbilities);
    }

    [Fact]
    public void AaStoreRemembersPurchasesTheLogHasLost()
    {
        // The janitor truncates quiet logs, erasing purchase lines for good — the durable
        // store is what still knows the ranks afterwards.
        var path = Path.Combine(Path.GetTempPath(), $"aa-ledger-test-{Guid.NewGuid():N}.json");
        try
        {
            var stats = new SessionStats
            {
                CharacterName = "Kaybek", ServerName = "freeport",
                AaStore = new AaLedgerStore(path),
            };
            stats.Apply(LogParser.Parse(
                At(0, 0, "You have improved Combat Fury 3 at a cost of 3 ability points."))!);
            stats.AaStore!.Flush();   // saves are debounced (audit #3); reload needs the write

            // Fresh stats over an emptied log (character switch semantics + nothing to
            // replay), same store file re-read from disk: the ledger still knows.
            var later = new SessionStats
            {
                CharacterName = "Kaybek", ServerName = "freeport",
                AaStore = new AaLedgerStore(path),
            };
            Assert.Equal(3, later.Snapshot().AaAbilities.Single(a => a.Name == "Combat Fury").Rank);

            // Another character on the same install sees none of Kaybek's AAs.
            var other = new SessionStats
            {
                CharacterName = "Douglas", ServerName = "qeynos",
                AaStore = new AaLedgerStore(path),
            };
            Assert.Empty(other.Snapshot().AaAbilities);

            // Replaying an OLD log (rank 2 after the store knows 3) never regresses.
            later.Apply(LogParser.Parse(
                At(0, 5, "You have improved Combat Fury 2 at a cost of 2 ability points."))!);
            Assert.Equal(3, later.Snapshot().AaAbilities.Single(a => a.Name == "Combat Fury").Rank);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OverlappingFightsGroupIntoOnePullAndGapsSplitThem()
    {
        var s = Replay(
            At(0, 0, "You slash orc pawn for 30 points of damage."),
            At(0, 2, "Orc centurion hits YOU for 5 points of damage."),   // add joins the pull
            At(0, 4, "You have slain orc pawn!"),
            At(0, 6, "You slash orc centurion for 40 points of damage."),
            At(0, 8, "You have slain orc centurion!"),
            At(1, 0, "You slash a ghoul for 25 points of damage."),       // 52s later: new pull
            At(1, 2, "You have slain a ghoul!")).Snapshot();

        var pulls = EncounterGrouping.Group(s.Encounters);
        Assert.Equal(2, pulls.Count);

        var pull = pulls[0];
        Assert.Equal("Orc pawn + Orc centurion", pull.Title);
        Assert.Equal(70, pull.DamageOut);
        // Your damage merges by ability; incoming keeps the creature's name because
        // the pull has more than one.
        Assert.Equal(70, pull.ByAbility.Single(x => x.Name == "Slash").Total);
        Assert.Equal(5, pull.ByIncoming.Single(x => x.Name == "Orc centurion: Hit").Total);

        Assert.Equal("Ghoul", pulls[1].Title);
        // Single-creature pull: incoming rows stay unprefixed (none here, though).
        Assert.Equal(25, pulls[1].ByAbility.Single(x => x.Name == "Slash").Total);
    }

    [Fact]
    public void TheLiveCardShowsTheCurrentPullNotOneCreature()
    {
        // Pawn is dead but the add is still swinging: the "current fight" is the PULL —
        // dead pawn included — not just the creature touched most recently.
        var s = Replay(
            At(0, 0, "You slash orc pawn for 30 points of damage."),
            At(0, 2, "Orc centurion hits YOU for 5 points of damage."),
            At(0, 4, "You have slain orc pawn!"),
            At(0, 6, "You slash orc centurion for 40 points of damage.")).Snapshot();

        var f = s.LastFight!;
        Assert.True(f.InProgress);
        Assert.Equal("Orc pawn + Orc centurion", f.Name);
        Assert.Equal(70, f.DamageOut);
        Assert.Equal(70, f.ByAbility.Single(x => x.Name == "Slash").Total);
        Assert.Equal(5, f.ByIncoming.Single(x => x.Name == "Orc centurion: Hit").Total);
        Assert.Equal(2, f.Fights.Count);
    }

    [Fact]
    public void SameNamedAddsCountInThePullTitle()
    {
        var fights = new List<EncounterInfo>
        {
            new("Orc pawn", DateTime.Parse("2026-07-18T15:00:00"), 5, 30, 0, 6, "Killed"),
            new("Orc pawn", DateTime.Parse("2026-07-18T15:00:03"), 5, 30, 0, 6, "Killed"),
        };
        Assert.Equal("Orc pawn ×2", EncounterGrouping.Group(fights).Single().Title);
    }

    /// <summary>Issue #39 end-to-end: a "mote" loot watch rule must fire when the mote
    /// routes to currency storage — joeymavity's exact line, previously invisible.</summary>
    [Fact]
    public void AMoteLootRuleFiresOnCurrencyStoredMotes()
    {
        var stats = Replay(
            At(0, 0, "You looted a Mote of Major Potential from a spite golem's corpse and stored it in your currency"));
        var rules = new[] { new TrackedRule { Name = "Motes", Pattern = "mote", Kind = WatchKind.Loot } };
        var r = Assert.Single(stats.Snapshot(null, rules).Tracked);
        Assert.Equal(1, r.TotalQuantity);
    }

    [Fact]
    public void KillWatchRuleCountsAndBreaksDown()
    {
        var stats = Replay(
            At(0, 0, "You have slain orc pawn!"),
            At(0, 10, "You have slain orc centurion!"),
            At(0, 20, "You have slain a ghoul!"));
        var rules = new[] { new TrackedRule { Name = "Orcs", Pattern = "orc", Kind = WatchKind.Kill } };
        var r = Assert.Single(stats.Snapshot(null, rules).Tracked);
        Assert.Equal(2, r.TotalQuantity);
        Assert.Equal(2, r.Items.Count);
    }

    [Fact]
    public void NameOnlyRuleFallsBackToNameAsPattern()
    {
        // Users often type the match text into the name box and leave the pattern
        // empty; the rule must still match instead of being silently skipped.
        var stats = Replay(
            At(0, 0, "You have slain a ghoul!"),
            At(0, 10, "You have slain orc pawn!"));
        var rules = new[] { new TrackedRule { Name = "Ghoul", Kind = WatchKind.Kill } };
        var r = Assert.Single(stats.Snapshot(null, rules).Tracked);
        Assert.Equal(1, r.TotalQuantity);
        Assert.Equal("Ghoul", r.Name);
    }

    [Fact]
    public void SpellFadeRuleAlertsOnMezOrCharmBreak()
    {
        // Real EQL lines: the caster sees spell wear-offs with spell + target names.
        var s = Replay(
            At(0, 0, "Your Befriend Animal spell has worn off of a puma."),
            At(0, 30, "Your Chords of Dissonance spell has worn off of a giant spider."),
            At(1, 0, "Your Root spell has worn off."));
        var rules = new[] { new TrackedRule { Name = "Charm", Pattern = "Befriend", Kind = WatchKind.SpellFade } };
        var r = Assert.Single(s.Snapshot(null, rules).Tracked);
        Assert.Equal(1, r.TotalQuantity);
        Assert.Equal("Befriend Animal (Puma)", Assert.Single(r.Items).Name);

        // Name-only rule + the targetless variant both work.
        var root = new[] { new TrackedRule { Name = "Root", Kind = WatchKind.SpellFade } };
        Assert.Equal("Root", Assert.Single(s.Snapshot(null, root).Tracked).Items.Single().Name);
    }

    [Fact]
    public void MilestoneWatchRuleMatchesWithEmptyPattern()
    {
        var stats = Replay(
            At(0, 0, "You have gained a level! Welcome to level 12!"),
            At(0, 10, "You have gained an ability point!  You now have 3 ability points."));
        var rules = new[] { new TrackedRule { Name = "Dings", Kind = WatchKind.Milestone } };
        Assert.Equal(2, Assert.Single(stats.Snapshot(null, rules).Tracked).TotalQuantity);
    }

    [Fact]
    public void DeathWatchRuleFiltersByKiller()
    {
        var stats = Replay(
            At(0, 0, "You have been slain by a greater mummy!"),
            At(0, 30, "You have been slain by orc taskmaster!"));
        var all = new[] { new TrackedRule { Name = "Deaths", Kind = WatchKind.Death } };
        var mummy = new[] { new TrackedRule { Name = "MummyDeaths", Pattern = "mummy", Kind = WatchKind.Death } };
        Assert.Equal(2, Assert.Single(stats.Snapshot(null, all).Tracked).TotalQuantity);
        Assert.Equal(1, Assert.Single(stats.Snapshot(null, mummy).Tracked).TotalQuantity);
    }
}
