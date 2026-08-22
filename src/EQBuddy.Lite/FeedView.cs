using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQBuddy.Lite;

/// <summary>One row of a FEED list: pre-formatted text and its brush. Bound by the
/// FeedRowTemplate resource in MainWindow.xaml.</summary>
public sealed record FeedRow(string Text, Brush Color);

/// <summary>One FEED window: heading (with the + that spawns another, and ✕ to close
/// extras), the search-chip row, the sixteen filter pills, and the fixed-height list.
/// The panel can hold any number of these, all filtering the same shared
/// <see cref="DamageFeed"/> buffers at render time — filtering is a lens, so extra
/// windows cost only their own render, never a second copy of the scrollback.
/// Everything the view owns persists in its <see cref="FeedPane"/>.</summary>
internal sealed class FeedView
{
    public FeedPane Pane { get; }
    public string Key => Pane.Key;
    public StackPanel Root { get; }
    public TextBlock Header { get; }
    public Rectangle TopSep { get; }

    private readonly MainWindow _owner;
    private readonly LiteUiSettings _ui;
    private readonly DamageFeed _feed;
    private readonly WrapPanel _searchRow;
    private readonly WrapPanel _pillRow;
    private readonly ListBox _list;
    private readonly TextBox _searchBox;
    private readonly List<Action> _pillRefreshers = [];

    /// <summary>The bound row list, newest first. Held for the life of the window and
    /// mutated in place: a live feed inserts the handful of rows that arrived since the
    /// last frame at the top and drops the same number off the bottom. Rebuilding it (the
    /// old behaviour, once a second) reset the ListBox, threw away every realised row
    /// container, and made the arrival of a line something you saw happen in a lump.</summary>
    private readonly ObservableCollection<FeedRow> _rows = [];

    /// <summary>How far into <see cref="DamageFeed"/>'s sequence <see cref="_rows"/> has
    /// been filled, or -1 when the next render must rebuild from scratch (a filter moved,
    /// the mode flipped, the window was re-opened).</summary>
    private long _cursor = -1;

    /// <summary>The list is showing the "nothing matching yet" placeholder, which the
    /// first real row has to clear.</summary>
    private bool _placeholder;

    /// <summary>Rows kept per window. Deep enough to scroll back through a long fight;
    /// virtualisation means only the visible dozen ever become controls.</summary>
    private const int MaxRows = 2000;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush YouBrush = Frozen(0xCF, 0xE3, 0xF5);
    private static readonly Brush CritBrush = Frozen(0xE8, 0xCE, 0x9C);
    private static readonly Brush PetBrush = Frozen(0x8F, 0xD4, 0xC8);
    private static readonly Brush GroupBrush = Frozen(0xB9, 0xA7, 0xE8);
    private static readonly Brush TakenBrush = Frozen(0xE8, 0x9C, 0x9C);
    private static readonly Brush HealBrush = Frozen(0x8B, 0xE2, 0x8B);
    private static readonly Brush DimBrush = Frozen(0x7B, 0x87, 0x94);
    private static readonly Brush KillBrush = Frozen(0xD9, 0xC4, 0x6B);
    private static readonly Brush RawBrush = Frozen(0xAE, 0xBB, 0xC7);
    private static readonly Brush LinkBrush = Frozen(0x55, 0x61, 0x6C);
    private static readonly Brush InputBrush = Frozen(0xDD, 0xE5, 0xEC);

    private static readonly Brush PillOnFg = Frozen(0xD9, 0xC4, 0x6B);
    private static readonly Brush PillOffFg = Frozen(0x55, 0x61, 0x6C);
    private static readonly Brush PillOnBg = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PillOffBg = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PillOnBorder = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    private static readonly Brush PillOffBorder = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

    private string Title => Key == "feed" ? "FEED" : "FEED " + Key[4..];

    public FeedView(MainWindow owner, LiteUiSettings ui, DamageFeed feed, FeedPane pane)
    {
        _owner = owner;
        _ui = ui;
        _feed = feed;
        Pane = pane;

        TopSep = new Rectangle
        {
            Height = 1,
            Fill = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 9, 0, 7),
        };

