using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>
/// EQBuddy Lite: the whole UI is this one always-on-top panel — your DPS, your pet's
/// DPS, motes looted, and a group board read from your own log (no network). The
/// engine underneath is the unmodified, audited EQBuddy.Core.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SessionStats _stats = new();
    private readonly LogWatcher _watcher;
    private readonly GroupDpsTracker _group = new();
    private readonly ThirdPartyLedger _ledger = new();
    private readonly DamageFeed _feed = new();
    private readonly AudioCues _cues;
    /// <summary>Last tick's pet name — the pet-break cue fires on the claim VANISHING,
    /// which is Core's single point of truth for every way a pet is lost.</summary>
    private string _lastPetName = "";
    private readonly GroupSync _sync = new();
    private readonly SpawnTimers _spawnTimers;
    private readonly LiteUiSettings _ui = LiteUiSettings.Load();
    private BreakdownPopup? _popup;
    private StatsSnapshot? _snap;

    /// <summary>One row of the FIGHTS list; Key is the fight's Start ticks — the stable
    /// identity a repeat of the same mob name can't fake.</summary>
    private sealed record FightRow(string Text, long Key);

    /// <summary>One row of the SPAWNS list; Zone+Name identify the timer.</summary>
    private sealed record SpawnRow(string Name, string Due, string Zone);

    // ---- detachable sections: tear off by dragging a heading, magnetise by dropping
    //      near another EQdps window's bottom edge, ✕ to rejoin the panel ----

    private const double DockGap = 6;
    // No longer a static truth: FEED windows are spawnable, so their keys ("feed",
    // "feed2", …) join this list from FeedPanes at startup and as the user adds them.
    private readonly List<string> SectionKeys = ["motes", "loot", "fights", "spawns", "group", "group2"];
    private readonly Dictionary<string, SectionWindow> _sectionWindows = new();
    /// <summary>Every OPEN feed pane, whether it is a window of its own or a tab inside
    /// one. Closed panes live on in <see cref="LiteUiSettings.FeedPanes"/> with their
    /// settings, but have no view until they are reopened.</summary>
    private readonly Dictionary<string, FeedView> _feedViews = new();

    /// <summary>The feed WINDOWS, by the key of the pane that names each one. A host
    /// draws one of its panes at a time and a tab strip for the rest.</summary>
    private readonly Dictionary<string, FeedHost> _feedHosts = new();

    private FrameworkElement SectionElement(string key) =>
        _feedHosts.TryGetValue(key, out var host) ? host.Root : key switch
        {
            "motes" => MotesSection,
            "loot" => LootSection,
            "fights" => FightsSection,
            "spawns" => SpawnSection,
            "group2" => Group2Section,
            _ => GroupSection,
        };

    private bool Attached(string key) => !_sectionWindows.ContainsKey(key);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>The FEED windows repaint on their own clock, ten times a second, because
    /// they show a stream rather than a set of numbers: on the 1 s panel tick a burst of
    /// combat arrived as one lump a second after the game showed it, which read as the
    /// feed lagging the fight. Everything else on the panel is a rate or a total that
    /// only means anything once a second anyway.
    ///
    /// It costs what an idle frame costs — each view asks its buffer for "anything after
    /// sequence N", gets nothing, and returns. Normal priority, not the DispatcherTimer
    /// default of Background: a background timer this short is starved by the layout work
    /// the panel tick kicks off, which is exactly when the feed is busiest.</summary>
    private readonly DispatcherTimer _feedTimer =
        new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };

    private DateTime _lastCharScan = DateTime.MinValue;
    private DateTime _lastJanitor = DateTime.MinValue;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private UpdateInfo? _pendingUpdate;
    private bool _installing;

    /// <summary>What a synced member had already banked when you last reset the session.
    /// The relay only ever reports running totals — a member's app has no idea you reset
    /// — so counting "from now on" like the rest of the panel does is a subtraction we
    /// do here, in memory. Null <see cref="_resetAt"/> means no reset this run, and the
    /// board shows their totals untouched.</summary>
    private readonly record struct MemberBaseline(long Damage, double CombatSeconds, int Motes, bool Exact,
        IReadOnlyDictionary<string, int> TierBase);
    private readonly Dictionary<string, MemberBaseline> _groupBaseline = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _resetAt;

    public MainWindow()
    {
        InitializeComponent();
        VersionItem.Header = $"EQdps v{UpdateChecker.CurrentVersion}";
        VersionText.Text = $"v{UpdateChecker.CurrentVersion}";

        // Same UiScale the full app persists: the corner grip scales the whole panel
        // and SizeToContent re-fits the window around it.
        RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(_settings.UiScale, MinScale, MaxScale);

        if (_settings.LogFolder is { } saved && !Directory.Exists(saved))
            _settings.LogFolder = null; // stale saved path (game moved) — re-detect
        _settings.LogFolder ??= LogWatcher.FindDefaultLogFolder();

        // Restore the saved spot only when it's still on a live monitor.
        if (WindowPlacement.IsReachable(_settings.WindowLeft, _settings.WindowTop,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - 290;
            Top = 60;
        }

        // Same spawn-timer wiring as the full app: catalog + user overrides + a persistence
        // file for countdowns longer than a log's lifetime. LogWatcher.Select feeds it the
        // server name, and the parser feeds it kills/zones/sightings.
        _spawnTimers = new SpawnTimers(SpawnCatalog.LoadEmbedded(),
            SpawnOverrides.Load(AppPaths.File("spawn-overrides.json")),
            AppPaths.File("spawn-timers.json"));

        _cues = new AudioCues(_ui);
        _watcher = new LogWatcher(_stats)
        {
            Tap = e => { _group.Apply(e); _ledger.Apply(e); _feed.Apply(e); },
            RawTap = line => { _feed.ApplyRaw(line); _cues.OnLine(line); },
            Spawns = _spawnTimers,
        };
        FollowCharacter(force: true);

        // Log hygiene at startup, same promises as the full app: force Log=1 and wipe
        // finished-session logs — both stand down while the game (or GINA/GamParse) runs.
        RunJanitor();

        WireSection(MotesHeader, "motes", () => _ui.ShowMotes = !_ui.ShowMotes);
        WireSection(LootHeader, "loot", () => _ui.ShowLoot = !_ui.ShowLoot);
        WireSection(FightsHeader, "fights", () => _ui.ShowFights = !_ui.ShowFights);
        WireSection(SpawnHeader, "spawns", () => _ui.ShowSpawns = !_ui.ShowSpawns);
        WireSection(GroupLabel, "group", () => _ui.ShowGroup = !_ui.ShowGroup);
        WireSection(Group2Label, "group2", () => _ui.ShowGroup2 = !_ui.ShowGroup2);
        _feed.SetCapacity(_ui.FeedHistory);

        // FEED windows come from the panes list — a settings file from before panes
        // existed seeds one from the legacy single-feed keys, so nobody's filters are
        // lost on the update.
        if (_ui.FeedPanes.Count == 0)
            _ui.FeedPanes.Add(new FeedPane
            {
                Key = "feed",
                Filters = _ui.FeedFilters,
                Rows = _ui.FeedRows,
                Show = _ui.ShowFeed,
            });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pane in _ui.FeedPanes.ToList())
            if (string.IsNullOrEmpty(pane.Key) || !seen.Add(pane.Key))
                _ui.FeedPanes.Remove(pane);   // a hand-edited duplicate; drop it
        // Closed panes are remembered, not deleted — but the app must never come up with
        // no feed at all, or there is no + to press and no menu to reopen from.
        if (_ui.FeedPanes.Count > 0 && _ui.FeedPanes.TrueForAll(pane => pane.Closed))
            _ui.FeedPanes[0].Closed = false;
        RebuildFeedSections();
        Loaded += (_, _) => SetupSectionWindows();
        LocationChanged += (_, _) => { RepositionFollowers(this); RefreshPopupPosition(); };
        SizeChanged += (_, _) => { RepositionFollowers(this); RefreshPopupPosition(); };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _feedTimer.Tick += (_, _) => RenderFeeds();
        _feedTimer.Start();
        _sync.Start();
        CheckUpdates();

        Closing += (_, _) =>
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.UiScale = RootScale.ScaleX;
            _settings.Save();
            foreach (var (key, win) in _sectionWindows)
                _ui.SectionPositions[key] = [win.Left, win.Top];
            _ui.Save();
            _popup?.Close();
            foreach (var win in _sectionWindows.Values.ToList()) win.Close();
            _watcher.Dispose();
            _sync.Dispose();
        };
    }

    private void FollowCharacter(bool force)
    {
        if (_settings.LogFolder is not { } lf) return;
        var m = LogWatcher.MostRecentlyActive(lf);
        if (m is null) return;
        if (force || !string.Equals(m.FilePath, _watcher.CurrentPath, StringComparison.OrdinalIgnoreCase))
            _watcher.Select(m.FilePath, ResumeOffset(m.FilePath), long.MaxValue);
    }

    /// <summary>Where to start reading this log. Normally the top; after a session reset,
    /// the point that reset happened — otherwise a restart replays the very lines you
    /// cleared and hands back the session you just ended. The mark is dropped if the log
    /// has since been emptied or rotated (the offset would land mid-nowhere), or if it
    /// belongs to a different character.</summary>
    private long ResumeOffset(string path)
    {
        if (_ui.ResetLogOffset <= 0 ||
            !string.Equals(_ui.ResetLogPath, path, StringComparison.OrdinalIgnoreCase))
            return 0;
        try
        {
            return new FileInfo(path).Length >= _ui.ResetLogOffset ? _ui.ResetLogOffset : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void RunJanitor()
    {
        if (_settings.LogFolder is not { } lf) return;
        _lastJanitor = DateTime.Now;
        var prune = _settings.TruncateLogs;
        var archive = _settings.ArchiveLogs;
        Task.Run(() =>
        {
            EqConfig.EnsureLoggingEnabled(lf);
            if (prune) EqConfig.TruncateStaleLogs(lf, SessionStats.SessionGap, archive: archive);
        });
    }

    private void Tick()
    {
        var now = DateTime.Now;
        if (now - _lastCharScan > TimeSpan.FromSeconds(5))
        {
            _lastCharScan = now;
            FollowCharacter(force: false);
        }
        if (now - _lastJanitor > TimeSpan.FromMinutes(10)) RunJanitor();
        if (now - _lastUpdateCheck > TimeSpan.FromHours(6)) CheckUpdates();
        if (_statusUntil is { } until && now > until && _pendingUpdate is null)
        {
            _statusUntil = null;
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        var s = _stats.Snapshot();
        _snap = s; // the popups read fight details from the latest snapshot

        // Class combo, derived from the AA ledger: each owned AA names its class in the
        // catalog, and the ledger persists per character — so the combo fills in as AAs
        // are seen and sticks. Classes with no AAs observed yet stay unknown.
        var combo = ClassCombo(s.AaAbilities);
        TitleText.Text = string.IsNullOrEmpty(_stats.CharacterName) ? "EQdps"
            : combo.Length > 0 ? $"{_stats.CharacterName} · {combo}"
            : _stats.CharacterName;
        StatusDot.Fill = _watcher.LastGrowth is { } g && now - g < TimeSpan.FromSeconds(30)
            ? Brushes.LimeGreen : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        // The headline is ALWAYS the current fight (or the last one while idle) — the
        // old scope dropdown flipped every number on the panel at once, but with both
        // GROUP boards now live side by side, session numbers have a permanent home
        // and the big meter can stay a fight meter.
        var lastFight = s.LastFight;

        // The dim suffix is how long the data behind the number spans — the fight's
        // length — so a rate is never read without its denominator.
        void SetDps(string main, TimeSpan? span)
        {
            DpsText.Inlines.Clear();
            DpsText.Inlines.Add(new System.Windows.Documents.Run(main));
            if (span is { TotalSeconds: >= 1 } t)
                DpsText.Inlines.Add(new System.Windows.Documents.Run($"   {FmtDur(t)}")
                {
                    FontSize = 12,
                    FontWeight = FontWeights.Normal,
                    Foreground = MoteDim,
                });
        }

        if (lastFight is null)
            SetDps("⚔ 0 dps", null);
        else
            SetDps($"⚔ {lastFight.Dps:0} dps" + (lastFight.InProgress ? "" : "  (last fight)"),
                TimeSpan.FromSeconds(lastFight.DurationSeconds));

        // Your breakdown lives in a side popup (key "own:") so a long ability list
        // never pushes the sections below it down the screen. The popup carries BOTH
        // scopes now that the headline no longer switches; the count here is the
        // session's, the fuller of the two lists.
        if (s.DamageBySource.Count > 0)
        {
            var open = _popup?.MemberName == "own:";
            DamageHeader.Text = $"{(open ? "▾" : "▸")} DAMAGE · fight + session · "
                + $"{s.DamageBySource.Count} source{(s.DamageBySource.Count == 1 ? "" : "s")}";
            DamageHeader.Visibility = Visibility.Visible;
        }
        else DamageHeader.Visibility = Visibility.Collapsed;

        // Charm provenance when Core proved it (blink/charmed/glaze landings), with how
        // long the charm has held; the charm spell rides the tooltip. A pet without a
        // seen charm landing shows plain — the log can't say which class summoned it.
        var charmTag = "";
        if (s.PetCharmed)
        {
            var dur = s.PetSince is { } since ? FmtDur(now - since) : "";
            charmTag = dur.Length > 0 ? $" · charmed {dur}" : " · charmed";
        }
        // Pet dps follows the headline: always the current fight.
        var petRows = lastFight?.PetAbilities ?? [];
        var petSeconds = lastFight?.DurationSeconds ?? 1;
        var petDamage = petRows.Sum(p => p.Total);
        PetText.Text = s.PetName.Length > 0
            ? $"🐾 {s.PetName}{charmTag}: {petDamage / Math.Max(1, petSeconds):0.#} dps"
            : petDamage > 0 ? $"🐾 pet: {petDamage / Math.Max(1, petSeconds):0.#} dps"
            : "🐾 no pet";
        PetText.ToolTip = s.PetCharmSpell is { Length: > 0 } charmSpell
            ? $"Charmed with {charmSpell}" : null;

        // Motes as their own heading, tier list behind the same click-to-expand — and
        // the group's hauls underneath, because motes are a loot race and reading them
        // one member-popup at a time made that impossible to see. Sync is the only
        // source for other players: your log records nobody else's loot lines.
        var motes = Motes.Summarize(s.Loot, s.Elapsed);
        var groupMotes = (_ui.ShowGroupMotes ? _sync.Members : [])
            .Where(m => !IsSelf(m.Name))
            .Select(m => (m.Name, Total: ScopedMotes(m), m.Motes.PerHour, Tiers: ScopedTiers(m),
                m.SessionSeconds))
            .Where(m => m.Total > 0)
            .OrderByDescending(m => m.Total)
            .Take(8)
            .ToList();
        var motesExpandable = motes.Total > 0 || groupMotes.Count > 0;
        // Racing = rows actually on the board; a you-row with nothing looted isn't one.
        var racers = groupMotes.Count + (motes.Total > 0 ? 1 : 0);
        var sharingTag = groupMotes.Count == 0 ? ""
            : _resetAt is null ? $" · {racers} racing"
            : $" · {racers} racing since reset";
        MotesHeader.Text = motesExpandable
            ? $"{(_ui.ShowMotes ? "▾" : "▸")} MOTES · {motes.Total} ({motes.PerHour:0.#}/h){sharingTag}"
            : "MOTES · none yet";
        if (motesExpandable && _ui.ShowMotes)
        {
            // A member's shared per-hour covers their whole session; after a reset it
            // would contradict the rebased count beside it, so recompute over the time
            // you have been counting.
            var moteHours = _resetAt is { } from
                ? Math.Max((now - from).TotalHours, 1.0 / 60)
                : 0;
            RenderMotesTable(motes, groupMotes, moteHours, s.Elapsed);
            MotesTable.Visibility = Visibility.Visible;
        }
        else MotesTable.Visibility = Visibility.Collapsed;

        // Section separators only make sense inside the main panel — a torn-off
        // window has its own chrome.
        MotesTopSep.Visibility = Attached("motes") ? Visibility.Visible : Visibility.Collapsed;
        SpawnTopSep.Visibility = Attached("spawns") ? Visibility.Visible : Visibility.Collapsed;
        GroupTopSep.Visibility = Attached("group") ? Visibility.Visible : Visibility.Collapsed;
        Group2TopSep.Visibility = Attached("group2") ? Visibility.Visible : Visibility.Collapsed;

        // Session loot (motes excluded — they have their own line above), collapsed to
        // a one-line heading by default.
        var loot = s.Loot.Where(l => !Motes.IsMote(l.Item)).ToList();
        if (loot.Count == 0 && !Attached("loot"))
        {
            // A torn-off window with nothing in it still needs to say what it is.
            LootHeader.Text = "LOOT · none yet";
            LootHeader.Visibility = Visibility.Visible;
            LootList.Visibility = Visibility.Collapsed;
        }
        else if (loot.Count > 0)
        {
            var pieces = loot.Sum(l => l.Count);
            LootHeader.Text = $"{(_ui.ShowLoot ? "▾" : "▸")} LOOT · {pieces} item{(pieces == 1 ? "" : "s")}";
            LootHeader.Visibility = Visibility.Visible;
            if (_ui.ShowLoot)
            {
                LootList.ItemsSource = loot
                    .OrderByDescending(l => l.Count)
                    .ThenBy(l => l.Item, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(l => $"{Pad(l.Item, 18)} ×{l.Count}")
                    .ToList();
                LootList.Visibility = Visibility.Visible;
            }
            else LootList.Visibility = Visibility.Collapsed;
        }
        else
        {
            LootHeader.Visibility = Visibility.Collapsed;
            LootList.Visibility = Visibility.Collapsed;
        }
        LootTopSep.Visibility = Attached("loot") && LootHeader.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // Past fights of this session, newest first; click a row for that fight's popup.
        // Only fights where you actually dealt damage count — getting pierced by a
        // passing ghoul opens an encounter in Core, but it isn't a fight to review.
        var realFights = s.Encounters.Where(f => f.DamageOut > 0).ToList();
        if (realFights.Count == 0 && !Attached("fights"))
        {
            FightsHeader.Text = "FIGHTS · none yet";
            FightsHeader.Visibility = Visibility.Visible;
            FightsList.Visibility = Visibility.Collapsed;
        }
        else if (realFights.Count > 0)
        {
            FightsHeader.Text = $"{(_ui.ShowFights ? "▾" : "▸")} FIGHTS · {realFights.Count} this session";
            FightsHeader.Visibility = Visibility.Visible;
            if (_ui.ShowFights)
            {
                FightsList.ItemsSource = realFights
                    .TakeLast(8)
                    .Reverse()
                    .Select(f => new FightRow($"{Pad(f.Name, 14)} {f.Dps,5:0} dps", f.Start.Ticks))
                    .ToList();
                FightsList.Visibility = Visibility.Visible;
            }
            else FightsList.Visibility = Visibility.Collapsed;
        }
        else
        {
            FightsHeader.Visibility = Visibility.Collapsed;
            FightsList.Visibility = Visibility.Collapsed;
        }
        FightsTopSep.Visibility = Attached("fights") && FightsHeader.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

        // Spawn timers: soonest first (Core's Snapshot order), section hidden entirely
        // when no camp is running — an empty list isn't worth panel height.
        var timers = _spawnTimers.Snapshot(now);
        SpawnClearLink.Visibility = timers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (timers.Count > 0)
        {
            SpawnHeader.Text = $"{(_ui.ShowSpawns ? "▾" : "▸")} SPAWNS · {timers.Count}";
            SpawnSection.Visibility = Visibility.Visible;
            if (_ui.ShowSpawns)
            {
                SpawnList.ItemsSource = timers
                    .Take(6)
                    .Select(t => new SpawnRow(t.Name, FmtSpawn(t, now), t.Zone))
                    .ToList();
                SpawnList.Visibility = Visibility.Visible;
            }
            else SpawnList.Visibility = Visibility.Collapsed;
        }
        else if (!Attached("spawns"))
        {
            // "none running" alone reads as BROKEN when you have been killing for an
            // hour — but only NAMED spawns get a clock, and a trash camp produces none.
            // Saying what is being watched, and where, is the difference between a dead
            // section and an armed one (reported as "spawns aren't tracking again").
            SpawnHeader.Text = _spawnTimers.CurrentZone is { } watching
                ? $"SPAWNS · none running · watching {watching.Named.Count} named "
                    + $"in {watching.Zone}"
                : "SPAWNS · none running · zone unknown";
            SpawnList.Visibility = Visibility.Collapsed;
            SpawnSection.Visibility = Visibility.Visible;
        }
        else SpawnSection.Visibility = Visibility.Collapsed;

        // We publish every rate we have — live, current-fight, and session — so each
        // client's own scope dropdown can pick one without a round trip.
        _sync.Publish(new GroupSync.OwnStats(
            _stats.CharacterName ?? "", s.CurrentDps, lastFight?.Dps ?? 0, s.SessionDps,
            // The damage BEHIND SessionDps, not DamageDealt: pairing it with CombatSeconds
            // is what makes a receiver's "since your reset" rate mean the same thing as
            // the session rate beside it.
            (long)Math.Round(s.SessionDps * s.CombatSeconds), s.CombatSeconds,
            s.Elapsed.TotalSeconds,
            s.DamageBySource.Take(6).Select(d => new BreakdownEntry(d.Name, d.Total, d.Hits)).ToList(),
            motes));

        // Two GROUP boards, the same roster under two clocks: "group" is this fight,
        // "group2" the session. A popup opened from a board stays in that board's
        // scope (_popupSource) — each window's popup shows only its own specifics.
        RenderGroupBoard(fightMode: true, _ui.ShowGroup,
            GroupLabel, GroupList, GroupEmptyText, lastFight, s);
        RenderGroupBoard(fightMode: false, _ui.ShowGroup2,
            Group2Label, Group2List, Group2EmptyText, lastFight, s);

        _feed.PetName = s.PetName;
        // Pet-break cue: the claim was there and now is not. Startup replay is excluded
        // (InitialIngestDone), and so is the few seconds after a manual session reset —
        // resetting clears the pet by design, and a cue for it would cry wolf.
        if (_watcher.InitialIngestDone
            && !(_resetAt is { } reset && (now - reset).TotalSeconds < 5))
        {
            if (_lastPetName.Length > 0 && s.PetName.Length == 0) _cues.PetLost();
            _lastPetName = s.PetName;
        }
        foreach (var host in _feedHosts.Values)
            host.TopSep.Visibility = Attached(host.Key) ? Visibility.Visible : Visibility.Collapsed;
        RenderFeeds();

        // Everything above may have changed a section's height; re-seat the stack once
        // the layout pass has actually run, so a section that shrank this tick doesn't
        // leave the windows under it stranded.
        Dispatcher.BeginInvoke(() => { RepinStack(); RefreshPopupPosition(); },
            System.Windows.Threading.DispatcherPriority.Loaded);
        RefreshPopup();
    }

    /// <summary>The window a popup belongs beside: fight popups ride the FIGHTS window,
    /// spawn popups the SPAWNS window, member popups the GROUP window — wherever those
    /// have been dragged.</summary>
    /// <summary>Which window the open popup was launched from: "main", "group"
    /// (fight board), or "group2" (session board). It decides two things: where the
    /// popup parks (beside the window actually clicked), and what it SHOWS — a popup
    /// stays in its board's scope, fight specifics from the fight board, session
    /// specifics from the session board, both from the main panel.</summary>
    private string _popupSource = "main";

    private Window PopupAnchor(string key)
    {
        var section = key.StartsWith("fight:", StringComparison.Ordinal) ? "fights"
            : key.StartsWith("spawn:", StringComparison.Ordinal) ? "spawns"
            : _popupSource;
        if (section == "main") return this;
        return _sectionWindows.TryGetValue(section, out var win) && win.IsVisible ? win : this;
    }

    /// <summary>Park the open popup at the right edge of the window it belongs to.
    /// Called from every anchor's move/resize so it follows a drag live.</summary>
    internal void RefreshPopupPosition()
    {
        if (_popup is not { } popup) return;
        var anchor = PopupAnchor(popup.MemberName);
        popup.Left = anchor.Left + anchor.ActualWidth + 8;
        popup.Top = anchor.Top;
    }

    /// <summary>Keep the member popup parked beside its section window and fed with the
    /// latest synced numbers. Called every tick and when the popup opens.</summary>
    private void RefreshPopup()
    {
        if (_popup is not { } popup) return;
        RefreshPopupPosition();

        // Your own breakdown (keyed "own:"): every source, not a top-N. Which scopes
        // it shows follows where it was opened — the fight board's popup is fight
        // only, the session board's session only, the main panel's both stacked.
        if (popup.MemberName == "own:")
        {
            var sections = new List<string>();
            void Section(string title, IReadOnlyList<SourceDamage>? src)
            {
                var total = src?.Sum(d => d.Total) ?? 0;
                if (src is null || total == 0) return;
                if (sections.Count > 0) sections.Add("");
                sections.Add(title);
                sections.AddRange(src.Select(d =>
                    $"{Pad(d.Name, 13)} {d.Hits,4}× {FmtDamage(d.Total),6} {d.Total * 100 / total,3}%"));
            }
            if (_popupSource != "group2")
                Section($"— this fight · {_snap?.LastFight?.Dps ?? 0:0} dps —",
                    _snap?.LastFight?.ByAbility);
            if (_popupSource != "group")
                Section($"— session · {_snap?.SessionDps ?? 0:0} dps —", _snap?.DamageBySource);
            popup.Update(
                _stats.CharacterName is { Length: > 0 } cn ? cn : "You",
                sections.Count > 0 ? string.Join("\n", sections) : "(no damage yet)");
            return;
        }

        // A spawn popup (keyed "spawn:<zone>|<name>") reads from the timers.
        if (popup.MemberName.StartsWith("spawn:", StringComparison.Ordinal))
        {
            var parts = popup.MemberName[6..].Split('|', 2);
            var t = parts.Length == 2
                ? _spawnTimers.Snapshot(DateTime.Now).FirstOrDefault(x =>
                    string.Equals(x.Zone, parts[0], StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Name, parts[1], StringComparison.OrdinalIgnoreCase))
                : null;
            if (t is null)
            {
                popup.Update("spawn", "(timer expired or cleared)");
                return;
            }
            var detail = $"zone    {t.Zone}\nkilled  {t.KilledAt:HH:mm:ss}";
            detail += t.DueAt is { } due
                ? $"\ndue     {due:HH:mm:ss}"
                : "\ndue     unknown respawn time";
            if (t is { CampLocY: { } y, CampLocX: { } x })
                detail += $"\ncamp    /loc {y:0}, {x:0}";
            popup.Update($"{t.Name} · {FmtSpawn(t, DateTime.Now)}", detail);
            return;
        }

        // A fight popup (keyed "fight:<start ticks>") reads from the snapshot; the
        // member popups below read from sync.
        if (popup.MemberName.StartsWith("fight:", StringComparison.Ordinal))
        {
            if (!long.TryParse(popup.MemberName.AsSpan(6), out var ticks)) return;
            var f = _snap?.Encounters.FirstOrDefault(en => en.Start.Ticks == ticks);
            if (f is null)
            {
                popup.CopyText = null;
                popup.Update("fight", "(no longer tracked — session\n pruned or reset)");
                return;
            }
            var abilityTotal = f.ByAbility.Sum(b => b.Total);
            var detail =
                $"{FmtDur(TimeSpan.FromSeconds(f.DurationSeconds))} · {f.Outcome} · {FmtDamage(f.DamageOut)} dmg"
                + (f.DamageIn > 0 ? $" · took {FmtDamage(f.DamageIn)}" : "")
                + (abilityTotal > 0
                    ? "\n" + string.Join("\n", f.ByAbility.Take(8).Select(b =>
                        $"{Pad(b.Name, 13)} {b.Hits,4}× {FmtDamage(b.Total),6} {b.Total * 100 / abilityTotal,3}%"))
                    : "");
            // What the group did on this same mob in this window, from your log.
            var others = _ledger.DamageOn(f.Name, f.Start,
                TimeSpan.FromSeconds(f.DurationSeconds), _snap?.PetName);
            if (others.Count > 0)
                detail += "\n\ngroup on this fight · from log\n" + string.Join("\n",
                    others.Take(6).Select(g =>
                        $"{Pad(g.Name, 13)} {g.Hits,4}× {FmtDamage(g.Total),6} {g.Total / Math.Max(1, f.DurationSeconds),4:0} dps"));
            // ⧉ copies only the per-player summary — you plus everyone the ledger saw
            // on this pull, highest dps first. That's the line worth pasting to the
            // group; the ability table above is for reading, not sharing.
            var board = new List<(string Name, double Dps, long Total)>
            {
                (_stats.CharacterName is { Length: > 0 } cn ? cn : "you", f.Dps, f.DamageOut),
            };
            board.AddRange(others.Select(g =>
                (g.Name, g.Total / (double)Math.Max(1, f.DurationSeconds), g.Total)));
            popup.CopyText = $"{f.Name} ({FmtDur(TimeSpan.FromSeconds(f.DurationSeconds))}): "
                + string.Join(", ", board.OrderByDescending(b => b.Dps)
                    .Select(b => $"{b.Name} {b.Dps:0} dps ({FmtDamage(b.Total)})"));
            popup.Update($"{f.Name} · {f.Dps:0} dps", detail);
            return;
        }

        var member = _ui.GroupBoardUseSync
            ? _sync.Members.FirstOrDefault(m =>
                m.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase))
            : null;

        // Opened from the FIGHT board: this-fight specifics only. The synced fight
        // rate when their app shares one, plus what YOUR log saw of them on the
        // current/last pull — per-ability fight detail never crosses the wire, and
        // session numbers belong to the session board's popups.
        if (_popupSource == "group")
        {
            var lf = _snap?.LastFight;
            long dmg = 0;
            var hits = 0;
            if (lf is not null)
                foreach (var fight in lf.Fights)
                    foreach (var g in _ledger.DamageOn(fight.Name, fight.Start,
                                 TimeSpan.FromSeconds(fight.DurationSeconds), _snap?.PetName))
                        if (g.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase))
                        {
                            dmg += g.Total;
                            hits += g.Hits;
                        }
            var dur = Math.Max(1.0, lf?.DurationSeconds ?? 1);
            var syncedFight = member is not null ? ScopedDps(member, true) : 0;
            var headDps = syncedFight > 0 ? syncedFight : dmg / dur;
            var body = dmg > 0
                ? $"on this pull · from your log\n{hits,4}× {FmtDamage(dmg),6} · {dmg / dur:0} dps"
                : "(nothing seen on this pull)";
            popup.Update(
                $"{member?.Name ?? "~" + popup.MemberName} · this fight · {headDps:0} dps",
                body);
            return;
        }

        if (member is null)
        {
            // Not synced — show what YOUR log knows about them instead (the ~ rows):
            // real numbers, just incomplete by nature.
            var logRow = _group.Snapshot(DateTime.Now, _snap?.PetName)
                .FirstOrDefault(r => r.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase));
            if (logRow is not null)
            {
                var logTotal = logRow.Breakdown.Sum(b => b.Total);
                var lines = $"from your log · approximate\nsession {FmtDamage(logRow.SessionDamage)} dmg";
                if (logTotal > 0)
                    lines += "\n" + string.Join("\n", logRow.Breakdown.Take(8).Select(b =>
                        $"{Pad(b.Name, 13)} {b.Hits,4}× {FmtDamage(b.Total),6} {b.Total * 100 / logTotal,3}%"));
                popup.Update(
                    $"~{logRow.Name} · {ScopedDps(logRow, false, _snap?.CombatSeconds ?? 0):0} dps · session",
                    lines);
                return;
            }
            popup.Update(popup.MemberName,
                _sync.Active
                    ? "(no data — not in your log and\n not sharing via group sync)"
                    : "(not in your log — exact numbers\n need group sync)");
            return;
        }

        string rows;
        var total = member.Breakdown.Sum(b => b.Total);
        if (total > 0)
        {
            rows = string.Join("\n", member.Breakdown.Select(b =>
                $"{Pad(b.Name, 13)} {b.Hits,4}× {FmtDamage(b.Total),6} {b.Total * 100 / total,3}%"));
            // Only per-source totals come over sync, so unlike the headline these rows
            // can't be counted from your reset — say so rather than let them look stale.
            if (_resetAt is not null) rows = "their whole session\n" + rows;
        }
        else
        {
            // Their app shared no breakdown (older version, or nothing yet) — fall
            // back to what YOUR log saw of them before declaring a blank.
            var seen = _group.Snapshot(DateTime.Now, _snap?.PetName)
                .FirstOrDefault(r => r.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase));
            var seenTotal = seen?.Breakdown.Sum(b => b.Total) ?? 0;
            if (seen is not null && seenTotal > 0)
                rows = "from your log · approximate\n" + string.Join("\n",
                    seen.Breakdown.Take(8).Select(b =>
                        $"{Pad(b.Name, 13)} {b.Hits,4}× {FmtDamage(b.Total),6} {b.Total * 100 / seenTotal,3}%"));
            else if (member.Dps > 0 || member.SessionDps > 0)
                rows = "(no breakdown shared — their\n app may be an older version)";
            else
                rows = "(no damage yet)";
        }

        // Their motes are NOT here — they live beside yours in the MOTES section, where
        // the whole group's hauls can be compared at a glance.
        // Session board's popup, so the session clock only — the rows below are
        // per-source session totals, which matches.
        popup.Update($"{member.Name} · session · {ScopedDps(member, false):0} dps", rows);
    }

    // ---- FEED: lives in FeedView (one instance per spawned feed window) ----

    /// <summary>Repaint every FEED window. Called from the 100 ms feed timer and from the
    /// panel tick, since a tick can change what the views want to show (the pet name a
    /// row is attributed by, a section becoming attached).</summary>
    private void RenderFeeds()
    {
        foreach (var host in _feedHosts.Values) host.Render();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool IsSelf(string name) =>
        string.Equals(name, _stats.CharacterName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A synced member's row under the current scope. "Each fight" prefers the
    /// current-or-last fight rate they shared; clients older than that field send 0, so
    /// we fall back to their live rate (which reads 0 out of combat — the best the old
    /// protocol could say). "Session" counts from your last reset when there has been
    /// one, so the board restarts with the rest of the panel instead of carrying their
    /// running total across your fresh session.</summary>
    private double ScopedDps(SyncedMember m, bool fightMode)
    {
        if (fightMode) return m.FightDps > 0 ? m.FightDps : m.Dps;
        if (_resetAt is not { } since) return m.SessionDps;

        var b = BaselineFor(m);
        var damage = DamageOf(m).Damage - b.Damage;
        // Their own combat clock keeps the number comparable to what their app shows;
        // without one (pre-1.56 client) wall clock since your reset is the honest
        // substitute, and it reads low while they idle.
        var seconds = m.CombatSeconds > 0
            ? m.CombatSeconds - b.CombatSeconds
            : (DateTime.Now - since).TotalSeconds;
        return damage <= 0 || seconds <= 0 ? 0 : damage / seconds;
    }

    /// <summary>Their mote haul under the same rule: everything they have, or everything
    /// since your reset.</summary>
    private int ScopedMotes(SyncedMember m) =>
        _resetAt is null ? m.Motes.Total : Math.Max(0, m.Motes.Total - BaselineFor(m).Motes);

    /// <summary>Their per-tier counts, rebased against the same baseline as the total —
    /// mismatched halves (a rebased headline over whole-session tiers) would contradict
    /// each other on the board. Tiers at or below their mark drop out entirely.</summary>
    private IReadOnlyList<MoteEntry> ScopedTiers(SyncedMember m)
    {
        if (_resetAt is null) return m.Motes.Tiers;
        var baseTiers = BaselineFor(m).TierBase;
        return m.Motes.Tiers
            .Select(t => new MoteEntry(t.Name, t.Count - baseTiers.GetValueOrDefault(t.Name)))
            .Where(t => t.Count > 0)
            .ToList();
    }

    /// <summary>Cumulative damage a member has shared. 1.56+ sends the real total; before
    /// that the best available is the sum of their top sources, which undercounts the
    /// long tail — flagged so a baseline never mixes the two.</summary>
    private static (long Damage, bool Exact) DamageOf(SyncedMember m) =>
        m.SessionDamage > 0 ? (m.SessionDamage, true) : (m.Breakdown.Sum(b => b.Total), false);

    /// <summary>The mark to subtract from. Re-taken whenever a member's totals fall below
    /// it (they reset their own session) or switch source (they updated mid-session) —
    /// either way the old mark is meaningless and a stale one would report nonsense.</summary>
    private MemberBaseline BaselineFor(SyncedMember m)
    {
        var (damage, exact) = DamageOf(m);
        if (!_groupBaseline.TryGetValue(m.Name, out var b) || b.Exact != exact
            || damage < b.Damage || m.CombatSeconds < b.CombatSeconds || m.Motes.Total < b.Motes)
            _groupBaseline[m.Name] = b = new MemberBaseline(damage, m.CombatSeconds, m.Motes.Total, exact,
                m.Motes.Tiers.ToDictionary(t => t.Name, t => t.Count, StringComparer.OrdinalIgnoreCase));
        return b;
    }

    /// <summary>The GROUP board, session-scoped in practice (fightMode stays for the
    /// fight-scope math the popups reuse via ScopedDps/InferredRows). With sync off
    /// (or the ⚙ toggle pinning the board to your log) your own row leads: nothing
    /// else on a local board carries your number, and a comparison starts with
    /// yourself.</summary>
    private void RenderGroupBoard(bool fightMode, bool expanded, TextBlock label,
        ItemsControl list, TextBlock empty, LastFightInfo? lastFight, StatsSnapshot s)
    {
        var boardTag = fightMode ? "this fight"
            : _resetAt is null ? "session" : "session since reset";
        List<string> rows;
        // The ⚙ toggle can pin the board to your own log even while sync runs — sync
        // still publishes your numbers; this is only which side you look at.
        if (_sync.Active && _ui.GroupBoardUseSync)
        {
            label.Text = (expanded ? "▾ " : "▸ ") + (_sync.LastError is { } err
                ? $"GROUP · {boardTag} · sync {_sync.GroupCode} · {err}"
                : $"GROUP · {boardTag} · synced · {_sync.GroupCode}");
            var synced = _sync.Members;
            var syncedNames = new HashSet<string>(synced.Select(m => m.Name),
                StringComparer.OrdinalIgnoreCase);
            rows = synced.Select(m => $"{Pad(m.Name, 12)} {ScopedDps(m, fightMode),5:0} dps").ToList();
            // Players near you who aren't running the app still show, marked approximate.
            rows.AddRange(InferredRows(fightMode, lastFight, s)
                .Where(r => !syncedNames.Contains(r.Name))
                .Select(r => $"{Pad("~" + r.Name, 12)} {r.Dps,5:0} dps"));
            rows = rows.Take(8).ToList();
            empty.Text = "(waiting for group…)";
        }
        else
        {
            label.Text = (expanded ? "▾ " : "▸ ") + $"GROUP · {boardTag} · from your log";
            var ownDps = fightMode ? lastFight?.Dps ?? 0 : s.SessionDps;
            rows = InferredRows(fightMode, lastFight, s)
                .Take(7)
                .Select(r => $"{Pad("~" + r.Name, 12)} {r.Dps,5:0} dps")
                .ToList();
            if (ownDps > 0)
                rows.Insert(0, $"{Pad(_stats.CharacterName is { Length: > 0 } cn ? cn : "you", 12)} "
                    + $"{ownDps,5:0} dps");
            empty.Text = "(no group activity nearby)";
        }
        list.ItemsSource = rows;
        list.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = expanded && rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Log-inferred board rows for the current scope. "Each fight" reads the
    /// ledger's damage on the current/last pull's creatures over the pull's duration —
    /// the same numbers the fight popup shows — because the old 60-second sliding
    /// window meant "right now", which is a third thing the scope dropdown never
    /// offered. "Session" divides their running damage by YOUR combat seconds, the
    /// span you were fighting together; ~ rows carry no clock of their own.</summary>
    private List<(string Name, double Dps)> InferredRows(bool fightMode, LastFightInfo? lastFight,
        StatsSnapshot s)
    {
        if (!fightMode)
            return _group.Snapshot(DateTime.Now, s.PetName)
                .Select(r => (r.Name, r.SessionDamage / Math.Max(1.0, s.CombatSeconds)))
                .OrderByDescending(r => r.Item2)
                .ToList();

        if (lastFight is null || lastFight.Fights.Count == 0) return [];
        var byPlayer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var fight in lastFight.Fights)
            foreach (var (name, _, total) in _ledger.DamageOn(
                         fight.Name, fight.Start, TimeSpan.FromSeconds(fight.DurationSeconds), s.PetName))
                byPlayer[name] = byPlayer.GetValueOrDefault(name) + total;
        return byPlayer
            .Select(kv => (kv.Key, kv.Value / Math.Max(1.0, lastFight.DurationSeconds)))
            .OrderByDescending(r => r.Item2)
            .ToList();
    }

    /// <summary>A log-inferred member's row under the current scope. Your log gives them
    /// a 60-second sliding window (the "right now" number) and a session total, but no
    /// clock of their own — so session mode divides their damage by YOUR combat seconds,
    /// which is the span you were fighting together. Approximate, like every ~ row.</summary>
    private static double ScopedDps(GroupMemberDps r, bool fightMode, double combatSeconds) =>
        fightMode ? r.WindowDps : r.SessionDamage / Math.Max(1, combatSeconds);

    private static string Pad(string s, int width) =>
        s.Length >= width ? s[..width] : s.PadRight(width);

    private static string FmtDur(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0}s"
        : t.TotalHours < 1 ? $"{t.TotalMinutes:0}m"
        : $"{(int)t.TotalHours}h{t.Minutes:00}m";

    /// <summary>EQ's short class codes in the conventional archetype order.</summary>
    private static readonly string[] ClassOrder =
        ["war", "pal", "shd", "rng", "mnk", "rog", "brd", "bst", "ber",
         "clr", "dru", "shm", "enc", "mag", "nec", "wiz"];

    private static readonly Dictionary<string, string> ClassShort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Warrior"] = "war", ["Paladin"] = "pal", ["Shadow Knight"] = "shd",
        ["Shadowknight"] = "shd", ["Ranger"] = "rng", ["Monk"] = "mnk", ["Rogue"] = "rog",
        ["Bard"] = "brd", ["Beastlord"] = "bst", ["Berserker"] = "ber", ["Cleric"] = "clr",
        ["Druid"] = "dru", ["Shaman"] = "shm", ["Enchanter"] = "enc", ["Magician"] = "mag",
        ["Necromancer"] = "nec", ["Wizard"] = "wiz",
    };

    /// <summary>"mnk/shm/enc" from the classes of the AAs this character owns.</summary>
    private static string ClassCombo(IEnumerable<AaAbilityInfo> ledger)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in ledger)
            if (AaCatalog.Find(a.Name)?.Class is { Length: > 0 } cls)
                found.Add(ClassShort.TryGetValue(cls, out var code)
                    ? code : cls[..Math.Min(3, cls.Length)].ToLowerInvariant());
        return string.Join("/",
            ClassOrder.Where(found.Contains)
                .Concat(found.Where(f => !ClassOrder.Contains(f)).OrderBy(x => x, StringComparer.Ordinal)));
    }

    private static string FmtDamage(long total) => total switch
    {
        >= 1_000_000 => $"{total / 1_000_000.0:0.#}M",
        >= 10_000 => $"{total / 1000.0:0}k",
        >= 1_000 => $"{total / 1000.0:0.#}k",
        _ => total.ToString(),
    };

    /// <summary>"DUE" / countdown / "killed N ago" for a spawn row, in the same
    /// spirit as the full app's chips: mm:ss under an hour, h:mm above.</summary>
    private static string FmtSpawn(SpawnTimerState t, DateTime now)
    {
        if (t.DueAt is not { } due)
        {
            var ago = now - t.KilledAt;
            return ago.TotalHours >= 1 ? $"† {ago.TotalHours:0}h ago" : $"† {ago.TotalMinutes:0}m ago";
        }
        if (now >= due) return "DUE!";
        var left = due - now;
        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
            : $"{left.Minutes}:{left.Seconds:00}";
    }

    private static readonly Brush MoteGold = Frozen(0xD9, 0xC4, 0x6B);
    private static readonly Brush MoteBright = Frozen(0xE8, 0xCE, 0x9C);
    private static readonly Brush MoteDim = Frozen(0x7B, 0x87, 0x94);
    private static readonly Brush MoteFaint = Frozen(0x4A, 0x54, 0x5E);
    private static readonly Brush MoteMemberName = Frozen(0xCF, 0xE3, 0xF5);

    /// <summary>The MOTES board as one aligned table — players down, tiers across,
    /// then total, rate, and how LONG each player took to gather it. Your row leads; a
    /// dim dot marks a tier a player hasn't seen. The time column exists because a
    /// haul without its timeframe reads as skill when it might be a head start: 43
    /// motes over six hours and 12 over one are the same pace.</summary>
    private void RenderMotesTable(MotesSummary yours,
        List<(string Name, int Total, double PerHour, IReadOnlyList<MoteEntry> Tiers,
            double SessionSeconds)> members,
        double rebasedHours, TimeSpan yourElapsed)
    {
        var rows = new List<(string Name, bool IsYou, int Total, double Rate,
            TimeSpan Span, Dictionary<string, int> ByTier)>();
        // Your row shows even at ZERO whenever anyone else is on the board. A missing
        // row reads as "the app has stopped counting my motes", which is exactly how a
        // 60-minute break got reported as a bug: SessionStats rolls the session on that
        // gap, so motes looted before it belong to the previous one, while the group's
        // numbers come from THEIR apps and keep counting. Zero is information, and the
        // time column beside it says which window each row is measuring.
        if (yours.Total > 0 || members.Count > 0)
            rows.Add((_stats.CharacterName is { Length: > 0 } cn ? cn : "You",
                true, yours.Total, yours.PerHour, yourElapsed,
                yours.Tiers.ToDictionary(t => TierShort(t.Item), t => t.Count)));
        foreach (var m in members)
        {
            // After your reset every member column counts from that mark, so the span
            // is yours-since-reset too. Otherwise it's their whole session: shared
            // directly by 1.63+ clients, recovered from total ÷ per-hour for older
            // ones (that's exactly how their app computed the rate).
            var span = rebasedHours > 0 ? TimeSpan.FromHours(rebasedHours)
                : m.SessionSeconds > 0 ? TimeSpan.FromSeconds(m.SessionSeconds)
                : m.PerHour > 0 ? TimeSpan.FromHours(m.Total / m.PerHour)
                : TimeSpan.Zero;
            rows.Add((m.Name, false, m.Total,
                rebasedHours > 0 ? m.Total / rebasedHours : m.PerHour,
                span,
                m.Tiers.ToDictionary(t => TierShort(t.Name), t => t.Count)));
        }

        // Union of everyone's tiers in ladder order — first-seen order breaks down the
        // moment two players hold disjoint tiers.
        var tiers = rows
            .SelectMany(r => r.ByTier.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Motes.LadderRank)
            .ToList();

        MotesTable.Children.Clear();
        MotesTable.RowDefinitions.Clear();
        MotesTable.ColumnDefinitions.Clear();
        for (var c = 0; c < tiers.Count + 4; c++)
            MotesTable.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r <= rows.Count; r++)
            MotesTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void Cell(int row, int col, string text, Brush brush, double size = 11.5,
            bool right = true, bool bold = false, string? tip = null)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = brush,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(col == 0 ? 0 : 11, row == 0 ? 0 : 2, 0, 0),
                ToolTip = tip,
            };
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            MotesTable.Children.Add(tb);
        }

        for (var c = 0; c < tiers.Count; c++)
            Cell(0, c + 1, tiers[c], MoteDim, size: 10.5,
                // TierShort is invertible, so the header can name the actual item.
                tip: tiers[c] == "Base" ? "Mote of Potential — the tierless base mote"
                    : $"Mote of {tiers[c]} Potential");
        Cell(0, tiers.Count + 1, "all", MoteDim, size: 10.5, tip: "Total motes");
        Cell(0, tiers.Count + 2, "/h", MoteDim, size: 10.5, tip: "Motes per hour");
        Cell(0, tiers.Count + 3, "time", MoteDim, size: 10.5,
            tip: "How long this player has been collecting — their session length, or "
                 + "time since your reset when the board is rebased");

        for (var r = 0; r < rows.Count; r++)
        {
            var (name, isYou, total, rate, span, byTier) = rows[r];
            Cell(r + 1, 0, name, isYou ? MoteBright : MoteMemberName,
                size: 12, right: false, bold: isYou);
            for (var c = 0; c < tiers.Count; c++)
                Cell(r + 1, c + 1,
                    byTier.TryGetValue(tiers[c], out var n) ? n.ToString() : "·",
                    byTier.ContainsKey(tiers[c]) ? MoteGold : MoteFaint);
            Cell(r + 1, tiers.Count + 1, total.ToString(), MoteBright, bold: true);
            Cell(r + 1, tiers.Count + 2, $"{rate:0.#}", MoteDim, size: 10.5);
            Cell(r + 1, tiers.Count + 3,
                span.TotalSeconds >= 1 ? FmtDur(span) : "?", MoteDim, size: 10.5);
        }
    }

    /// <summary>"Mote of Greater Potential" → "Greater"; the tierless base mote → "Base".</summary>    /// <summary>"Mote of Greater Potential" → "Greater"; the tierless base mote → "Base".</summary>
    private static string TierShort(string item)
    {
        var t = item.Replace("Mote of", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Potential", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
        return t.Length == 0 ? "Base" : t;
    }

    // ---- updates: same fail-closed UpdateChecker as always, pointed at your fork ----

    /// <summary>Look for a newer build. <paramref name="userAsked"/> is the difference
    /// between the silent six-hourly poll and the menu item: a menu item that answers
    /// nothing at all when you are already up to date is indistinguishable from a menu
    /// item that did not register the click, so the manual path narrates itself —
    /// checking, then the verdict, which clears itself a few seconds later.</summary>
    private void CheckUpdates(bool userAsked = false)
    {
        _lastUpdateCheck = DateTime.Now;
        if (userAsked) ShowUpdateStatus("Checking for updates…", TimeSpan.FromSeconds(20));
        Task.Run(async () =>
        {
            UpdateInfo? info = null;
            var failed = false;
            try
            {
                info = await UpdateChecker.FindBestAsync(_settings.UpdateFolder);
            }
            catch (Exception ex)
            {
                // FindBestAsync swallows a dead network already, so reaching here means
                // something else went wrong — worth saying rather than reporting
                // "up to date" on the strength of a failure.
                CoreLog.Error(ex);
                failed = true;
            }

            var newer = info is not null && UpdateChecker.IsNewer(info);
            Dispatcher.Invoke(() =>
            {
                if (newer)
                {
                    _pendingUpdate = info;
                    _statusUntil = null;   // an offer stays put until it is acted on
                    UpdateBanner.Text = info!.SetupPath is not null || info.DownloadUrl is not null
                        ? $"Update v{info.Latest} is ready — click to install."
                        : $"Update v{info.Latest} is available — click to open the release page.";
                    UpdateBanner.Visibility = Visibility.Visible;
                    return;
                }
                if (!userAsked) return;   // the background poll stays quiet
                ShowUpdateStatus(
                    failed
                        ? "Couldn't check for updates — see error.log."
                        : $"You're on the latest version (v{UpdateChecker.CurrentVersion}).",
                    TimeSpan.FromSeconds(6));
            });
        });
    }

    /// <summary>A transient line in the update banner. Cleared by the panel tick once
    /// <see cref="_statusUntil"/> passes, so it needs no timer of its own — and never
    /// while a real update is being offered, which must not time out.</summary>
    private DateTime? _statusUntil;

    private void ShowUpdateStatus(string text, TimeSpan linger)
    {
        if (_pendingUpdate is not null) return;
        UpdateBanner.Text = text;
        UpdateBanner.Visibility = Visibility.Visible;
        _statusUntil = DateTime.Now + linger;
    }

    private void OnUpdateClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_pendingUpdate is not { } info || _installing) return;

        if (info.SetupPath is null && info.DownloadUrl is null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    UpdateChecker.GitHubLatestPage) { UseShellExecute = true });
            }
            catch (Exception ex) { CoreLog.Error(ex); }
            return;
        }

        _installing = true;
        UpdateBanner.Text = "Downloading update — EQBuddy will restart itself…";
        Task.Run(async () =>
        {
            try
            {
                var staged = await UpdateChecker.StageForInstall(info);
                System.Diagnostics.Process.Start(staged, UpdateChecker.SilentInstallArgs(Environment.ProcessPath));
                Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
            catch (Exception ex)
            {
                CoreLog.Error(ex);
                Dispatcher.Invoke(() =>
                {
                    _installing = false;
                    UpdateBanner.Text = "Update failed to start — see error.log.";
                });
            }
        });
    }

    // ---- window chrome ----

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        SaveLayout();
    }

    /// <summary>Persist window positions the moment a drag or resize ends — waiting
    /// for a clean shutdown loses the layout to crashes and killed processes.</summary>
    internal void SaveLayout()
    {
        // A hidden (⚙-unticked) window is skipped: its live DockHost is null while
        // hidden, and banking that would turn "hidden" into "floating" on relaunch.
        // (This loop went missing in v1.58 — docks silently stopped being re-banked
        // after startup; restored in 1.68.)
        foreach (var (key, win) in _sectionWindows)
        {
            if (!win.IsVisible) continue;
            _ui.SectionPositions[key] = [win.Left, win.Top];
            _ui.SectionDocks[key] = DockKey(win.DockHost);
            if (win.DockSide == DockSide.Below) _ui.SectionDockSides.Remove(key);
            else _ui.SectionDockSides[key] = win.DockSide == DockSide.Right ? "right" : "left";
        }
        _ui.Save();
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.UiScale = RootScale.ScaleX;
        _settings.Save();
    }

    // ---- corner resize: the grip drags a scale factor, not a window edge ----

    private const double MinScale = 0.6;
    private const double MaxScale = 2.5;
    private Point _gripOrigin;
    private double _gripScale;
    private double _gripWidth;

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // the grip must not start a window drag
        if (e.ClickCount == 2)
        {
            RootScale.ScaleX = RootScale.ScaleY = 1.0;
            foreach (var win in _sectionWindows.Values) win.SetScale(1.0);
            return;
        }
        _gripOrigin = PointToScreen(e.GetPosition(this));
        _gripScale = RootScale.ScaleX;
        _gripWidth = Math.Max(1, ActualWidth);
        ResizeGrip.CaptureMouse();
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!ResizeGrip.IsMouseCaptured) return;
        var p = PointToScreen(e.GetPosition(this));
        // Growth tracks the drag like a real corner would: pulling the corner out by
        // half the panel's width makes the panel half again as big.
        var drag = ((p.X - _gripOrigin.X) + (p.Y - _gripOrigin.Y)) / 2;
        RootScale.ScaleX = RootScale.ScaleY =
            Math.Clamp(_gripScale * (_gripWidth + drag) / _gripWidth, MinScale, MaxScale);
        foreach (var win in _sectionWindows.Values) win.SetScale(RootScale.ScaleX);
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!ResizeGrip.IsMouseCaptured) return;
        ResizeGrip.ReleaseMouseCapture();
        e.Handled = true;
        SaveLayout();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCheckUpdatesMenu(object sender, RoutedEventArgs e) =>
        CheckUpdates(userAsked: true);

    /// <summary>Heading gesture: a plain click toggles the section open/closed; a drag
    /// past the threshold tears the section off into its own window (or, if already
    /// torn off, drags that window).</summary>
    private void WireSection(FrameworkElement heading, string key, Action toggle)
    {
        var pressed = false;
        var start = default(Point);
        heading.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pressed = true;
            start = e.GetPosition(heading);
            heading.CaptureMouse();
        };
        heading.MouseMove += (_, e) =>
        {
            if (!pressed || !heading.IsMouseCaptured) return;
            var p = e.GetPosition(heading);
            if (Math.Abs(p.X - start.X) + Math.Abs(p.Y - start.Y) < 9) return;
            pressed = false;
            heading.ReleaseMouseCapture();
            if (_sectionWindows.TryGetValue(key, out var win)) win.BeginUserDrag();
            else Detach(key, tearOff: true);
        };
        heading.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            e.Handled = true;
            pressed = false;
            heading.ReleaseMouseCapture();
            toggle();
            Tick();
        };
    }

    private void Detach(string key, bool tearOff)
    {
        if (!Attached(key)) return;
        var el = SectionElement(key);
        RootStack.Children.Remove(el);
        var win = new SectionWindow(key, el, this);
        win.SetScale(RootScale.ScaleX);
        // A FEED window's ✕ closes it (remembering its settings); every shipped
        // section's ✕ keeps its "hook back under the stack" meaning.
        if (_feedHosts.ContainsKey(key))
            win.CloseOverride = () =>
            {
                if (!_feedHosts.TryGetValue(key, out var host)) return;
                // The LAST feed window can't be closed — there would be no + left to
                // press and no menu to reopen from — so its ✕ keeps the old meaning and
                // hooks it back under the stack.
                if (_ui.FeedPanes.Count(p => !p.Closed) <= host.Views.Count)
                {
                    DockToStack(win);
                    return;
                }
                foreach (var view in host.Views.ToList()) CloseFeedPane(view);
            };
        var at = Mouse.GetPosition(this);
        win.Left = Left + at.X - 24;
        win.Top = Top + at.Y - 10;
        _sectionWindows[key] = win;
        win.Show();
        Tick();
        if (tearOff) win.BeginDragDeferred();
    }

    /// <summary>The ✕ on a section window: hook it back under the stack that starts at
    /// the main window (below whatever is already chained there).</summary>
    internal void DockToStack(SectionWindow w)
    {
        w.DockHost = null;
        Window tail = this;
        var extended = true;
        while (extended)
        {
            extended = false;
            foreach (var f in _sectionWindows.Values)
            {
                if (ReferenceEquals(f, w) || !ReferenceEquals(f.DockHost, tail)) continue;
                tail = f;
                extended = true;
                break;
            }
        }
        Dock(w, tail, DockSide.Below);
        SaveLayout();   // the dock graph is remembered now, so every change to it is saved
    }

    /// <summary>Where a feed window sat, so a successor can take its place exactly.</summary>
    private sealed record SectionSlot(double Left, double Top, Window? Host, DockSide Side,
        List<SectionWindow> Followers);

    /// <summary>Bring the feed windows into line with the panes. One function does every
    /// structural change — a new window, a new tab, a tab detached, two windows merged,
    /// one closed or reopened — because they are all the same operation: work out which
    /// panes are open, which of them name a window, and make the UI say so. Views are
    /// keyed by pane and survive the rebuild, so a tab dragged between windows keeps its
    /// scrollback. Returns the windows that are new, for the caller to place.</summary>
    private List<string> RebuildFeedSections()
    {
        var open = _ui.FeedPanes.Where(p => !p.Closed && p.Key.Length > 0).ToList();
        var openKeys = open.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A host must be an open pane other than this one, and must host itself: tabs
        // never chain, so a settings file (or a merge of a merge) that says otherwise is
        // straightened out here rather than defended against everywhere else.
        foreach (var pane in open)
            if (pane.Host.Length > 0 &&
                (!openKeys.Contains(pane.Host) ||
                 pane.Host.Equals(pane.Key, StringComparison.OrdinalIgnoreCase)))
                pane.Host = "";
        foreach (var pane in open)
            if (pane.Host.Length > 0 && open.First(x => x.Key == pane.Host).Host.Length > 0)
                pane.Host = "";

        foreach (var pane in open)
            if (!_feedViews.ContainsKey(pane.Key))
                _feedViews[pane.Key] = new FeedView(this, _ui, _feed, pane);
        foreach (var key in _feedViews.Keys.Where(k => !openKeys.Contains(k)).ToList())
            _feedViews.Remove(key);

        var hostKeys = open.Where(p => p.Host.Length == 0).Select(p => p.Key).ToList();
        foreach (var key in _feedHosts.Keys.Where(k => !hostKeys.Contains(k)).ToList())
            DropFeedSection(key);

        var added = new List<string>();
        foreach (var key in hostKeys)
        {
            if (_feedHosts.ContainsKey(key)) continue;
            var host = new FeedHost(this, _ui, _feedViews[key].Pane);
            _feedHosts[key] = host;
            SectionKeys.Add(key);
            RootStack.Children.Add(host.Root);
            WireSection(host.DragBar, key, () => host.Pane.Show = !host.Pane.Show);
            added.Add(key);
        }

        // Two passes: every window lets go of the body it is showing before any window
        // takes one up, or a pane moving from one window to another would be claimed
        // while the old window still holds it.
        foreach (var host in _feedHosts.Values) host.ClearBody();
        foreach (var (key, host) in _feedHosts)
        {
            var views = open
                .Where(p => (p.Host.Length == 0 ? p.Key : p.Host)
                    .Equals(key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Order).ThenBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => _feedViews[p.Key])
                .ToList();
            foreach (var view in views) view.HostPane = host.Pane;
            host.SetViews(views);
            host.ApplyInnerWidth(_ui.SectionWidths.TryGetValue(key, out var w) ? w : double.NaN);
        }
        _ui.Save();
        return added;
    }

    /// <summary>Tear down a feed window whose pane no longer names one (it was closed, or
    /// merged into another window as a tab). Without a successor its followers bridge to
    /// its own host so the stack closes over the gap; with one they are handed over by
    /// <see cref="AdoptSlot"/> instead, and the returned slot says where to put it.</summary>
    private SectionSlot? DropFeedSection(string key, bool capture = false)
    {
        SectionSlot? slot = null;
        if (_sectionWindows.TryGetValue(key, out var win))
        {
            var followers = _sectionWindows.Values
                .Where(f => ReferenceEquals(f.DockHost, win)).ToList();
            if (capture)
            {
                slot = new SectionSlot(win.Left, win.Top, win.DockHost, win.DockSide, followers);
            }
            else
            {
                foreach (var follower in followers)
                {
                    follower.DockSide = win.DockSide;
                    follower.DockHost = win.DockHost ?? this;
                }
            }
            _sectionWindows.Remove(key);
            win.Close();
        }
        else if (_feedHosts.TryGetValue(key, out var host))
        {
            RootStack.Children.Remove(host.Root);
        }
        _feedHosts.Remove(key);
        SectionKeys.Remove(key);
        return slot;
    }

    /// <summary>Put a newly detached window into the place a departed one held, and hand
    /// it that window's followers — a tab taking over from the pane that named the window
    /// should leave the stack looking untouched.</summary>
    private void AdoptSlot(string key, SectionSlot slot)
    {
        if (!_sectionWindows.TryGetValue(key, out var win)) return;
        win.DockHost = slot.Host;
        win.DockSide = slot.Side;
        win.Left = slot.Left;
        win.Top = slot.Top;
        foreach (var follower in slot.Followers)
            if (!ReferenceEquals(follower, win)) follower.DockHost = win;
        RepositionFollowers(win);
    }

    /// <summary>The + on a FEED heading: another FEED window, starting as a copy of
    /// the clicked one's filters (set up a view, clone it, tweak the copy). It hooks
    /// under the stack's tail like any new section.</summary>
    /// <summary>A fresh pane cloned from an existing one — same filters, same colours,
    /// same size. A JSON round-trip is the cheapest deep copy of the settings bags, and
    /// sharing the instances would tie the two windows together.</summary>
    private FeedPane ClonePane(FeedView from, string host)
    {
        var n = 2;
        while (_ui.FeedPanes.Any(p => p.Key == "feed" + n)) n++;
        return new FeedPane
        {
            Key = "feed" + n,
            Rows = from.HostPane.Rows,
            Show = true,
            Host = host,
            Order = host.Length == 0 ? 0 : NextTabOrder(host),
            Filters = Copy(from.Pane.Filters) ?? new FeedFilters(),
            Colors = Copy(from.Pane.Colors) ?? new FeedColors(),
        };
    }

    private static T? Copy<T>(T value) => System.Text.Json.JsonSerializer.Deserialize<T>(
        System.Text.Json.JsonSerializer.Serialize(value));

    private int NextTabOrder(string host) =>
        _ui.FeedPanes.Where(p => p.Host == host).Select(p => p.Order + 1).DefaultIfEmpty(1).Max();

    /// <summary>The + on a FEED heading: another FEED WINDOW, starting as a copy of the
    /// tab that was in front.</summary>
    internal void SpawnFeedPane(FeedView from)
    {
        var pane = ClonePane(from, host: "");
        // Same width as the window it came from, not the 340 px default: the filter line
        // WRAPS on width, so a copy even slightly narrower can stand a row taller than
        // its source and the two could never be lined up however carefully they are
        // dragged. Matching the width is what makes the heights match.
        if (_ui.SectionWidths.TryGetValue(SectionKeyOf(from), out var srcWidth))
            _ui.SectionWidths[pane.Key] = srcWidth;
        _ui.FeedPanes.Add(pane);
        RebuildFeedSections();
        OpenFeedWindow(pane.Key, SectionKeyOf(from));
    }

    /// <summary>Right-click ▸ New tab: another pane inside THIS window, cloned from the
    /// tab in front — the game's own chat windows stack lenses this way, and a second
    /// view of the log rarely deserves a second rectangle of screen.</summary>
    internal void AddFeedTab(FeedHost host)
    {
        var pane = ClonePane(host.Active, host.Key);
        _ui.FeedPanes.Add(pane);
        RebuildFeedSections();
        if (_feedViews.TryGetValue(pane.Key, out var view)) host.Select(view);
        Tick();
    }

    /// <summary>Right-click ▸ Move this tab to its own window. The tab that NAMES the
    /// window is allowed to leave too: one of the tabs staying behind takes the window
    /// over, inheriting its place in the stack, and the departing pane gets a new one
    /// beside it. Without that, the one tab you cannot move would be the first one.</summary>
    internal void DetachFeedTab(FeedView view)
    {
        var hostKey = SectionKeyOf(view);
        if (!_feedHosts.TryGetValue(hostKey, out var host) || host.Views.Count < 2) return;

        var near = hostKey;
        SectionSlot? slot = null;
        if (view.Pane.Host.Length == 0)
        {
            var successor = host.Views.First(v => !ReferenceEquals(v, view)).Pane;
            slot = DropFeedSection(hostKey, capture: true);
            InheritSectionSettings(hostKey, successor.Key);
            foreach (var stays in host.Views)
                stays.Pane.Host = ReferenceEquals(stays.Pane, successor) ? "" : successor.Key;
            near = successor.Key;
        }
        view.Pane.Host = "";

        foreach (var key in RebuildFeedSections()) MakeFeedWindow(key);
        if (slot is not null) AdoptSlot(near, slot);
        PlaceFeedWindow(view.Key, near);
        RepinStack();
        SaveLayout();
        Tick();
    }

    /// <summary>Right-click ▸ Merge this window into ▸ …: every tab here becomes a tab
    /// there, and this window goes away.</summary>
    internal void MergeFeedWindow(FeedHost from, FeedHost into)
    {
        if (ReferenceEquals(from, into)) return;
        foreach (var view in from.Views.ToList())
        {
            view.Pane.Host = into.Key;
            view.Pane.Order = NextTabOrder(into.Key);
        }
        RebuildFeedSections();
        RepinStack();
        SaveLayout();
        Tick();
    }

    /// <summary>Give a host pane a window of its own, at the width remembered for it.</summary>
    private void MakeFeedWindow(string key)
    {
        if (!_feedHosts.TryGetValue(key, out var host) || _sectionWindows.ContainsKey(key)) return;
        // Draw the contents BEFORE the window wraps them: a feed list only gets its height
        // from a render, and a SizeToContent window built around an unrendered one comes
        // up as a heading with nothing under it.
        host.Render();
        Detach(key, tearOff: false);
        ApplySectionWidth(key, _ui.SectionWidths.TryGetValue(key, out var w) ? w : double.NaN);
        Refit(key);
    }

    /// <summary>Make a section window re-measure. SizeToContent can latch the size the
    /// content had when the window was created, and a window that has just adopted a
    /// different pane is exactly that case.</summary>
    private void Refit(string key) => Dispatcher.BeginInvoke(() =>
    {
        if (!_sectionWindows.TryGetValue(key, out var win)) return;
        win.SizeToContent = SizeToContent.Manual;
        win.SizeToContent = SizeToContent.WidthAndHeight;
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>Give a pane a window of its own and park it beside <paramref name="near"/>.</summary>
    private void OpenFeedWindow(string key, string near)
    {
        MakeFeedWindow(key);
        PlaceFeedWindow(key, near);
    }

    /// <summary>Park an existing feed window beside another one.</summary>
    private void PlaceFeedWindow(string key, string near)
    {
        if (!_sectionWindows.TryGetValue(key, out var win)) return;

        // BESIDE the window it came from, not under it: a full stack already reaches the
        // bottom of the screen, so a window added below it opens off-screen and reads as
        // the + having done nothing at all. Docked to that side rather than left loose,
        // so the two stay top-aligned and move together.
        var src = _sectionWindows.TryGetValue(near, out var s) && s.IsVisible ? (Window)s : this;
        win.DockHost = null;
        win.Left = src.Left + Math.Max(src.ActualWidth, 200) + DockGap;
        win.Top = src.Top;
        _ui.Save();
        Tick();
        // Its size is only known once it has laid out its rows, and which side it can
        // take depends on that size — so choose the side, then bank the layout.
        Dispatcher.BeginInvoke(() =>
        {
            SeatBeside(win, src);
            SaveLayout();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The window key a view is drawn in.</summary>
    private static string SectionKeyOf(FeedView view) =>
        view.Pane.Host.Length == 0 ? view.Key : view.Pane.Host;

    /// <summary>Feed windows other than this one, for the merge submenu.</summary>
    internal List<FeedHost> FeedHostsOtherThan(FeedHost host) =>
        _feedHosts.Values.Where(h => !ReferenceEquals(h, host)).ToList();

    /// <summary>Panes the user closed, newest first — the reopen submenu. Their filters,
    /// colours, and size are all still here; closing a feed no longer throws them away.</summary>
    internal List<FeedPane> ClosedFeedPanes() =>
        _ui.FeedPanes.Where(p => p.Closed).Reverse().ToList();

    internal void ReopenFeedPane(FeedPane pane)
    {
        var near = _feedHosts.Keys.FirstOrDefault() ?? "";
        pane.Closed = false;
        pane.Host = "";
        RebuildFeedSections();
        OpenFeedWindow(pane.Key, near);
    }

    /// <summary>A tab click on the active tab: the collapse toggle the old heading
    /// line used to carry.</summary>
    internal void ToggleFeedShow(FeedHost host)
    {
        host.Pane.Show = !host.Pane.Show;
        host.Render();
        Tick();
    }

    /// <summary>Right-click ▸ Rename (or double-click the tab): what this pane calls
    /// itself, on its tab and in every menu. Empty puts the derived name back.</summary>
    internal void RenameFeedPane(FeedView view)
    {
        var dlg = new RenameDialog(view.Title) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        view.Pane.Title = string.IsNullOrWhiteSpace(dlg.Value) ? null : dlg.Value.Trim();
        _ui.Save();
        RebuildFeedSections();
        Tick();
    }

    /// <summary>Right-click ▸ Text size: the row font for one feed WINDOW.</summary>
    internal void SetFeedFontSize(FeedHost host, double size)
    {
        host.Pane.FontSize = size;
        _ui.Save();
        foreach (var view in host.Views) view.Invalidate();
        host.Render();
        Refit(host.Key);
    }

    /// <summary>Right-click ▸ Chat layout: incoming right, outgoing left.</summary>
    internal void SetFeedSplitSides(FeedView view, bool on)
    {
        view.Pane.SplitSides = on;
        _ui.Save();
        view.Invalidate();   // every drawn row has to be re-sided
        RenderFeeds();
    }

    /// <summary>The elements a section's font override paints — the DATA rows only,
    /// never the heading: headings are chrome, and staying readable is how a section
    /// survives a wild font choice.</summary>
    private FrameworkElement[] SectionFontTargets(string key) => key switch
    {
        "motes" => [MotesTable],
        "loot" => [LootList],
        "fights" => [FightsList],
        "spawns" => [SpawnList],
        "group" => [GroupList, GroupEmptyText],
        "group2" => [Group2List, Group2EmptyText],
        _ => [],
    };

    /// <summary>What a section's rows are drawn in — the override, or Consolas.</summary>
    internal string SectionFontOf(string key) =>
        _ui.SectionFonts.TryGetValue(key, out var f) && f.Length > 0 ? f : "Consolas";

    /// <summary>Right-click ▸ Font on a section window — per WINDOW, like the feeds'.
    /// Picking the default removes the override rather than storing it.</summary>
    internal void SetSectionFont(string key, string family)
    {
        if (family.Length > 0 && !string.Equals(family, "Consolas", StringComparison.OrdinalIgnoreCase))
            _ui.SectionFonts[key] = family;
        else
            _ui.SectionFonts.Remove(key);
        _ui.Save();
        ApplySectionFont(key);
    }

    private void ApplySectionFont(string key)
    {
        // TextElement's inherited attached property, not Control.FontFamily: the motes
        // board is a bare Grid, which has no font of its own to set.
        var family = new FontFamily(SectionFontOf(key));
        foreach (var el in SectionFontTargets(key))
            System.Windows.Documents.TextElement.SetFontFamily(el, family);
    }

    /// <summary>Right-click ▸ Font: the row typeface for one feed WINDOW.</summary>
    internal void SetFeedFont(FeedHost host, string family)
    {
        host.Pane.FontFamily = family;
        _ui.Save();
        foreach (var view in host.Views) view.Invalidate();
        host.Render();
        Refit(host.Key);
    }

    /// <summary>Right-click ▸ Colours…: per-window row colours.</summary>
    /// <summary>A feed window's watch-tag matched a fresh line. Replay is silent: the
    /// startup ingest re-reads the whole log, and history must not ring the bell.</summary>
    internal void FeedAlert(FeedPane pane)
    {
        if (!_watcher.InitialIngestDone) return;
        _cues.FeedAlert(pane.Key, pane.AlertSound);
    }

    /// <summary>Right-click ▸ Alert tags…: per-window watch words and their sound.</summary>
    internal void EditFeedAlerts(FeedHost host)
    {
        var dlg = new FeedAlertsDialog(FeedHost.FeedTitle(host.Pane),
            host.Pane.AlertTags, host.Pane.AlertSound) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        host.Pane.AlertTags = dlg.Tags;
        host.Pane.AlertSound = dlg.Sound;
        _ui.Save();
        // Frames are judged at row-build time, so what is drawn must be re-judged.
        foreach (var view in host.Views) view.Invalidate();
        host.Render();
    }

    internal void EditFeedColors(FeedView view)
    {
        var dlg = new FeedColorsDialog(view.Title, view.Pane.Colors) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        foreach (var target in dlg.ApplyToAll ? _feedViews.Values.ToList() : [view])
        {
            target.Pane.Colors = Copy(dlg.Colors) ?? new FeedColors();
            target.ApplyColors();
        }
        _ui.Save();
        RenderFeeds();
    }

    /// <summary>Park a freshly spawned window against its source: to the right, or to the
    /// left when the right would hang off the screen (the panel's own default position is
    /// hard against a screen's right edge, so that is the common case). Docked, so it keeps
    /// its host's top edge from then on. If neither side fits — a source window wider than
    /// the space around it — it stays where it is, merely dragged back onto the desktop and
    /// left free-floating, which is what a spawned window always did.</summary>
    private void SeatBeside(SectionWindow w, Window src)
    {
        // The source's OWN screen, not the whole virtual desktop: the panel sits at the
        // right edge of a screen by default, and "there is room to the right" is true of
        // the desktop while meaning the new window opens on the next monitor over.
        var screen = Monitors.WorkAreaOf(src);
        var width = Math.Max(200, w.ActualWidth);
        if (src.Left + src.ActualWidth + DockGap + width <= screen.Right)
            Dock(w, src, DockSide.Right);
        else if (src.Left - DockGap - width >= screen.Left)
            Dock(w, src, DockSide.Left);
        else
            ClampToDesktop(w);
    }

    /// <summary>Slide a window back inside the virtual desktop if it would open past an
    /// edge — same reachability rule the main window uses for its saved position.</summary>
    private static void ClampToDesktop(Window w)
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        w.Left = Math.Clamp(w.Left, left, Math.Max(left, right - Math.Max(200, w.ActualWidth)));
        w.Top = Math.Clamp(w.Top, top, Math.Max(top, bottom - Math.Max(80, w.ActualHeight)));
    }

    /// <summary>Close a feed — the window's ✕ or a tab's. The pane is REMEMBERED, not
    /// deleted: its filters, colours, and size stay in the settings file so "reopen closed
    /// feed" brings back the window that was tuned. (Deleting was the old behaviour, and
    /// it meant a closed window's tuning was gone for good.) The last remaining feed is
    /// never closed — there would be no + left to press.
    ///
    /// Closing the pane that NAMES a window with tabs behind it promotes the first of
    /// them, which then takes over the window's place in the stack exactly.</summary>
    internal void CloseFeedPane(FeedView view)
    {
        var pane = view.Pane;
        if (!_feedViews.ContainsKey(pane.Key)) return;
        if (_ui.FeedPanes.Count(p => !p.Closed) <= 1) return;   // never the last one

        var successor = pane.Host.Length == 0
            ? _ui.FeedPanes.FirstOrDefault(p => !p.Closed && p.Host
                .Equals(pane.Key, StringComparison.OrdinalIgnoreCase))
            : null;
        var slot = successor is null ? null : DropFeedSection(pane.Key, capture: true);
        if (successor is not null)
        {
            successor.Host = "";
            InheritSectionSettings(pane.Key, successor.Key);
        }

        pane.Closed = true;
        pane.Host = "";
        _ui.HiddenSections.Remove(pane.Key);
        foreach (var key in RebuildFeedSections()) MakeFeedWindow(key);
        if (slot is not null && successor is not null) AdoptSlot(successor.Key, slot);
        RepinStack();
        SaveLayout();
        Tick();
    }

    /// <summary>Move everything remembered about one section window onto another key, so
    /// a promoted tab inherits the window it is taking over rather than starting fresh.</summary>
    private void InheritSectionSettings(string from, string to)
    {
        if (_ui.SectionPositions.Remove(from, out var pos)) _ui.SectionPositions[to] = pos;
        if (_ui.SectionDocks.Remove(from, out var dock)) _ui.SectionDocks[to] = dock;
        if (_ui.SectionDockSides.Remove(from, out var side)) _ui.SectionDockSides[to] = side;
        if (_ui.SectionWidths.TryGetValue(from, out var width)) _ui.SectionWidths[to] = width;
        // Anything docked to the old key by NAME now means the new one.
        foreach (var (key, host) in _ui.SectionDocks.ToList())
            if (host.Equals(from, StringComparison.OrdinalIgnoreCase)) _ui.SectionDocks[key] = to;
    }

    /// <summary>Magnetise: dropped near another EQdps window's bottom edge — or either
    /// SIDE of it — a section window aligns there and follows it from then on. Sideways
    /// docking is what lets a second column exist at all: two FEED windows side by side
    /// share a top edge exactly, instead of being nudged towards each other by hand and
    /// never quite landing on the same line.</summary>
    internal void SnapWindow(SectionWindow w)
    {
        const double snapX = 48, snapY = 28;
        w.DockHost = null;
        Window? best = null;
        var bestSide = DockSide.Below;
        var bestDist = double.MaxValue;
        foreach (var host in SnapHosts(w))
        foreach (var side in new[] { DockSide.Below, DockSide.Right, DockSide.Left })
        {
            var (x, y) = SeatOf(w, host, side);
            // The tolerances follow the axis the dock is ALONG: a side dock is aimed at
            // by its top edge (fine) and reached across the host's width (coarse), the
            // mirror of an under-dock.
            var dx = Math.Abs(w.Left - x);
            var dy = Math.Abs(w.Top - y);
            if (side == DockSide.Below ? dx > snapX || dy > snapY : dx > snapY || dy > snapX)
                continue;
            if (dx + dy >= bestDist) continue;
            bestDist = dx + dy;
            best = host;
            bestSide = side;
        }
        if (best is not null) Dock(w, best, bestSide);
        SaveLayout();
    }

    /// <summary>Hook a window onto a host on the given side and seat it there.</summary>
    private void Dock(SectionWindow w, Window host, DockSide side)
    {
        w.DockHost = host;
        w.DockSide = side;
        SeatOnHost(w);
        RepositionFollowers(w);
    }

    /// <summary>Where <paramref name="w"/> sits when docked to <paramref name="host"/> on
    /// the given side. A side dock shares the host's TOP edge (that is the alignment the
    /// eye reads across a row of windows); an under-dock shares its left edge.</summary>
    private (double X, double Y) SeatOf(Window w, Window host, DockSide side) => side switch
    {
        DockSide.Right => (host.Left + host.ActualWidth + DockGap, host.Top),
        DockSide.Left => (host.Left - DockGap - w.ActualWidth, host.Top),
        _ => (host.Left, host.Top + host.ActualHeight + DockGap),
    };

    /// <summary>Move a docked window to where its host says it belongs.</summary>
    private void SeatOnHost(SectionWindow w)
    {
        if (w.DockHost is not { } host) return;
        var (x, y) = SeatOf(w, host, w.DockSide);
        w.Left = x;
        w.Top = y;
    }

    /// <summary>A left-docked window hangs off its own right edge, so its position depends
    /// on its own width — the one case where a size change has to re-seat the window
    /// itself rather than its followers.</summary>
    internal void ReseatSelf(SectionWindow w)
    {
        if (w.DockSide == DockSide.Left && w.DockHost is not null) SeatOnHost(w);
    }

    private IEnumerable<Window> SnapHosts(SectionWindow w)
    {
        yield return this;
        foreach (var other in _sectionWindows.Values)
        {
            if (ReferenceEquals(other, w) || !other.IsVisible) continue;
            // No cycles: a window whose host chain leads back to w can't host w.
            var chain = other.DockHost;
            var cyclic = false;
            while (chain is SectionWindow link)
            {
                if (ReferenceEquals(link, w)) { cyclic = true; break; }
                chain = link.DockHost;
            }
            if (!cyclic) yield return other;
        }
    }

    // ---- section resize (the ◢ grip on every SectionWindow) ----

    private double _sectionResizeStartWidth;
    private int _sectionResizeStartRows;

    internal void BeginSectionResize(SectionWindow w)
    {
        _sectionResizeStartWidth = _ui.SectionWidths.TryGetValue(w.SectionKey, out var saved)
            ? saved
            : SectionElement(w.SectionKey).ActualWidth;
        _sectionResizeStartRows = _feedHosts.TryGetValue(w.SectionKey, out var v)
            ? v.Active.RowsClamped() : 0;
    }

    internal void SectionResizeDelta(SectionWindow w, double dx, double dy)
    {
        // Feeds read a whole log line, so they may grow to most of a monitor; the
        // number sections have nothing to show past ~720.
        var maxWidth = _feedHosts.ContainsKey(w.SectionKey) ? 2400 : 720;
        var width = Math.Clamp(_sectionResizeStartWidth + dx, 170, maxWidth);
        _ui.SectionWidths[w.SectionKey] = width;
        ApplySectionWidth(w.SectionKey, width);
        if (_feedHosts.TryGetValue(w.SectionKey, out var host))
        {
            // One text row per row-height of drag, at whatever size this window's font is.
            var rowHeight = Math.Max(8, host.Active.RowHeight);
            host.Pane.Rows = Math.Clamp(
                _sectionResizeStartRows + (int)Math.Round(dy / rowHeight), 4, 40);
            host.Render();
        }
    }

    internal void EndSectionResize()
    {
        _ui.Save();
        SaveLayout();   // followers moved with the new size; bank where they ended up
    }

    internal void ResetSectionSize(SectionWindow w)
    {
        _ui.SectionWidths.Remove(w.SectionKey);
        ApplySectionWidth(w.SectionKey, double.NaN);
        if (_feedHosts.TryGetValue(w.SectionKey, out var host))
        {
            host.Pane.Rows = 12;
            host.Render();
        }
        _ui.Save();
    }

    /// <summary>Give a section an explicit width (NaN = back to auto). A feed view's
    /// inner pieces track it — its list takes the width as fixed, so the window holds
    /// whatever size the user set.</summary>
    private void ApplySectionWidth(string key, double width)
    {
        SectionElement(key).Width = width;
        if (_feedHosts.TryGetValue(key, out var host)) host.ApplyInnerWidth(width);
    }

    internal void RepositionFollowers(Window host)
    {
        foreach (var follower in _sectionWindows.Values.Where(f => ReferenceEquals(f.DockHost, host)))
        {
            SeatOnHost(follower);
            RepositionFollowers(follower);
        }
    }

    /// <summary>Every section lives as its own window from the start — the default look
    /// IS the stack of magnetised windows. Saved positions win; anything without one
    /// chains under the main window in canonical order.</summary>
    private void SetupSectionWindows()
    {
        foreach (var key in SectionKeys)
        {
            Detach(key, tearOff: false);
            ApplySectionFont(key);
            if (_ui.SectionWidths.TryGetValue(key, out var w) && w is > 100 and < 2600)
                ApplySectionWidth(key, w);
            if (_ui.SectionPositions.TryGetValue(key, out var p) && p is [var x, var y]
                && !double.IsNaN(x) && !double.IsNaN(y))
            {
                _sectionWindows[key].Left = x;
                _sectionWindows[key].Top = y;
            }
        }
        Dispatcher.BeginInvoke(() =>
        {
            // A fresh install chains everything under the main window in order. On an
            // existing install this must NOT catch a section this BUILD introduced —
            // hosting it on the main window would collide with whatever the saved graph
            // already puts there. Brand-new sections hook under the stack's tail below.
            if (_ui.SectionPositions.Count == 0 && _ui.SectionDocks.Count == 0)
            {
                Window previous = this;
                foreach (var key in SectionKeys)
                {
                    var win = _sectionWindows[key];
                    Dock(win, previous, DockSide.Below);
                    previous = win;
                }
            }
            // Put the remembered stack back. This is restored, never re-derived: the
            // saved coordinates describe last run's content heights, so a section that
            // shrank in between leaves the window below it stranded far from its host.
            foreach (var key in SectionKeys)
            {
                var win = _sectionWindows[key];
                if (win.DockHost is not null) continue;
                if (!_ui.SectionDocks.TryGetValue(key, out var hostKey)) continue;
                win.DockHost = hostKey switch
                {
                    "" => null,                                   // deliberately floating
                    "main" => this,
                    _ => _sectionWindows.GetValueOrDefault(hostKey),
                };
                win.DockSide = _ui.SectionDockSides.TryGetValue(key, out var side)
                    ? side switch
                    {
                        "right" => DockSide.Right,
                        "left" => DockSide.Left,
                        _ => DockSide.Below,
                    }
                    : DockSide.Below;   // every dock was an under-dock before 1.69
                // A remembered host that no longer ships (a section key retired in an
                // update — "group2" lived for one release) would orphan this window
                // exactly the way lost geometry used to; the tail is the safe spot.
                if (win.DockHost is null && hostKey.Length > 0)
                    DockToStack(win);
            }
            BreakDockCycles();
            // A saved position with no remembered host — a settings file from before
            // docks were saved — re-magnetises by adjacency.
            foreach (var win in _sectionWindows.Values.OrderBy(v => v.Top).ToList())
                if (win.DockHost is null && !_ui.SectionDocks.ContainsKey(win.SectionKey)
                    && _ui.SectionPositions.ContainsKey(win.SectionKey))
                    Remagnetise(win);
            // A section with NOTHING saved is new in this build — hook it under the tail.
            foreach (var key in SectionKeys)
            {
                var win = _sectionWindows[key];
                if (win.DockHost is null && !_ui.SectionDocks.ContainsKey(key)
                    && !_ui.SectionPositions.ContainsKey(key))
                    DockToStack(win);
            }
            ApplySectionVisibility();
            RepinStack();
            SaveLayout();     // bank the graph so the guessing never has to happen again
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private string DockKey(Window? host) =>
        host is null ? ""
        : ReferenceEquals(host, this) ? "main"
        : ((SectionWindow)host).SectionKey;

    /// <summary>A hand-edited or half-written settings file could describe a loop, and
    /// RepositionFollowers walks the chain — so cut loose anything that doesn't reach a
    /// root rather than recurse forever.</summary>
    private void BreakDockCycles()
    {
        foreach (var win in _sectionWindows.Values)
        {
            var hops = 0;
            var host = win.DockHost;
            while (host is SectionWindow link && hops++ <= _sectionWindows.Count)
                host = link.DockHost;
            if (hops > _sectionWindows.Count) win.DockHost = null;
        }
    }

    /// <summary>Restore a window into the stack when nothing remembers where it belongs.
    /// Deliberately looser than the drag-drop snap: dropping a window says "exactly here",
    /// whereas a stack being restored has drifted by however much the content above it
    /// shrank since last run. Same column, nearest edge above.</summary>
    private void Remagnetise(SectionWindow w)
    {
        const double columnX = 48, reachY = 260;
        Window? best = null;
        var bestGap = double.MaxValue;
        foreach (var host in SnapHosts(w))
        {
            if (Math.Abs(w.Left - host.Left) > columnX) continue;
            var gap = w.Top - (host.Top + host.ActualHeight + DockGap);
            if (gap < -DockGap || gap > reachY) continue;   // must sit below that host
            if (gap < bestGap) { bestGap = gap; best = host; }
        }
        if (best is null) return;
        Dock(w, best, DockSide.Below);
    }

    /// <summary>Re-seat every docked window under its host. LocationChanged/SizeChanged
    /// already do this as things move; running it on the tick as well means a gap can
    /// never simply sit there — several sections shrink at once on a session reset, and
    /// anything missed then would otherwise stay spread apart until the next drag.</summary>
    private void RepinStack()
    {
        RepositionFollowers(this);
        foreach (var win in _sectionWindows.Values)
            if (win.DockHost is null) RepositionFollowers(win);
    }

    private void StackAllSections()
    {
        foreach (var win in _sectionWindows.Values) win.DockHost = null;
        Window previous = this;
        foreach (var key in SectionKeys)
        {
            var win = _sectionWindows[key];
            Dock(win, previous, DockSide.Below);
            previous = win;
        }
        RepositionFollowers(this);
        SaveLayout();
    }

    /// <summary>Share layout…: the whole arrangement as one pasteable string, and the
    /// way back in. Applying rebuilds the feed windows from the shared panes and re-seats
    /// every section, so it takes effect without a restart.</summary>
    private void OnShareLayout(object sender, RoutedEventArgs e)
    {
        SaveLayout();   // export what is on screen, not what was last banked
        var dlg = new LayoutShareDialog(LayoutShare.Export(_ui, Left, Top),
            _ui.LayoutPresets, () => _ui.Save()) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Applied is not { } payload) return;

        if (MessageBox.Show(this,
                $"Replace your layout with this one?\n\n{LayoutShare.Describe(payload)}",
                "Import layout", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        LayoutShare.Apply(payload, _ui, Left, Top);
        _ui.Save();

        // Tear the feed UI down COMPLETELY before rebuilding: views are keyed by pane
        // KEY and survive RebuildFeedSections, so a key the import shares with the old
        // layout ("feed", nearly always) kept its view bound to the OLD pane object —
        // the first tab showed the pre-import filters, and any rename after the import
        // wrote its Title onto that orphan and evaporated on the next save. Fresh views
        // from the imported panes are the fix for both.
        foreach (var key in _feedHosts.Keys.ToList()) DropFeedSection(key);
        _feedViews.Clear();

        // Rebuild the feed windows from the imported panes, then put every section where
        // the layout says. Windows the payload names are re-detached and repositioned;
        // the dock graph does the rest on the next RepinStack.
        foreach (var key in RebuildFeedSections()) MakeFeedWindow(key);
        foreach (var key in SectionKeys)
        {
            ApplySectionWidth(key,
                _ui.SectionWidths.TryGetValue(key, out var w) ? w : double.NaN);
            // The payload carries SectionFonts since 1.77 but nothing re-applied them
            // to the elements — an imported font only showed after a restart.
            ApplySectionFont(key);
            if (!_sectionWindows.TryGetValue(key, out var win)) continue;
            if (_ui.SectionPositions.TryGetValue(key, out var p) && p is [var x, var y])
            {
                win.Left = x;
                win.Top = y;
            }
            win.DockSide = _ui.SectionDockSides.TryGetValue(key, out var side)
                ? side switch { "right" => DockSide.Right, "left" => DockSide.Left, _ => DockSide.Below }
                : DockSide.Below;
            win.DockHost = _ui.SectionDocks.TryGetValue(key, out var host)
                ? host switch { "" => null, "main" => this, _ => _sectionWindows.GetValueOrDefault(host) }
                : win.DockHost;
        }
        BreakDockCycles();
        ApplySectionVisibility();
        foreach (var view in _feedViews.Values) { view.ApplyColors(); view.Invalidate(); }
        RenderFeeds();
        // Every feed window re-measures: an imported pane changes the row count and the
        // font under a window that has already latched its SizeToContent, so two windows
        // sharing a width would otherwise come up different heights.
        foreach (var key in _feedHosts.Keys) Refit(key);
        RepinStack();
        SaveLayout();
        Tick();
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Stack all sections back under the main window in the default order?",
                "Reset window layout", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _ui.SectionPositions.Clear();
        RootScale.ScaleX = RootScale.ScaleY = 1.0;
        foreach (var win in _sectionWindows.Values) win.SetScale(1.0);
        // Let the size change from the scale reset settle before measuring the stack.
        Dispatcher.BeginInvoke(StackAllSections, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnSyncPill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnGroupSyncMenu(sender, e);
    }

    private void OnResetSessionPill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnResetSession(sender, e);
    }

    /// <summary>The SPAWNS window's own clear. Session reset deliberately leaves timers
    /// running — a respawn countdown is real-world time, not session state — so breaking
    /// camp needs its own action rather than a surprise inside "reset session".</summary>
    private void OnClearSpawns(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var running = _spawnTimers.Snapshot(DateTime.Now).Count;
        if (running == 0) return;
        if (MessageBox.Show(this,
                $"Clear {running} spawn timer{(running == 1 ? "" : "s")}? Countdowns are gone for good — "
                + "use this when you break camp, not to start a new session.",
                "Clear spawn timers", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _spawnTimers.ClearServer();
        Tick();
    }

    private void OnResetLayoutPill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OnResetLayout(sender, e);
    }

    private void OnUpdatePill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CheckUpdates();
    }

    private void OnSettingsPill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var dlg = new SettingsDialog(_ui.GroupBoardUseSync, _ui.ShowGroupMotes,
            SectionKeys, _ui.HiddenSections, _ui.FeedHistory, _ui) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _ui.GroupBoardUseSync = dlg.GroupBoardUseSync;
        _ui.ShowGroupMotes = dlg.ShowGroupMotes;
        dlg.ApplyCues(_ui);
        _ui.HiddenSections = dlg.HiddenSections;
        _ui.FeedHistory = dlg.FeedHistory;
        _feed.SetCapacity(_ui.FeedHistory);
        // Shrinking the buffer drops the oldest entries out from under rows already
        // drawn; redraw from what survived.
        foreach (var view in _feedViews.Values) view.Invalidate();
        RenderFeeds();
        _ui.Save();
        ApplySectionVisibility();
        Tick();
    }

    /// <summary>Hide/show whole sections per the ⚙ tick boxes. Hiding a window
    /// mid-stack bridges its followers up to its own host first, so the chain closes
    /// over the gap; re-showing hooks the window back under the stack's tail — its old
    /// spot has long since closed up, and the tail is the one place that is always
    /// right. Collapse (the ▸ headings) is separate: that keeps the header visible.</summary>
    private void ApplySectionVisibility()
    {
        foreach (var key in SectionKeys)
        {
            var win = _sectionWindows[key];
            var hide = _ui.HiddenSections.Contains(key);
            if (hide && win.IsVisible)
            {
                foreach (var follower in _sectionWindows.Values
                             .Where(f => ReferenceEquals(f.DockHost, win)))
                {
                    follower.DockSide = win.DockSide;   // takes over the hidden window's slot
                    follower.DockHost = win.DockHost ?? this;
                }
                win.DockHost = null;
                win.Hide();
            }
            else if (!hide && !win.IsVisible)
            {
                win.Show();
                DockToStack(win);
            }
        }
        RepinStack();
        SaveLayout();
    }

    private void OnBreakdownToggle(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _popupSource = "main";
        TogglePopup("own:");
        Tick(); // instant feedback rather than waiting up to a second
    }


    private void OnFightRowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: FightRow row }) return;
        TogglePopup($"fight:{row.Key}");
    }

    private void OnSpawnRowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SpawnRow row }) return;
        TogglePopup($"spawn:{row.Zone}|{row.Name}");
    }

    /// <summary>One satellite popup at a time — a fight's or a member's. Clicking the
    /// same thing again closes it; clicking something else switches to it.</summary>
    private void TogglePopup(string key)
    {
        if (_popup is { } open &&
            string.Equals(open.MemberName, key, StringComparison.OrdinalIgnoreCase))
        {
            open.Close();
            _popup = null;
            return;
        }
        _popup?.Close();
        var popup = _popup = new BreakdownPopup(key, this);
        popup.Closed += (_, _) => { if (ReferenceEquals(_popup, popup)) _popup = null; };
        RefreshPopup();
        popup.Show();
    }

    private void OnResetSession(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Start a new session? DPS, fights, loot, and motes count from now — for " +
                "the group board too — and the new session survives a restart. Spawn " +
                "timers keep running: clear those from the SPAWNS window.",
                "Reset session", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _stats.Reset();
        _group.Reset();
        _ledger.Reset();
        // Mark where every synced member stands right now, so their rows count from here
        // like yours do. Members who show up later get marked the first time we see them.
        _resetAt = DateTime.Now;
        _groupBaseline.Clear();
        foreach (var m in _sync.Members) BaselineFor(m);
        // Remember how far into the log we had read, so a restart resumes this session
        // rather than replaying the lines you just cleared.
        _ui.ResetLogPath = _watcher.CurrentPath;
        _ui.ResetLogOffset = _watcher.Offset;
        _ui.Save();
        _popup?.Close();
        Tick();
    }

    private void OnGroupRowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: string row }) return;
        var name = row.TrimStart('~').Split(' ')[0].Trim();
        if (name.Length == 0) return;
        // Both boards share this handler; the popup parks beside — and scopes to —
        // the board that was clicked.
        _popupSource = sender is Visual v && Group2List.IsAncestorOf(v) ? "group2" : "group";
        // Your own row (the local boards lead with it) opens your full breakdown —
        // the member popup only ever has the top sources that cross the wire.
        TogglePopup(IsSelf(name) ? "own:" : name);
    }

    private void OnGroupSyncMenu(object sender, RoutedEventArgs e)
    {
        var relay = _sync.RelayUrl.Length > 0 ? _sync.RelayUrl : GroupSync.DefaultRelay;
        var dlg = new SyncDialog(_sync.GroupCode, relay) { Owner = this };
        if (dlg.ShowDialog() == true) _sync.Configure(dlg.GroupCode, dlg.RelayUrl);
    }
}
