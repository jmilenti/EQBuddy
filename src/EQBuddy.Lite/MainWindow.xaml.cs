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
    private static readonly string[] SectionKeys = ["motes", "loot", "fights", "spawns", "group", "feed"];
    private readonly Dictionary<string, SectionWindow> _sectionWindows = new();

    private FrameworkElement SectionElement(string key) => key switch
    {
        "motes" => MotesSection,
        "loot" => LootSection,
        "fights" => FightsSection,
        "spawns" => SpawnSection,
        "feed" => FeedSection,
        _ => GroupSection,
    };

    private bool Attached(string key) => !_sectionWindows.ContainsKey(key);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

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

        _watcher = new LogWatcher(_stats)
        {
            Tap = e => { _group.Apply(e); _ledger.Apply(e); _feed.Apply(e); },
            RawTap = _feed.ApplyRaw,
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
        WireSection(FeedHeader, "feed", () => _ui.ShowFeed = !_ui.ShowFeed);
        _feed.SetCapacity(_ui.FeedHistory);
        BuildFeedPills();
        BuildFeedSearch();
        Loaded += (_, _) => SetupSectionWindows();
        LocationChanged += (_, _) => { RepositionFollowers(this); RefreshPopupPosition(); };
        SizeChanged += (_, _) => { RepositionFollowers(this); RefreshPopupPosition(); };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
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
        FeedTopSep.Visibility = Attached("feed") ? Visibility.Visible : Visibility.Collapsed;

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
            SpawnHeader.Text = "SPAWNS · none running";
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

        // ONE session-scoped GROUP board. Fight-scope detail lives in the popups
        // instead (a row's popup header carries both rates; your own row opens the
        // full two-scope breakdown) — a second fight-scoped board was tried and
        // retired within a day: the popups already said everything it did.
        RenderGroupBoard(fightMode: false, _ui.ShowGroup,
            GroupLabel, GroupList, GroupEmptyText, lastFight, s);

        _feed.PetName = s.PetName;
        RenderFeed();

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
    /// <summary>Which window the open popup was launched from: "main" or "group".
    /// Member and own-breakdown popups park beside the window that was actually
    /// clicked — your own row sits on the GROUP board, and anchoring its popup to a
    /// fixed window put it at the top of the stack while the click happened at the
    /// bottom.</summary>
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

        // Your own breakdown (keyed "own:"): every source, not a top-N — and BOTH
        // scopes stacked, fight then session, since the headline stopped switching
        // between them. This popup is where the session detail lives now.
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
            Section($"— this fight · {_snap?.LastFight?.Dps ?? 0:0} dps —",
                _snap?.LastFight?.ByAbility);
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
            popup.Update($"{f.Name} · {f.Dps:0} dps", detail);
            return;
        }

        var member = _ui.GroupBoardUseSync
            ? _sync.Members.FirstOrDefault(m =>
                m.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase))
            : null;
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
        // Both clocks in the header, mirroring the two GROUP boards the row was
        // clicked on; the rows below are per-source session totals either way.
        popup.Update($"{member.Name} · fight {ScopedDps(member, true):0} · "
            + $"session {ScopedDps(member, false):0} dps", rows);
    }

    // ---- FEED: a live, filterable view of combat from your own log ----

    private sealed record FeedRow(string Text, Brush Color);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush FeedYouBrush = Frozen(0xCF, 0xE3, 0xF5);
    private static readonly Brush FeedCritBrush = Frozen(0xE8, 0xCE, 0x9C);
    private static readonly Brush FeedPetBrush = Frozen(0x8F, 0xD4, 0xC8);
    private static readonly Brush FeedGroupBrush = Frozen(0xB9, 0xA7, 0xE8);
    private static readonly Brush FeedTakenBrush = Frozen(0xE8, 0x9C, 0x9C);
    private static readonly Brush FeedHealBrush = Frozen(0x8B, 0xE2, 0x8B);
    private static readonly Brush FeedDimBrush = Frozen(0x7B, 0x87, 0x94);
    private static readonly Brush FeedKillBrush = Frozen(0xD9, 0xC4, 0x6B);
    private static readonly Brush FeedRawBrush = Frozen(0xAE, 0xBB, 0xC7);

    private static readonly Brush FeedPillOnFg = Frozen(0xD9, 0xC4, 0x6B);
    private static readonly Brush FeedPillOffFg = Frozen(0x55, 0x61, 0x6C);
    private static readonly Brush FeedPillOnBg = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FeedPillOffBg = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FeedPillOnBorder = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FeedPillOffBorder = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

    private void RenderFeed()
    {
        if (!_ui.ShowFeed)
        {
            FeedHeader.Text = "\u25b8 FEED \u00b7 live";
            FeedSearchRow.Visibility = Visibility.Collapsed;
            FeedPillRow.Visibility = Visibility.Collapsed;
            FeedList.Visibility = Visibility.Collapsed;
            FeedEmptyText.Visibility = Visibility.Collapsed;
            return;
        }
        var raw = _ui.FeedFilters.RawMode;
        FeedSearchRow.Visibility = Visibility.Visible;
        // The who/kind pills describe parsed combat events; raw mode shows the log
        // verbatim, so they'd be dead controls there.
        FeedPillRow.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        // The grip's row count is the VIEWPORT, not the data: the list renders the whole
        // filtered scrollback (virtualized) and shows this many rows of it at once.
        FeedList.MaxHeight = FeedRowsClamped() * 14 + 4;

        // Newest-first means every refresh shifts rows under a reader who has scrolled
        // back — so while they're anywhere but the top, the list freezes and the header
        // says so. Scrolling back up resumes live on the next tick.
        if (FeedScroller() is { VerticalOffset: > 0.5 })
        {
            FeedHeader.Text = "\u25be FEED \u00b7 paused \u2014 scroll to top to resume";
            return;
        }
        List<FeedRow> rows;
        if (raw)
        {
            rows = _feed.SnapshotRaw(_ui.FeedFilters.SearchTerms, 2000)
                .Select(l => new FeedRow(
                    l.Time == DateTime.MinValue ? l.Text : $"{l.Time:HH:mm:ss}  {l.Text}",
                    FeedRawBrush))
                .ToList();
            FeedHeader.Text = "\u25be FEED \u00b7 raw log \u00b7 live";
        }
        else
        {
            rows = _feed.Snapshot(_ui.FeedFilters, 2000).Select(RowOf).ToList();
            FeedHeader.Text = "\u25be FEED \u00b7 live";
        }
        FeedList.ItemsSource = rows;
        FeedList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FeedEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The ListBox's internal ScrollViewer, once templated (null before the
    /// first layout pass).</summary>
    private ScrollViewer? FeedScroller()
    {
        if (VisualTreeHelper.GetChildrenCount(FeedList) == 0) return null;
        return VisualTreeHelper.GetChild(FeedList, 0) is System.Windows.Controls.Border b
            ? b.Child as ScrollViewer
            : null;
    }

    private FeedRow RowOf(FeedEntry e)
    {
        var t = e.Time.ToString("HH:mm:ss");
        // The log's own annotation wins (it already says "Riposte Critical" when both
        // apply); a bare crit flag gets the plain tag.
        var tag = e.Note is { Length: > 0 } n ? $" ({n})" : e.Crit ? " (Crit)" : "";
        var actor = e.Who == FeedWho.You ? "" : $"{e.Actor}: ";
        return e.Kind switch
        {
            FeedKind.Melee or FeedKind.Spell or FeedKind.Dot or FeedKind.Aux => new FeedRow(
                $"{t}  {actor}{e.Ability} \u2192 {e.Target}  {e.Amount:N0}{tag}",
                e.Crit ? FeedCritBrush : e.Who switch
                {
                    FeedWho.Pet => FeedPetBrush,
                    FeedWho.Group => FeedGroupBrush,
                    _ => FeedYouBrush,
                }),
            FeedKind.Taken => new FeedRow(
                $"{t}  {e.Actor}{(e.Ability.Length > 0 ? $" {e.Ability}" : "")} \u2192 you  {e.Amount:N0}",
                FeedTakenBrush),
            FeedKind.Heal => new FeedRow(
                e.Incoming
                    ? $"{t}  {e.Actor} heals you  +{e.Amount:N0}"
                    : $"{t}  {e.Ability} \u2192 {e.Target}  +{e.Amount:N0}",
                FeedHealBrush),
            FeedKind.Miss => new FeedRow(
                e.Incoming ? $"{t}  missed you" : $"{t}  you miss", FeedDimBrush),
            FeedKind.Kill => new FeedRow($"{t}  {e.Actor} slew {e.Target}", FeedKillBrush),
            FeedKind.Resist => new FeedRow(
                $"{t}  {(e.Ability.Length > 0 ? e.Ability : "spell")} resisted", FeedDimBrush),
            _ => new FeedRow(
                $"{t}  {(e.Ability.Length > 0 ? e.Ability : "spell")} fizzled", FeedDimBrush),
        };
    }

    /// <summary>The FEED filter pills, built in code — sixteen toggles sharing one tiny
    /// template. Each pill owns its refresh closure; clicking saves and re-renders at
    /// once, so a filter change reads back through the buffer instead of only changing
    /// what arrives next.</summary>
    private void BuildFeedPills()
    {
        var f = _ui.FeedFilters;
        void Pill(string label, string tip, Func<bool> isOn, Action click, Func<string>? text = null)
        {
            var tb = new TextBlock { FontSize = 10, Text = label };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 1, 4, 1),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = tb,
            };
            void Refresh()
            {
                var on = isOn();
                tb.Text = text?.Invoke() ?? label;
                tb.Foreground = on ? FeedPillOnFg : FeedPillOffFg;
                pill.Background = on ? FeedPillOnBg : FeedPillOffBg;
                pill.BorderBrush = on ? FeedPillOnBorder : FeedPillOffBorder;
            }
            pill.MouseLeftButtonDown += (_, args) =>
            {
                args.Handled = true;
                click();
                _ui.Save();
                Refresh();
                RenderFeed();
            };
            Refresh();
            _feedPillRefreshers.Add(Refresh);
            FeedPillRow.Children.Add(pill);
        }

        // who
        Pill("you", "Your own damage", () => f.You, () => f.You = !f.You);
        Pill("pet", "Your pet's damage", () => f.Pet, () => f.Pet = !f.Pet);
        Pill("grp", "Other players near you, from your log", () => f.Group, () => f.Group = !f.Group);
        Pill("in", "Damage you take", () => f.Incoming, () => f.Incoming = !f.Incoming);
        // kind
        Pill("melee", "Melee hits", () => f.Melee, () => f.Melee = !f.Melee);
        Pill("spell", "Direct spell damage", () => f.Spells, () => f.Spells = !f.Spells);
        Pill("dot", "Damage-over-time ticks", () => f.Dots, () => f.Dots = !f.Dots);
        Pill("ds", "Damage shields / automatic damage", () => f.DamageShields, () => f.DamageShields = !f.DamageShields);
        Pill("heal", "Heals, cast and received", () => f.Heals, () => f.Heals = !f.Heals);
        Pill("miss", "Misses, dodges, parries", () => f.Misses, () => f.Misses = !f.Misses);
        Pill("kill", "Killing blows", () => f.Kills, () => f.Kills = !f.Kills);
        Pill("r/f", "Resists and fizzles", () => f.ResistsFizzles, () => f.ResistsFizzles = !f.ResistsFizzles);
        // narrowing
        Pill("crit", "Critical hits only", () => f.CritsOnly, () => f.CritsOnly = !f.CritsOnly);
        Pill("slay", "Slay Undead hits only (combines with rip/crip as either-or)",
            () => f.OnlySlays, () => f.OnlySlays = !f.OnlySlays);
        Pill("rip", "Ripostes only (combines with slay/crip as either-or)",
            () => f.OnlyRipostes, () => f.OnlyRipostes = !f.OnlyRipostes);
        Pill("crip", "Crippling Blows only (combines with slay/rip as either-or)",
            () => f.OnlyCrippling, () => f.OnlyCrippling = !f.OnlyCrippling);
        Pill("dmg", "Minimum damage to show \u2014 click to cycle",
            () => f.MinDamage > 0,
            () => f.MinDamage = f.MinDamage switch { 0 => 100, 100 => 500, 500 => 1000, 1000 => 5000, _ => 0 },
            () => f.MinDamage == 0 ? "dmg\u00b7any" : $"dmg\u00b7{f.MinDamage}+");
        Pill("type", "Melee damage type \u2014 click to cycle",
            () => f.MeleeType != "all",
            () => f.MeleeType = f.MeleeType switch
            {
                "all" => "slash", "slash" => "pierce", "pierce" => "blunt",
                "blunt" => "archery", _ => "all",
            },
            () => $"type\u00b7{f.MeleeType}");
    }

    // ---- FEED search chips: [all] [term ✕]… [box] [+] ----

    private readonly List<Action> _feedPillRefreshers = [];
    private TextBox _feedSearchBox = null!;

    private void BuildFeedSearch()
    {
        _feedSearchBox = new TextBox
        {
            MinWidth = 64,
            FontSize = 10,
            Padding = new Thickness(3, 0, 3, 1),
            Margin = new Thickness(0, 1, 2, 1),
            Background = FeedPillOffBg,
            Foreground = Frozen(0xDD, 0xE5, 0xEC),
            CaretBrush = Frozen(0xDD, 0xE5, 0xEC),
            BorderBrush = FeedPillOffBorder,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Type a word and press Enter — rows must contain one of the chips " +
                      "(actor, ability, target, or annotation; try slay, crit, riposte, a name…)",
        };
        _feedSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; CommitFeedSearch(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _feedSearchBox.Clear(); }
        };
        RefreshFeedSearchRow();
    }

    private void CommitFeedSearch()
    {
        var term = _feedSearchBox.Text.Trim();
        _feedSearchBox.Clear();
        if (term.Length == 0) return;
        var f = _ui.FeedFilters;
        if (!f.SearchTerms.Any(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase)))
            f.SearchTerms.Add(term);
        _ui.Save();
        RefreshFeedSearchRow();
        RenderFeed();
    }

    /// <summary>Rebuild the whole row — chips are cheap and a full rebuild keeps one
    /// source of truth (the settings list). The text box is a persistent instance so
    /// half-typed input survives a chip add/remove.</summary>
    private void RefreshFeedSearchRow()
    {
        var f = _ui.FeedFilters;
        FeedSearchRow.Children.Clear();

        var all = FlatButton("all", f.RawMode ? FeedPillOnFg : FeedPillOffFg,
            "Show the raw log — every line the game writes (chat, emotes, system, " +
            "everything), not just parsed combat. Chips filter by text; click again " +
            "for the combat view.");
        if (f.RawMode)
        {
            all.Background = FeedPillOnBg;
            all.BorderBrush = FeedPillOnBorder;
        }
        all.Click += (_, _) =>
        {
            f.RawMode = !f.RawMode;
            _ui.Save();
            RefreshFeedSearchRow();
            RenderFeed();
        };
        FeedSearchRow.Children.Add(all);

        foreach (var term in f.SearchTerms.ToList())
        {
            var text = new TextBlock
            {
                Text = term,
                FontSize = 10,
                Foreground = FeedPillOnFg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = FlatButton("✕", Frozen(0x8A, 0x97, 0xA3), $"Stop filtering by {term}");
            remove.Margin = new Thickness(3, 0, 0, 0);
            remove.Padding = new Thickness(2, 0, 2, 1);
            remove.BorderThickness = new Thickness(0);
            remove.Background = Brushes.Transparent;
            remove.Click += (_, _) =>
            {
                f.SearchTerms.RemoveAll(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
                _ui.Save();
                RefreshFeedSearchRow();
                RenderFeed();
            };
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(text);
            body.Children.Add(remove);
            FeedSearchRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 3, 1),
                Margin = new Thickness(0, 1, 4, 1),
                BorderThickness = new Thickness(1),
                Background = FeedPillOnBg,
                BorderBrush = FeedPillOnBorder,
                Child = body,
            });
        }

        FeedSearchRow.Children.Add(_feedSearchBox);
        var plus = FlatButton("+", FeedPillOnFg, "Add the typed word as a chip (same as Enter)");
        plus.Click += (_, _) => CommitFeedSearch();
        FeedSearchRow.Children.Add(plus);
    }

    private Button FlatButton(string text, Brush fg, string tip) => new()
    {
        Content = text,
        ToolTip = tip,
        Cursor = Cursors.Hand,
        Focusable = false,
        FontSize = 10,
        Foreground = fg,
        Background = FeedPillOffBg,
        BorderBrush = FeedPillOffBorder,
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 1, 4, 1),
        Template = (ControlTemplate)FindResource("FlatButtonTemplate"),
    };

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
        if (yours.Total > 0)
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
            bool mono = true, bool right = true, bool bold = false, string? tip = null)
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
            if (mono) tb.FontFamily = new FontFamily("Consolas");
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            MotesTable.Children.Add(tb);
        }

        for (var c = 0; c < tiers.Count; c++)
            Cell(0, c + 1, tiers[c], MoteDim, size: 9, mono: false,
                // TierShort is invertible, so the header can name the actual item.
                tip: tiers[c] == "Base" ? "Mote of Potential — the tierless base mote"
                    : $"Mote of {tiers[c]} Potential");
        Cell(0, tiers.Count + 1, "all", MoteDim, size: 9, mono: false, tip: "Total motes");
        Cell(0, tiers.Count + 2, "/h", MoteDim, size: 9, mono: false, tip: "Motes per hour");
        Cell(0, tiers.Count + 3, "time", MoteDim, size: 9, mono: false,
            tip: "How long this player has been collecting — their session length, or "
                 + "time since your reset when the board is rebased");

        for (var r = 0; r < rows.Count; r++)
        {
            var (name, isYou, total, rate, span, byTier) = rows[r];
            Cell(r + 1, 0, name, isYou ? MoteBright : MoteMemberName,
                size: 12, mono: false, right: false, bold: isYou);
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

    private void CheckUpdates()
    {
        _lastUpdateCheck = DateTime.Now;
        Task.Run(async () =>
        {
            var info = await UpdateChecker.FindBestAsync(_settings.UpdateFolder);
            if (info is null || !UpdateChecker.IsNewer(info)) return;
            Dispatcher.Invoke(() =>
            {
                _pendingUpdate = info;
                UpdateBanner.Text = info.SetupPath is not null || info.DownloadUrl is not null
                    ? $"Update v{info.Latest} is ready — click to install."
                    : $"Update v{info.Latest} is available — click to open the release page.";
                UpdateBanner.Visibility = Visibility.Visible;
            });
        });
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

    private void OnCheckUpdatesMenu(object sender, RoutedEventArgs e) => CheckUpdates();

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
        w.DockHost = tail;
        w.Left = tail.Left;
        w.Top = tail.Top + tail.ActualHeight + DockGap;
        RepositionFollowers(w);
        SaveLayout();   // the dock graph is remembered now, so every change to it is saved
    }

    /// <summary>Magnetise: dropped near another EQdps window's bottom edge, a section
    /// window aligns under it and follows it from then on.</summary>
    internal void SnapWindow(SectionWindow w)
    {
        const double snapX = 48, snapY = 28;
        w.DockHost = null;
        Window? best = null;
        var bestDist = double.MaxValue;
        foreach (var host in SnapHosts(w))
        {
            var dx = Math.Abs(w.Left - host.Left);
            var dy = Math.Abs(w.Top - (host.Top + host.ActualHeight + DockGap));
            if (dx > snapX || dy > snapY) continue;
            if (dx + dy < bestDist) { bestDist = dx + dy; best = host; }
        }
        if (best is not null)
        {
            w.DockHost = best;
            w.Left = best.Left;
            w.Top = best.Top + best.ActualHeight + DockGap;
            RepositionFollowers(w);
        }
        SaveLayout();
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
        _sectionResizeStartRows = FeedRowsClamped();
    }

    internal void SectionResizeDelta(SectionWindow w, double dx, double dy)
    {
        var width = Math.Clamp(_sectionResizeStartWidth + dx, 170, 720);
        _ui.SectionWidths[w.SectionKey] = width;
        ApplySectionWidth(w.SectionKey, width);
        if (w.SectionKey == "feed")
        {
            // ~14 px per Consolas 11 row: dragging down grows the list, up shrinks it.
            _ui.FeedRows = Math.Clamp(_sectionResizeStartRows + (int)Math.Round(dy / 14), 4, 40);
            RenderFeed();
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
        if (w.SectionKey == "feed")
        {
            _ui.FeedRows = 12;
            RenderFeed();
        }
        _ui.Save();
    }

    /// <summary>Give a section an explicit width (NaN = back to auto). The feed's inner
    /// caps must track it — they exist to stop SizeToContent growing without bound, and
    /// a fixed cap would hold the content at 340 px inside a wider window.</summary>
    private void ApplySectionWidth(string key, double width)
    {
        SectionElement(key).Width = width;
        if (key == "feed")
        {
            var cap = double.IsNaN(width) ? 340 : Math.Max(150, width);
            FeedSearchRow.MaxWidth = cap;
            FeedPillRow.MaxWidth = cap;
            FeedList.MaxWidth = cap;
        }
    }

    private int FeedRowsClamped() => Math.Clamp(_ui.FeedRows, 4, 40);

    internal void RepositionFollowers(Window host)
    {
        foreach (var follower in _sectionWindows.Values.Where(f => ReferenceEquals(f.DockHost, host)))
        {
            follower.Left = host.Left;
            follower.Top = host.Top + host.ActualHeight + DockGap;
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
            if (_ui.SectionWidths.TryGetValue(key, out var w) && w is > 100 and < 2000)
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
                    win.DockHost = previous;
                    win.Left = previous.Left;
                    win.Top = previous.Top + previous.ActualHeight + DockGap;
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
        w.DockHost = best;
        w.Left = best.Left;
        w.Top = best.Top + best.ActualHeight + DockGap;
        RepositionFollowers(w);
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
            win.DockHost = previous;
            win.Left = previous.Left;
            win.Top = previous.Top + previous.ActualHeight + DockGap;
            previous = win;
        }
        RepositionFollowers(this);
        SaveLayout();
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
            SectionKeys, _ui.HiddenSections, _ui.FeedHistory) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _ui.GroupBoardUseSync = dlg.GroupBoardUseSync;
        _ui.ShowGroupMotes = dlg.ShowGroupMotes;
        _ui.HiddenSections = dlg.HiddenSections;
        _ui.FeedHistory = dlg.FeedHistory;
        _feed.SetCapacity(_ui.FeedHistory);
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
                    follower.DockHost = win.DockHost ?? this;
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
        _popupSource = "group";
        // Your own row (the local board leads with it) opens your full breakdown —
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
