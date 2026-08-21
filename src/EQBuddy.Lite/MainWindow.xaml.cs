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

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _sync.Start();
        CheckUpdates();

        Closing += (_, _) =>
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.Save();
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

        TitleText.Text = string.IsNullOrEmpty(_stats.CharacterName)
            ? "EQdps" : _stats.CharacterName;
        StatusDot.Fill = _watcher.LastGrowth is { } g && now - g < TimeSpan.FromSeconds(30)
            ? Brushes.LimeGreen : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        DpsText.Text = s.CurrentDps > 0
            ? $"⚔ {s.SessionDps:0} dps  (now {s.CurrentDps:0})"
            : $"⚔ {s.SessionDps:0} dps";

        // DPS breakdown: your top damage sources (melee, spells, abilities) with their
        // share of session damage — the Lite take on the full app's Damage breakout.
        var totalDamage = s.DamageBySource.Sum(d => d.Total);
        if (totalDamage > 0)
        {
            BreakdownList.ItemsSource = s.DamageBySource
                .Take(5)
                .Select(d => $"{Pad(d.Name, 13)} {FmtDamage(d.Total),6} {d.Total * 100 / totalDamage,3}%")
                .ToList();
            BreakdownList.Visibility = Visibility.Visible;
        }
        else BreakdownList.Visibility = Visibility.Collapsed;

        var petDamage = s.PetAbilities.Sum(p => p.Total);
        PetText.Text = s.PetName.Length > 0
            ? $"🐾 {s.PetName}: {petDamage / Math.Max(1, s.CombatSeconds):0.#} dps"
            : petDamage > 0 ? $"🐾 pet: {petDamage / Math.Max(1, s.CombatSeconds):0.#} dps"
            : "🐾 no pet";

        var motes = Motes.Summarize(s.Loot, s.Elapsed);
        MotesText.Text = motes.Total == 0
            ? "✨ no motes yet"
            : $"✨ {motes.Total} motes ({motes.PerHour:0.#}/h)" +
              string.Concat(motes.Tiers.Select(t => $"\n      {TierShort(t.Item)} ×{t.Count}"));

        // Spawn timers: soonest first (Core's Snapshot order), section hidden entirely
        // when no camp is running — an empty list isn't worth panel height.
        var timers = _spawnTimers.Snapshot(now);
        if (timers.Count > 0)
        {
            SpawnList.ItemsSource = timers
                .Take(6)
                .Select(t => $"{Pad(t.Name, 13)} {FmtSpawn(t, now)}")
                .ToList();
            SpawnSection.Visibility = Visibility.Visible;
        }
        else SpawnSection.Visibility = Visibility.Collapsed;

        _sync.Publish(_stats.CharacterName ?? "", s.CurrentDps, s.SessionDps);

        List<string> rows;
        if (_sync.Active)
        {
            GroupLabel.Text = _sync.LastError is { } err
                ? $"GROUP · sync {_sync.GroupCode} · {err}"
                : $"GROUP · synced · {_sync.GroupCode}";
            var synced = _sync.Members;
            var syncedNames = new HashSet<string>(synced.Select(m => m.Name),
                StringComparer.OrdinalIgnoreCase);
            rows = synced.Select(m => $"{Pad(m.Name, 12)} {m.Dps,5:0} dps").ToList();
            // Players near you who aren't running the app still show, marked approximate.
            rows.AddRange(_group.Snapshot(now, s.PetName)
                .Where(r => !syncedNames.Contains(r.Name))
                .Select(r => $"{Pad("~" + r.Name, 12)} {r.WindowDps,5:0} dps"));
            rows = rows.Take(8).ToList();
            if (rows.Count == 0) rows.Add("(waiting for group…)");
        }
        else
        {
            GroupLabel.Text = "GROUP · from your log, near you";
            rows = _group.Snapshot(now, s.PetName)
                .Take(8)
                .Select(r => $"{Pad(r.Name, 12)} {r.WindowDps,5:0} dps")
                .ToList();
            if (rows.Count == 0) rows.Add("(no group activity nearby)");
        }
        GroupList.ItemsSource = rows;
    }

    private static string Pad(string s, int width) =>
        s.Length >= width ? s[..width] : s.PadRight(width);

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
            ? $"{(int)left.TotalHours}h{left.Minutes:00}m"
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCheckUpdatesMenu(object sender, RoutedEventArgs e) => CheckUpdates();

    private void OnGroupSyncMenu(object sender, RoutedEventArgs e)
    {
        var relay = _sync.RelayUrl.Length > 0 ? _sync.RelayUrl : GroupSync.DefaultRelay;
        var dlg = new SyncDialog(_sync.GroupCode, relay) { Owner = this };
        if (dlg.ShowDialog() == true) _sync.Configure(dlg.GroupCode, dlg.RelayUrl);
    }
}
