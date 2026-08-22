using System.IO;
using System.Windows;
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
    private static readonly string[] SectionKeys = ["motes", "loot", "fights", "spawns", "group"];
    private readonly Dictionary<string, SectionWindow> _sectionWindows = new();

    private FrameworkElement SectionElement(string key) => key switch
    {
        "motes" => MotesSection,
        "loot" => LootSection,
        "fights" => FightsSection,
        "spawns" => SpawnSection,
        _ => GroupSection,
    };

    private bool Attached(string key) => !_sectionWindows.ContainsKey(key);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTime _lastCharScan = DateTime.MinValue;
    private DateTime _lastJanitor = DateTime.MinValue;
    private DateTime _lastUpdateCheck = DateTime.MinValue;
    private UpdateInfo? _pendingUpdate;
    private bool _installing;

    public MainWindow()
    {
        InitializeComponent();
        VersionItem.Header = $"EQdps v{UpdateChecker.CurrentVersion}";

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

        _watcher = new LogWatcher(_stats) { Tap = _group.Apply, Spawns = _spawnTimers };
        FollowCharacter(force: true);

        // Log hygiene at startup, same promises as the full app: force Log=1 and wipe
        // finished-session logs — both stand down while the game (or GINA/GamParse) runs.
        RunJanitor();

        WireSection(MotesHeader, "motes", () => _ui.ShowMotes = !_ui.ShowMotes);
        WireSection(LootHeader, "loot", () => _ui.ShowLoot = !_ui.ShowLoot);
        WireSection(FightsHeader, "fights", () => _ui.ShowFights = !_ui.ShowFights);
        WireSection(SpawnHeader, "spawns", () => _ui.ShowSpawns = !_ui.ShowSpawns);
        WireSection(GroupLabel, "group", () => _ui.ShowGroup = !_ui.ShowGroup);
        Loaded += (_, _) => SetupSectionWindows();
        LocationChanged += (_, _) => RepositionFollowers(this);
        SizeChanged += (_, _) => RepositionFollowers(this);

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
            _watcher.Select(m.FilePath);
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

        DpsText.Text = s.CurrentDps > 0
            ? $"⚔ {s.SessionDps:0} dps  (now {s.CurrentDps:0})"
            : $"⚔ {s.SessionDps:0} dps";

        // DPS breakdown behind its own clickable heading, collapsed by default — the
        // summary line is always there, the detail is opt-in.
        var totalDamage = s.DamageBySource.Sum(d => d.Total);
        if (totalDamage > 0)
        {
            DamageHeader.Text = $"{(_ui.ShowBreakdown ? "▾" : "▸")} DAMAGE · top sources";
            DamageHeader.Visibility = Visibility.Visible;
            if (_ui.ShowBreakdown)
            {
                BreakdownList.ItemsSource = s.DamageBySource
                    .Take(5)
                    .Select(d => $"{Pad(d.Name, 13)} {FmtDamage(d.Total),6} {d.Total * 100 / totalDamage,3}%")
                    .ToList();
                BreakdownList.Visibility = Visibility.Visible;
            }
            else BreakdownList.Visibility = Visibility.Collapsed;
        }
        else
        {
            DamageHeader.Visibility = Visibility.Collapsed;
            BreakdownList.Visibility = Visibility.Collapsed;
        }

        // Charm provenance when Core proved it (blink/charmed/glaze landings), with how
        // long the charm has held; the charm spell rides the tooltip. A pet without a
        // seen charm landing shows plain — the log can't say which class summoned it.
        var charmTag = "";
        if (s.PetCharmed)
        {
            var dur = s.PetSince is { } since ? FmtDur(now - since) : "";
            charmTag = dur.Length > 0 ? $" · charmed {dur}" : " · charmed";
        }
        var petDamage = s.PetAbilities.Sum(p => p.Total);
        PetText.Text = s.PetName.Length > 0
            ? $"🐾 {s.PetName}{charmTag}: {petDamage / Math.Max(1, s.CombatSeconds):0.#} dps"
            : petDamage > 0 ? $"🐾 pet: {petDamage / Math.Max(1, s.CombatSeconds):0.#} dps"
            : "🐾 no pet";
        PetText.ToolTip = s.PetCharmSpell is { Length: > 0 } charmSpell
            ? $"Charmed with {charmSpell}" : null;

        // Motes as their own heading, tier list behind the same click-to-expand.
        var motes = Motes.Summarize(s.Loot, s.Elapsed);
        if (motes.Total == 0)
        {
            MotesHeader.Text = "MOTES · none yet";
            MotesList.Visibility = Visibility.Collapsed;
        }
        else
        {
            MotesHeader.Text =
                $"{(_ui.ShowMotes ? "▾" : "▸")} MOTES · {motes.Total} ({motes.PerHour:0.#}/h)";
            if (_ui.ShowMotes)
            {
                MotesList.ItemsSource = motes.Tiers
                    .Select(t => $"{Pad(TierShort(t.Item), 14)} ×{t.Count}")
                    .ToList();
                MotesList.Visibility = Visibility.Visible;
            }
            else MotesList.Visibility = Visibility.Collapsed;
        }

        // Section separators only make sense inside the main panel — a torn-off
        // window has its own chrome.
        MotesTopSep.Visibility = Attached("motes") ? Visibility.Visible : Visibility.Collapsed;
        SpawnTopSep.Visibility = Attached("spawns") ? Visibility.Visible : Visibility.Collapsed;
        GroupTopSep.Visibility = Attached("group") ? Visibility.Visible : Visibility.Collapsed;

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
        if (s.RecentEncounters.Count == 0 && !Attached("fights"))
        {
            FightsHeader.Text = "FIGHTS · none yet";
            FightsHeader.Visibility = Visibility.Visible;
            FightsList.Visibility = Visibility.Collapsed;
        }
        else if (s.RecentEncounters.Count > 0)
        {
            FightsHeader.Text = $"{(_ui.ShowFights ? "▾" : "▸")} FIGHTS · {s.EncounterCount} this session";
            FightsHeader.Visibility = Visibility.Visible;
            if (_ui.ShowFights)
            {
                FightsList.ItemsSource = s.RecentEncounters
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

        _sync.Publish(_stats.CharacterName ?? "", s.CurrentDps, s.SessionDps,
            s.DamageBySource.Take(6).Select(d => new BreakdownEntry(d.Name, d.Total)).ToList(),
            motes);

        List<string> rows;
        if (_sync.Active)
        {
            GroupLabel.Text = (_ui.ShowGroup ? "▾ " : "▸ ") + (_sync.LastError is { } err
                ? $"GROUP · sync {_sync.GroupCode} · {err}"
                : $"GROUP · synced · {_sync.GroupCode}");
            var synced = _sync.Members;
            var syncedNames = new HashSet<string>(synced.Select(m => m.Name),
                StringComparer.OrdinalIgnoreCase);
            rows = synced.Select(m => $"{Pad(m.Name, 12)} {m.Dps,5:0} dps").ToList();
            // Players near you who aren't running the app still show, marked approximate.
            rows.AddRange(_group.Snapshot(now, s.PetName)
                .Where(r => !syncedNames.Contains(r.Name))
                .Select(r => $"{Pad("~" + r.Name, 12)} {r.WindowDps,5:0} dps"));
            rows = rows.Take(8).ToList();
            GroupEmptyText.Text = "(waiting for group…)";
        }
        else
        {
            GroupLabel.Text = (_ui.ShowGroup ? "▾ " : "▸ ") + "GROUP · from your log, near you";
            rows = _group.Snapshot(now, s.PetName)
                .Take(8)
                .Select(r => $"{Pad(r.Name, 12)} {r.WindowDps,5:0} dps")
                .ToList();
            GroupEmptyText.Text = "(no group activity nearby)";
        }
        GroupList.ItemsSource = rows;
        GroupList.Visibility = _ui.ShowGroup ? Visibility.Visible : Visibility.Collapsed;
        GroupEmptyText.Visibility = _ui.ShowGroup && rows.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        RefreshPopup();
    }

    /// <summary>Keep the member popup parked at the panel's right edge and fed with the
    /// latest synced numbers. Called every tick and when the popup opens.</summary>
    private void RefreshPopup()
    {
        if (_popup is not { } popup) return;
        popup.Left = Left + ActualWidth + 8;
        popup.Top = Top;

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
                popup.Update("spawn", "(timer expired or cleared)", "", "");
                return;
            }
            var detail = $"zone    {t.Zone}\nkilled  {t.KilledAt:HH:mm:ss}";
            detail += t.DueAt is { } due
                ? $"\ndue     {due:HH:mm:ss}"
                : "\ndue     unknown respawn time";
            if (t is { CampLocY: { } y, CampLocX: { } x })
                detail += $"\ncamp    /loc {y:0}, {x:0}";
            popup.Update($"{t.Name} · {FmtSpawn(t, DateTime.Now)}", detail, "", "");
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
                popup.Update("fight", "(no longer tracked — session\n pruned or reset)", "", "");
                return;
            }
            var abilityTotal = f.ByAbility.Sum(b => b.Total);
            var detail =
                $"{FmtDur(TimeSpan.FromSeconds(f.DurationSeconds))} · {f.Outcome} · {FmtDamage(f.DamageOut)} dmg"
                + (f.DamageIn > 0 ? $" · took {FmtDamage(f.DamageIn)}" : "")
                + (abilityTotal > 0
                    ? "\n" + string.Join("\n", f.ByAbility.Take(8).Select(b =>
                        $"{Pad(b.Name, 13)} {FmtDamage(b.Total),6} {b.Total * 100 / abilityTotal,3}%"))
                    : "");
            popup.Update($"{f.Name} · {f.Dps:0} dps", detail, "", "");
            return;
        }

        var member = _sync.Members.FirstOrDefault(m =>
            m.Name.StartsWith(popup.MemberName, StringComparison.OrdinalIgnoreCase));
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
                        $"{Pad(b.Name, 13)} {FmtDamage(b.Total),6} {b.Total * 100 / logTotal,3}%"));
                popup.Update($"~{logRow.Name} · {logRow.WindowDps:0} dps", lines, "", "");
                return;
            }
            popup.Update(popup.MemberName,
                _sync.Active
                    ? "(no data — not in your log and\n not sharing via group sync)"
                    : "(not in your log — exact numbers\n need group sync)",
                "", "");
            return;
        }

        string rows;
        var total = member.Breakdown.Sum(b => b.Total);
        if (total > 0)
            rows = string.Join("\n", member.Breakdown.Select(b =>
                $"{Pad(b.Name, 13)} {FmtDamage(b.Total),6} {b.Total * 100 / total,3}%"));
        else
            rows = "(no damage yet)";

        var m = member.Motes;
        var motesSummary = m.Total > 0
            ? $"✨ {m.Total} motes ({m.PerHour:0.#}/h)"
            : "✨ no motes yet";
        var motesDetail = m.Total > 0
            ? string.Join("\n", m.Tiers.Select(t => $"{Pad(TierShort(t.Name), 12)} ×{t.Count}"))
            : "";

        popup.Update($"{member.Name} · {member.Dps:0} dps", rows, motesSummary, motesDetail);
    }

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

    /// <summary>"Mote of Greater Potential" → "Greater"; the tierless base mote → "Base".</summary>
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
                System.Diagnostics.Process.Start(staged, "/SILENT");
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
        if (e.ChangedButton == MouseButton.Left) DragMove();
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
        if (best is null) return;
        w.DockHost = best;
        w.Left = best.Left;
        w.Top = best.Top + best.ActualHeight + DockGap;
        RepositionFollowers(w);
    }

    private IEnumerable<Window> SnapHosts(SectionWindow w)
    {
        yield return this;
        foreach (var other in _sectionWindows.Values)
        {
            if (ReferenceEquals(other, w)) continue;
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
            if (_ui.SectionPositions.TryGetValue(key, out var p) && p is [var x, var y]
                && !double.IsNaN(x) && !double.IsNaN(y))
            {
                _sectionWindows[key].Left = x;
                _sectionWindows[key].Top = y;
            }
        }
        Dispatcher.BeginInvoke(() =>
        {
            Window previous = this;
            foreach (var key in SectionKeys)
            {
                var win = _sectionWindows[key];
                if (!_ui.SectionPositions.ContainsKey(key))
                {
                    win.DockHost = previous;
                    win.Left = previous.Left;
                    win.Top = previous.Top + previous.ActualHeight + DockGap;
                    previous = win;
                }
            }
            // Saved free positions re-magnetise by adjacency, top-down.
            foreach (var win in _sectionWindows.Values.OrderBy(v => v.Top).ToList())
                if (win.DockHost is null) SnapWindow(win);
            RepositionFollowers(this);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
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

    private void OnBreakdownToggle(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ui.ShowBreakdown = !_ui.ShowBreakdown;
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
                "Start a new session? DPS, fights, loot, and motes counters reset " +
                "from now; spawn timers and group sync keep running.",
                "Reset session", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        _stats.Reset();
        _group.Reset();
        _popup?.Close();
        Tick();
    }

    private void OnGroupRowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: string row }) return;
        var name = row.TrimStart('~').Split(' ')[0].Trim();
        if (name.Length == 0) return;
        TogglePopup(name);
    }

    private void OnGroupSyncMenu(object sender, RoutedEventArgs e)
    {
        var relay = _sync.RelayUrl.Length > 0 ? _sync.RelayUrl : GroupSync.DefaultRelay;
        var dlg = new SyncDialog(_sync.GroupCode, relay) { Owner = this };
        if (dlg.ShowDialog() == true) _sync.Configure(dlg.GroupCode, dlg.RelayUrl);
    }
}
