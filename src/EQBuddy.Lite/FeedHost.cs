using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EQBuddy.Lite;

/// <summary>
/// One FEED window: the heading (with the + that opens another), the tab strip, and
/// whichever pane's body is in front. A window can hold several panes as tabs — the game's
/// own chat windows work that way, and a second lens on the log rarely deserves a second
/// rectangle of screen. One <see cref="FeedView"/> is drawn at a time; the others keep
/// score of what they would have shown and wear a dot on their tab.
/// </summary>
internal sealed class FeedHost
{
    /// <summary>The pane whose key names this WINDOW. Its Rows, Show, and section
    /// geometry belong to the window; its filters belong to its own tab like any other.</summary>
    public FeedPane Pane { get; }

    public string Key => Pane.Key;
    public StackPanel Root { get; }
    public TextBlock Header { get; }
    public Rectangle TopSep { get; }
    public IReadOnlyList<FeedView> Views => _views;

    /// <summary>The tab in front. A host always has at least the pane that names it, so
    /// this is only unsafe in the instant between tearing one down and building the next —
    /// which is why <see cref="Render"/> and the + guard on <see cref="Views"/> being
    /// non-empty rather than trusting it.</summary>
    public FeedView Active => _views[Math.Clamp(_activeIndex, 0, _views.Count - 1)];

    private readonly MainWindow _owner;
    private readonly LiteUiSettings _ui;
    private readonly WrapPanel _tabStrip;
    private readonly Decorator _bodyHost;
    private readonly List<FeedView> _views = [];
    private int _activeIndex;

    private static readonly Brush DimBrush = FeedPalette.Frozen("#7B8794");
    private static readonly Brush TabOnFg = FeedPalette.Frozen("#DDE5EC");
    private static readonly Brush TabOffFg = FeedPalette.Frozen("#6E7A87");
    private static readonly Brush DotBrush = FeedPalette.Frozen("#D9C46B");
    private static readonly Brush PillOnFg = FeedPalette.Frozen("#D9C46B");