        Header = new TextBlock
        {
            Text = $"▸ {Title} · live",
            FontSize = 9.5,
            Foreground = DimBrush,
            Cursor = Cursors.Hand,
            ToolTip = "Click to show/hide · drag to pop out · a live feed of combat "
                + "from your log, with filters",
        };
        // The + is a real Button parked at the RIGHT end of the heading row, away from
        // the heading's own drag/toggle surface. It was a bare "+" TextBlock beside the
        // title in 1.68.0 and went unclicked: a 10 px glyph only hit-tests over its own
        // strokes, so most of what looks like the button isn't. A templated Button has
        // padding, a border, and a hover state — the whole pill is the target.
        // (Closing lives on the section window's ✕, which spawned feeds repoint to a
        // real close; the original FEED can be hidden in ⚙ but never closed, so there
        // is always a window left to press + on.)
        var spawn = new Button
        {
            Content = "+",
            ToolTip = "Open another FEED window — its own filters, starting as a copy "
                + "of this one's",
            Cursor = Cursors.Hand,
            Focusable = false,
            FontSize = 11,
            Foreground = PillOnFg,
            Background = PillOffBg,
            BorderBrush = PillOnBorder,
            Padding = new Thickness(7, 0, 7, 1),
            // Clear of the section window's ✕, which overlays the same top-right
            // corner: the two controls do opposite things and must not share a pixel.
            Margin = new Thickness(8, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Template = (ControlTemplate)owner.FindResource("FlatButtonTemplate"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(spawn, "SpawnFeed");
        spawn.Click += (_, _) => _owner.SpawnFeedPane(this);

        // DockPanel, not StackPanel: the + rides the right edge of whatever width the
        // ◢ grip has given this window, so it reads as its own control rather than
        // punctuation after the title.
        var headRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(spawn, Dock.Right);
        headRow.Children.Add(spawn);
        headRow.Children.Add(Header);
        Header.VerticalAlignment = VerticalAlignment.Center;

        _searchRow = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _pillRow = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 2) };

        _list = new ListBox
        {
            Margin = new Thickness(2, 2, 0, 0),
            Visibility = Visibility.Collapsed,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)_owner.FindResource("FeedItemStyle"),
            ItemTemplate = (DataTemplate)_owner.FindResource("FeedRowTemplate"),
            ItemsSource = _rows,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        _searchBox = new TextBox
        {
            MinWidth = 64,
            FontSize = 10,
            Padding = new Thickness(3, 0, 3, 1),
            Margin = new Thickness(0, 1, 2, 1),
            Background = PillOffBg,
            Foreground = InputBrush,
            CaretBrush = InputBrush,
            BorderBrush = PillOffBorder,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Type a word and press Enter — rows must contain one of the chips "
                + "(actor, ability, target, or annotation; try slay, crit, riposte, a name…)",
        };
        _searchBox.KeyDown += (_, e) =>
        {
            // Fully qualified: this class's Key property (the section key) shadows
            // the input enum inside instance members.
            if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; CommitSearch(); }
            else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; _searchBox.Clear(); }
        };

        Root = new StackPanel();
        Root.Children.Add(TopSep);
        Root.Children.Add(headRow);
        Root.Children.Add(_searchRow);
        Root.Children.Add(_pillRow);
        Root.Children.Add(_list);

        BuildPills();
        RefreshSearchRow();
        ApplyInnerWidth(double.NaN);
    }

    public int RowsClamped() => Math.Clamp(Pane.Rows, 4, 40);

    /// <summary>The section's width, from the ◢ grip (NaN = auto). The list takes the
    /// cap as a FIXED width, not a max — this window holds the size the user set and
    /// never breathes with its content.</summary>
    public void ApplyInnerWidth(double width)
    {
        var cap = double.IsNaN(width) ? 340 : Math.Max(150, width);
        _searchRow.MaxWidth = cap;
        _pillRow.MaxWidth = cap;
        _list.Width = cap;
    }

    /// <summary>Throw away what is drawn and rebuild from the buffer on the next render.
    /// Anything that changes which rows QUALIFY lands here — the incremental path only
    /// knows how to add what is new, not to re-judge what is already on screen.</summary>
    public void Invalidate() => _cursor = -1;

    public void Render()
    {
        if (!Pane.Show)
        {
            SetHeader($"▸ {Title} · live");
            _searchRow.Visibility = Visibility.Collapsed;
            _pillRow.Visibility = Visibility.Collapsed;
            _list.Visibility = Visibility.Collapsed;
            // Nothing repaints a hidden list, so re-opening starts from scratch.
            Invalidate();
            return;
        }
        var f = Pane.Filters;
        var raw = f.RawMode;
        _searchRow.Visibility = Visibility.Visible;
        // The who/kind pills describe parsed combat events; raw mode shows the log
        // verbatim, so they'd be dead controls there.
        _pillRow.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        // The grip's row count is the VIEWPORT, and a FIXED one: this many rows tall
        // whether the filter matches two rows or two thousand, so the window (and the
        // stack docked under it) never moves as the fight ebbs.
        _list.Height = RowsClamped() * 14 + 4;
        _list.Visibility = Visibility.Visible;

        // Newest-first means every refresh shifts rows under a reader who has scrolled
        // back — so while they're anywhere but the top, the list freezes and the header
        // says so. The cursor doesn't advance either, so resuming catches up rather than
        // skipping whatever landed while they were reading.
        if (Scroller() is { VerticalOffset: > 0.5 })
        {
            SetHeader($"▾ {Title} · paused — scroll to top to resume");
            return;
        }
        SetHeader(raw ? $"▾ {Title} · raw log · live" : $"▾ {Title} · live");

        var rebuild = _cursor < 0;
        var since = rebuild ? 0 : _cursor;
        List<FeedRow> fresh;
        if (raw)
        {
            fresh = _feed.SnapshotRaw(f.SearchTerms, MaxRows, since, out var cursor)
                .Select(l => new FeedRow(
                    l.Time == DateTime.MinValue ? l.Text : $"{l.Time:HH:mm:ss}  {l.Text}",
                    RawBrush))
                .ToList();
            _cursor = cursor;
        }
        else
        {
            fresh = _feed.Snapshot(f, MaxRows, since, out var cursor).Select(RowOf).ToList();
            _cursor = cursor;
        }
        if (!rebuild && fresh.Count == 0) return;   // the common frame: nothing to do

        // A rebuild starts empty; so does a list showing only the placeholder, which is
        // not a row anyone wants pushed down the page.
        if (rebuild || _placeholder)
        {
            _rows.Clear();
            _placeholder = false;
        }
        if (_rows.Count == 0)
        {
            foreach (var row in fresh) _rows.Add(row);   // already newest-first
        }
        else
        {
            // Oldest of the new rows first, each pushed onto the top, so they end up
            // newest-first above whatever was already there.
            for (var i = fresh.Count - 1; i >= 0; i--) _rows.Insert(0, fresh[i]);
        }
        while (_rows.Count > MaxRows) _rows.RemoveAt(_rows.Count - 1);

        // An empty match set renders as one dim row INSIDE the fixed-height list —
        // swapping the list for a message would change the window's height.
        if (_rows.Count == 0)
        {
            _rows.Add(new FeedRow("(nothing matching yet)", DimBrush));
            _placeholder = true;
        }
    }

    private void SetHeader(string text)
    {
        if (Header.Text != text) Header.Text = text;
    }

    /// <summary>The ListBox's internal ScrollViewer, once templated (null before the
    /// first layout pass).</summary>
    private ScrollViewer? Scroller()
    {
        if (VisualTreeHelper.GetChildrenCount(_list) == 0) return null;
        return VisualTreeHelper.GetChild(_list, 0) is Border b ? b.Child as ScrollViewer : null;
    }

    private static FeedRow RowOf(FeedEntry e)
    {
        var t = e.Time.ToString("HH:mm:ss");
        // The line as the game wrote it, colour-coded by what the parser made of it.
        // The reformatted version below is only a fallback for entries with no raw text
        // (captured before 1.68.1): a filtered feed is easier to read when its rows say
        // exactly what the log says, and it stops the two modes looking like two apps.
        if (e.Raw is { Length: > 0 } raw) return new FeedRow($"{t}  {raw}", BrushFor(e));
        // The log's own annotation wins (it already says "Riposte Critical" when both
        // apply); a bare crit flag gets the plain tag.
        var tag = e.Note is { Length: > 0 } n ? $" ({n})" : e.Crit ? " (Crit)" : "";
        var actor = e.Who == FeedWho.You ? "" : $"{e.Actor}: ";
        return e.Kind switch
        {
            FeedKind.Melee or FeedKind.Spell or FeedKind.Dot or FeedKind.Aux => new FeedRow(
                $"{t}  {actor}{e.Ability} → {e.Target}  {e.Amount:N0}{tag}",
                e.Crit ? CritBrush : e.Who switch
                {
                    FeedWho.Pet => PetBrush,
                    FeedWho.Group => GroupBrush,
                    _ => YouBrush,
                }),
            FeedKind.Taken => new FeedRow(
                $"{t}  {e.Actor}{(e.Ability.Length > 0 ? $" {e.Ability}" : "")} → you  {e.Amount:N0}",
                TakenBrush),
            FeedKind.Heal => new FeedRow(
                e.Incoming
                    ? $"{t}  {e.Actor} heals you  +{e.Amount:N0}"
                    : $"{t}  {e.Ability} → {e.Target}  +{e.Amount:N0}",
                HealBrush),
            FeedKind.Miss => new FeedRow(
                e.Incoming ? $"{t}  missed you" : $"{t}  you miss", DimBrush),
            FeedKind.Kill => new FeedRow($"{t}  {e.Actor} slew {e.Target}", KillBrush),
            FeedKind.Resist => new FeedRow(
                $"{t}  {(e.Ability.Length > 0 ? e.Ability : "spell")} resisted", DimBrush),
            _ => new FeedRow(
                $"{t}  {(e.Ability.Length > 0 ? e.Ability : "spell")} fizzled", DimBrush),
        };
    }

    /// <summary>Row colour by what the line turned out to be — the one piece of reading
    /// help a verbatim log line can't give you itself.</summary>
    private static Brush BrushFor(FeedEntry e) => e.Kind switch
    {
        FeedKind.Kill => KillBrush,
        FeedKind.Heal => HealBrush,
        FeedKind.Taken => TakenBrush,
        FeedKind.Miss or FeedKind.Resist or FeedKind.Fizzle => DimBrush,
        _ => e.Crit ? CritBrush : e.Who switch
        {
            FeedWho.Pet => PetBrush,
            FeedWho.Group => GroupBrush,
            _ => YouBrush,
        },
    };

    /// <summary>The filter pills — sixteen toggles sharing one tiny template. Each pill
    /// owns its refresh closure; clicking saves and re-renders THIS view only, so two
    /// feed windows never fight over a filter.</summary>
    private void BuildPills()
    {
        var f = Pane.Filters;
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
                tb.Foreground = on ? PillOnFg : PillOffFg;
                pill.Background = on ? PillOnBg : PillOffBg;
                pill.BorderBrush = on ? PillOnBorder : PillOffBorder;
            }
            pill.MouseLeftButtonDown += (_, args) =>
            {
                args.Handled = true;
                click();
                _ui.Save();
                Refresh();
                Invalidate();
                Render();
            };
            Refresh();
            _pillRefreshers.Add(Refresh);
            _pillRow.Children.Add(pill);
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
        Pill("dmg", "Minimum damage to show — click to cycle",
            () => f.MinDamage > 0,
            () => f.MinDamage = f.MinDamage switch { 0 => 100, 100 => 500, 500 => 1000, 1000 => 5000, _ => 0 },
            () => f.MinDamage == 0 ? "dmg·any" : $"dmg·{f.MinDamage}+");
        Pill("type", "Melee damage type — click to cycle",
            () => f.MeleeType != "all",
            () => f.MeleeType = f.MeleeType switch
            {
                "all" => "slash", "slash" => "pierce", "pierce" => "blunt",
                "blunt" => "archery", _ => "all",
            },
            () => $"type·{f.MeleeType}");
    }

    // ---- search chips: [all] [term ✕]… [box] [+] ----

    private void CommitSearch()
    {
        var term = _searchBox.Text.Trim();
        _searchBox.Clear();
        if (term.Length == 0) return;
        var f = Pane.Filters;
        if (!f.SearchTerms.Any(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase)))
            f.SearchTerms.Add(term);
        _ui.Save();
        RefreshSearchRow();
        Invalidate();
        Render();
    }

    /// <summary>Rebuild the whole row — chips are cheap and a full rebuild keeps one
    /// source of truth (the pane's list). The text box is a persistent instance so
    /// half-typed input survives a chip add/remove.</summary>
    private void RefreshSearchRow()
    {
        var f = Pane.Filters;
        _searchRow.Children.Clear();

        var all = FlatButton("all", f.RawMode ? PillOnFg : PillOffFg,
            "Show the raw log — every line the game writes (chat, emotes, system, " +
            "everything), not just parsed combat. Chips filter by text; click again " +
            "for the combat view.");
        if (f.RawMode)
        {
            all.Background = PillOnBg;
            all.BorderBrush = PillOnBorder;
        }
        all.Click += (_, _) =>
        {
            f.RawMode = !f.RawMode;
            _ui.Save();
            RefreshSearchRow();
            Invalidate();
            Render();
        };
        _searchRow.Children.Add(all);

        foreach (var term in f.SearchTerms.ToList())
        {
            var text = new TextBlock
            {
                Text = term,
                FontSize = 10,
                Foreground = PillOnFg,
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
                RefreshSearchRow();
                Invalidate();
                Render();
            };
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(text);
            body.Children.Add(remove);
            _searchRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 3, 1),
                Margin = new Thickness(0, 1, 4, 1),
                BorderThickness = new Thickness(1),
                Background = PillOnBg,
                BorderBrush = PillOnBorder,
                Child = body,
            });
        }

        _searchRow.Children.Add(_searchBox);
        var plus = FlatButton("+", PillOnFg, "Add the typed word as a chip (same as Enter)");
        plus.Click += (_, _) => CommitSearch();
        _searchRow.Children.Add(plus);
    }

    private Button FlatButton(string text, Brush fg, string tip) => new()
    {
        Content = text,
        ToolTip = tip,
        Cursor = Cursors.Hand,
        Focusable = false,
        FontSize = 10,
        Foreground = fg,
        Background = PillOffBg,
        BorderBrush = PillOffBorder,
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 1, 4, 1),
        Template = (ControlTemplate)_owner.FindResource("FlatButtonTemplate"),
    };
}