    private static Brush Fill(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    private static readonly Brush TabOnBg = Fill(0x2E);
    private static readonly Brush TabOffBg = Fill(0x0C);
    private static readonly Brush TabOnBorder = Fill(0x55);
    private static readonly Brush TabOffBorder = Fill(0x1A);

    public FeedHost(MainWindow owner, LiteUiSettings ui, FeedPane pane)
    {
        _owner = owner;
        _ui = ui;
        Pane = pane;

        TopSep = new Rectangle
        {
            Height = 1,
            Fill = Fill(0x2A),
            Margin = new Thickness(0, 9, 0, 7),
        };

        Header = new TextBlock
        {
            Text = "▸ FEED · live",
            FontSize = 9.5,
            Foreground = DimBrush,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Click to show/hide · drag to pop out · right-click for tabs, "
                + "colours, and filter reset",
        };

        // The + is a real Button parked at the RIGHT end of the heading row, away from
        // the heading's own drag/toggle surface. A bare "+" TextBlock only hit-tests over
        // its own strokes, so most of what looks like the button isn't (that is how the
        // 1.68.0 + shipped un-clickable).
        var spawn = new Button
        {
            Content = "+",
            ToolTip = "Open another FEED window — its own filters, starting as a copy "
                + "of this tab's. Right-click the window for a new TAB instead.",
            Cursor = Cursors.Hand,
            Focusable = false,
            FontSize = 11,
            Foreground = PillOnFg,
            Background = Fill(0x10),
            BorderBrush = TabOnBorder,
            Padding = new Thickness(7, 0, 7, 1),
            // Clear of the section window's ✕, which overlays the same top-right corner.
            Margin = new Thickness(8, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Template = (ControlTemplate)owner.FindResource("FlatButtonTemplate"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(spawn, "SpawnFeed");
        spawn.Click += (_, _) => { if (_views.Count > 0) _owner.SpawnFeedPane(Active); };

        var headRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(spawn, Dock.Right);
        headRow.Children.Add(spawn);
        headRow.Children.Add(Header);

        _tabStrip = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        _bodyHost = new Decorator();

        Root = new StackPanel();
        Root.Children.Add(TopSep);
        Root.Children.Add(headRow);
        Root.Children.Add(_tabStrip);
        Root.Children.Add(_bodyHost);
        // Built up front and again on every opening: the submenus list other windows and
        // closed panes, which change. An EMPTY ContextMenu never shows at all, so filling
        // it only from the Opened event would mean a right-click that did nothing.
        Root.ContextMenu = new ContextMenu();
        Root.ContextMenuOpening += (_, _) => BuildMenu();
    }

    /// <summary>Take a new tab list (order and membership come from the panes). Keeps the
    /// pane that was in front in front when it is still here.</summary>
    public void SetViews(IReadOnlyList<FeedView> views)
    {
        var wasActive = _views.Count > 0 ? Active.Key : Pane.Key;
        _views.Clear();
        _views.AddRange(views);
        var found = _views.FindIndex(v => v.Key == wasActive);
        _activeIndex = found >= 0 ? found : 0;
        ShowBody();
        BuildTabs();
        BuildMenu();   // never empty: an empty ContextMenu does not open at all
    }

    /// <summary>Let go of whatever body this window is showing. A pane moving between
    /// windows has to leave the old one FIRST — an element cannot be the logical child of
    /// two parents, and the rebuild re-seats several windows in one pass.</summary>
    public void ClearBody() => _bodyHost.Child = null;

    private void ShowBody()
    {
        if (_views.Count == 0) { _bodyHost.Child = null; return; }
        // Belt and braces for the same rule: if this body is still parented elsewhere
        // (a host we have not re-seated yet), take it back.
        if (Active.Body.Parent is Decorator previous && !ReferenceEquals(previous, _bodyHost))
            previous.Child = null;
        _bodyHost.Child = Active.Body;
    }

    public void Select(FeedView view)
    {
        var at = _views.IndexOf(view);
        if (at < 0 || at == _activeIndex) return;
        _activeIndex = at;
        ShowBody();
        Active.Invalidate();
        BuildTabs();
        Render();
    }

    public void ApplyInnerWidth(double width)
    {
        foreach (var view in _views) view.ApplyInnerWidth(width);
    }

    /// <summary>True while this window should be wearing its combat outline: some tab
    /// asked for it, that tab is showing YOUR rows, and blows are still landing.</summary>
    public bool Wants(bool combatOn) =>
        combatOn && _views.Any(v => v.Pane.CombatGlow && v.Pane.Filters.You);

    public void Render()
    {
        if (_views.Count == 0)
        {
            _tabStrip.Visibility = Visibility.Collapsed;
            _bodyHost.Visibility = Visibility.Collapsed;
            return;
        }
        var title = Active.Title;
        if (!Pane.Show)
        {
            SetHeader($"▸ {title}");
            _tabStrip.Visibility = Visibility.Collapsed;
            _bodyHost.Visibility = Visibility.Collapsed;
            return;
        }
        _tabStrip.Visibility = _views.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _bodyHost.Visibility = Visibility.Visible;

        for (var i = 0; i < _views.Count; i++) _views[i].Render(active: i == _activeIndex);
        SetHeader($"▾ {title} · {Active.StatusSuffix}");
        if (_views.Count > 1) RefreshTabLabels();
    }

    private void SetHeader(string text)
    {
        if (Header.Text != text) Header.Text = text;
    }

    // ---- tabs ----

    private readonly List<(FeedView View, TextBlock Label, Border Chrome)> _tabs = [];

    private void BuildTabs()
    {
        _tabStrip.Children.Clear();
        _tabs.Clear();
        if (_views.Count < 2) return;
        foreach (var view in _views)
        {
            var label = new TextBlock
            {
                Text = view.Title,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(label);

            var close = new Button
            {
                Content = "✕",
                ToolTip = $"Close {view.Title} (its settings are remembered — right-click "
                    + "the window to reopen it)",
                Cursor = Cursors.Hand,
                Focusable = false,
                FontSize = 9,
                Foreground = TabOffFg,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Padding = new Thickness(4, 1, 4, 2),
                // A clear gap between the label and the ✕: with them nose to tail,
                // selecting a tab closed it often enough to be infuriating.
                Margin = new Thickness(10, 0, -3, 0),
                Template = (ControlTemplate)_owner.FindResource("FlatButtonTemplate"),
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(close, "CloseFeedTab");
            close.Click += (_, e) => { e.Handled = true; _owner.CloseFeedPane(view); };
            row.Children.Add(close);

            var chrome = new Border
            {
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Padding = new Thickness(10, 3, 7, 4),
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = row,
                ToolTip = "Click to bring this tab forward · double-click to rename",
            };
            var captured = view;
            chrome.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (e.ClickCount == 2) _owner.RenameFeedPane(captured);
                else Select(captured);
            };
            _tabStrip.Children.Add(chrome);
            _tabs.Add((view, label, chrome));
        }
        RefreshTabLabels();
    }

    /// <summary>Selected state and the "something happened here" dot.</summary>
    private void RefreshTabLabels()
    {
        foreach (var (view, label, chrome) in _tabs)
        {
            var on = ReferenceEquals(view, Active);
            var dot = !on && view.Unseen > 0 ? " •" : "";
            var text = view.Title + dot;
            if (label.Text != text) label.Text = text;
            label.Foreground = on ? TabOnFg : dot.Length > 0 ? DotBrush : TabOffFg;
            chrome.Background = on ? TabOnBg : TabOffBg;
            chrome.BorderBrush = on ? TabOnBorder : TabOffBorder;
        }
    }

    // ---- right-click menu ----

    private void BuildMenu()
    {
        if (_views.Count == 0) return;
        var menu = Root.ContextMenu!;
        menu.Items.Clear();

        MenuItem Item(string header, Action click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
            return item;
        }

        var active = Active;
        Item("Rename this tab…", () => _owner.RenameFeedPane(active));

        // Text size is a property of the WINDOW (like its row count): tabs sharing a
        // window share it, or the window would resize on every tab click.
        var fonts = new MenuItem { Header = "Text size" };
        foreach (var size in new double[] { 9, 10, 11, 12, 13, 14, 16, 18 })
        {
            var pick = size;
            var item = new MenuItem
            {
                Header = size == 11 ? "11 (default)" : $"{size:0}",
                IsCheckable = true,
                IsChecked = Math.Abs(Pane.FontSize - size) < 0.1,
            };
            item.Click += (_, _) => _owner.SetFeedFontSize(this, pick);
            fonts.Items.Add(item);
        }
        menu.Items.Add(fonts);
        menu.Items.Add(new Separator());

        Item("New tab in this window", () => _owner.AddFeedTab(this));
        Item("Move this tab to its own window", () => _owner.DetachFeedTab(active),
            _views.Count > 1);

        var others = _owner.FeedHostsOtherThan(this);
        var merge = new MenuItem { Header = "Merge this window into", IsEnabled = others.Count > 0 };
        foreach (var other in others)
        {
            // The window's own name, not its active tab's: during a rebuild another host
            // may not have been handed its views yet, and this menu is about windows.
            var item = new MenuItem { Header = FeedTitle(other.Pane) };
            var target = other;
            item.Click += (_, _) => _owner.MergeFeedWindow(this, target);
            merge.Items.Add(item);
        }
        menu.Items.Add(merge);

        var closed = _owner.ClosedFeedPanes();
        var reopen = new MenuItem { Header = "Reopen closed feed", IsEnabled = closed.Count > 0 };
        foreach (var pane in closed)
        {
            var item = new MenuItem { Header = FeedTitle(pane) };
            var target = pane;
            item.Click += (_, _) => _owner.ReopenFeedPane(target);
            reopen.Items.Add(item);
        }
        menu.Items.Add(reopen);

        menu.Items.Add(new Separator());

        var split = new MenuItem
        {
            Header = "Chat layout (incoming on the right)",
            IsCheckable = true,
            IsChecked = active.Pane.SplitSides,
            ToolTip = "Rows for damage coming at you hug the right edge and everything "
                + "you and yours do stays left, with a gap wherever the side changes. "
                + "Needs a window wide enough for the lines to FIT — a line that "
                + "overflows fills the width and has nowhere to align to. Drag the ◢ "
                + "grip wider (feeds go to 2400 px).",
        };
        split.Click += (_, _) => _owner.SetFeedSplitSides(active, split.IsChecked);
        menu.Items.Add(split);

        var glow = new MenuItem
        {
            Header = "Red outline while in combat",
            IsCheckable = true,
            IsChecked = active.Pane.CombatGlow,
            ToolTip = "Flickers a red border around this window while blows are landing. "
                + "Only lights when the tab is showing YOUR rows (the 'you' filter).",
        };
        glow.Click += (_, _) =>
        {
            active.Pane.CombatGlow = glow.IsChecked;
            _ui.Save();
        };
        menu.Items.Add(glow);

        Item("Colours…", () => _owner.EditFeedColors(active));
        Item("Reset filters", () => active.ResetFilters());
    }

    /// <summary>A pane's tab label, without needing its view (the closed ones have none).</summary>
    public static string FeedTitle(FeedPane pane) => pane.Title is { Length: > 0 } t ? t
        : pane.Key == "feed" ? "FEED"
        : pane.Key.StartsWith("feed", StringComparison.Ordinal) ? "FEED " + pane.Key[4..]
        : pane.Key;
}
