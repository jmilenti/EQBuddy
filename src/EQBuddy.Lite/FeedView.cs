using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>One feed window's resolved colours. Built from the pane's <see cref="FeedColors"/>
/// once and rebuilt when the user changes them, so a render never parses a hex string.</summary>
internal sealed class FeedPalette
{
    public Brush You = Frozen("#CFE3F5"), Pet = Frozen("#8FD4C8"), Group = Frozen("#B9A7E8");
    public Brush Incoming = Frozen("#E89C9C"), Heal = Frozen("#8BE28B"), Crit = Frozen("#E8CE9C");
    public Brush Kill = Frozen("#D9C46B"), Spell = Frozen("#E8B24A"), Ability = Frozen("#FF8FC7");
    public Brush Cast = Frozen("#9FB6D0"), Mez = Frozen("#B48CDE"), Other = Frozen("#78838F");
    public Brush MezBreak = Frozen("#D9587E"), Consider = Frozen("#E0925A");
    public Brush Summary = Frozen("#7FD9E8"), Dim = Frozen("#7B8794");
    public Brush Xp = Frozen("#F2E33D"), Loot = Frozen("#4A8CFF");
    public Brush Money = Frozen("#33CC33"), Attack = Frozen("#4A8CFF");
    public Brush Faction = Frozen("#E04040"), Alert = Frozen("#F2E33D");

    public FeedPalette(FeedColors c)
    {
        You = Frozen(c.You, You); Pet = Frozen(c.Pet, Pet); Group = Frozen(c.Group, Group);
        Incoming = Frozen(c.Incoming, Incoming); Heal = Frozen(c.Heal, Heal);
        Crit = Frozen(c.Crit, Crit); Kill = Frozen(c.Kill, Kill); Spell = Frozen(c.Spell, Spell);
        Ability = Frozen(c.Ability, Ability); Cast = Frozen(c.Cast, Cast);
        Mez = Frozen(c.Mez, Mez); MezBreak = Frozen(c.MezBreak, MezBreak);
        Consider = Frozen(c.Consider, Consider);
        Other = Frozen(c.Other, Other); Summary = Frozen(c.Summary, Summary);
        Dim = Frozen(c.Dim, Dim); Xp = Frozen(c.Xp, Xp);
        Loot = Frozen(c.Loot, Loot); Money = Frozen(c.Money, Money);
        Attack = Frozen(c.Attack, Attack); Faction = Frozen(c.Faction, Faction);
        Alert = Frozen(c.Alert, Alert);
    }

    /// <summary>A hex colour as a frozen brush, or <paramref name="fallback"/> when the
    /// string is missing or malformed — a typo in the settings file must not blank a row.</summary>
    public static Brush Frozen(string? hex, Brush? fallback = null)
    {
        if (Parse(hex) is not { } color) return fallback ?? Brushes.Silver;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return ColorConverter.ConvertFromString(hex.Trim()) is Color c ? c : null;
        }
        catch (FormatException) { return null; }
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>One FEED pane's contents: the single filter line (mode, the filter popup,
/// search chips) and the fixed-height list. The window chrome around it — heading, tab
/// strip, +, ✕, context menu — belongs to <see cref="FeedHost"/>, because several panes
/// can share one window as tabs.
///
/// Every pane filters the same shared <see cref="DamageFeed"/> buffer at render time, so
/// extra panes cost only their own render, never a second copy of the scrollback.</summary>
internal sealed class FeedView
{
    public FeedPane Pane { get; }
    public string Key => Pane.Key;
    public StackPanel Body { get; }

    /// <summary>The pane whose window draws this one. Its Rows is the viewport height and
    /// its Show is the collapse state — those are properties of a WINDOW, and tabs in one
    /// window that disagreed about their height would resize it on every tab click.</summary>
    public FeedPane HostPane { get; set; }

    /// <summary>Rows that have arrived since this pane was last drawn at the bottom of its
    /// list — the "N new below" hint, and the dot on a background tab.</summary>
    public int Unseen { get; private set; }

    private readonly MainWindow _owner;
    private readonly LiteUiSettings _ui;
    private readonly DamageFeed _feed;
    private readonly WrapPanel _filterRow;
    private readonly StackPanel _pillRows;
    private WrapPanel? _pillCurrentRow;
    private readonly Popup _pillPopup;
    private readonly Button _pillButton;
    private readonly Button _modeButton;
    private readonly ListBox _list;
    private readonly TextBox _searchBox;
    private readonly List<Action> _pillRefreshers = [];
    private FeedPalette _palette;

    /// <summary>The bound row list, OLDEST first. Held for the life of the pane and
    /// mutated in place: a live feed appends the handful of rows that arrived since the
    /// last frame and drops the same number off the top.</summary>
    private readonly ObservableCollection<FeedRow> _rows = [];

    /// <summary>How far into <see cref="DamageFeed"/>'s sequence <see cref="_rows"/> has
    /// been filled, or -1 when the next render must rebuild from scratch (a filter moved,
    /// the mode flipped, the tab was brought forward).</summary>
    private long _cursor = -1;

    /// <summary>The list is showing the "nothing matching yet" placeholder, which the
    /// first real row has to clear.</summary>
    private bool _placeholder;

    /// <summary>Which side the row before this one took, for the chat layout's
    /// transition gap. Null at the top of a rebuild — the first row starts no
    /// conversation, so it gets no gap.</summary>
    private bool? _lastRight;

    /// <summary>Rows kept per pane. Deep enough to scroll back through a long fight;
    /// virtualisation means only the visible dozen ever become controls.</summary>
    private const int MaxRows = 2000;

    private static readonly Brush PillOnFg = FeedPalette.Frozen("#D9C46B");
    private static readonly Brush PillOffFg = FeedPalette.Frozen("#55616C");
    private static readonly Brush InputBrush = FeedPalette.Frozen("#DDE5EC");
    private static readonly Brush PillOnBg = Fill(0x2E);
    private static readonly Brush PillOffBg = Fill(0x10);
    private static readonly Brush PillOnBorder = Fill(0x55);
    private static readonly Brush PillOffBorder = Fill(0x1E);
    private static readonly Brush PopupBg = FeedPalette.Frozen("#F5141A20");
    private static readonly Brush RemoveFg = FeedPalette.Frozen("#8A97A3");

    private static Brush Fill(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    public string Title => Pane.Title is { Length: > 0 } t ? t
        : Key == "feed" ? "FEED" : "FEED " + Key[4..];

    public FeedView(MainWindow owner, LiteUiSettings ui, DamageFeed feed, FeedPane pane)
    {
        _owner = owner;
        _ui = ui;
        _feed = feed;
        Pane = pane;
        HostPane = pane;
        _palette = new FeedPalette(pane.Colors);

        _list = new ListBox
        {
            Margin = new Thickness(2, 2, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemContainerStyle = (Style)owner.FindResource("FeedItemStyle"),
            ItemTemplate = (DataTemplate)owner.FindResource("FeedRowTemplate"),
            ItemsSource = _rows,
        };
        // The dark slim scrollbar, and air between it and the text — the stock bar
        // was a bright light-theme control jammed against the rightmost characters.
        _list.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar),
            (Style)owner.FindResource("FeedScrollBarStyle"));
        _list.Padding = new Thickness(0, 0, 8, 0);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        // Item-unit scrolling is what makes "keep the reader where they are" exact: when
        // the oldest rows fall off the top, the offset is shifted back by the number of
        // rows removed, which is only meaningful if an offset counts rows.
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Item);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);

        // Kill summaries copy themselves on click, the way the FIGHTS popup's ⧉ does —
        // the summary IS the line worth pasting to the group. Preview, because a
        // ListBoxItem eats plain clicks for selection.
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject at) return;
            if (ItemsControl.ContainerFromElement(_list, at) is not ListBoxItem item
                || item.DataContext is not FeedRow { Copy: { Length: > 0 } copy }) return;
            e.Handled = true;
            try { Clipboard.SetText(copy); } catch { return; }
            StatusFlash?.Invoke("· copied ✓");
        };

        // One ROW per category, so "who" never wraps into the middle of "what": each
        // Group() call starts a fresh horizontal line under the previous one.
        _pillRows = new StackPanel();
        _pillPopup = new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Bottom,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = PopupBg,
                BorderBrush = PillOnBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Child = _pillRows,
            },
        };

        _modeButton = FlatButton("all", PillOffFg,
            "Show the RAW log — every line the game writes, in order, with no filtering "
            + "but your search chips. Click again for the filtered combat view.");
        _modeButton.Click += (_, _) =>
        {
            Pane.Filters.RawMode = !Pane.Filters.RawMode;
            _ui.Save();
            RefreshFilterRow();
            Invalidate();
            Render(active: true);
        };

        _pillButton = FlatButton("filters", PillOffFg,
            "Which rows this window shows — click for the whole set");
        _pillButton.Click += (_, _) =>
        {
            _pillPopup.PlacementTarget = _pillButton;
            _pillPopup.IsOpen = !_pillPopup.IsOpen;
        };

        _searchBox = new TextBox
        {
            MinWidth = 54,
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

        _filterRow = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };

        Body = new StackPanel();
        Body.Children.Add(_filterRow);
        Body.Children.Add(_list);

        BuildPills();
        RefreshFilterRow();
        ApplyInnerWidth(double.NaN);
    }

    public int RowsClamped() => Math.Clamp(HostPane.Rows, 4, 40);

    /// <summary>Re-read the pane's colours (the colour dialog just changed them).</summary>
    public void ApplyColors()
    {
        _palette = new FeedPalette(Pane.Colors);
        Invalidate();
    }

    /// <summary>Put every filter back to what a brand-new window would have.</summary>
    public void ResetFilters()
    {
        Pane.Filters = FeedPane.DefaultFilters();
        _ui.Save();
        RefreshPills();
        RefreshFilterRow();
        Invalidate();
        Render(active: true);
    }

    /// <summary>The section's width, from the ◢ grip (NaN = auto). The list takes the
    /// cap as a FIXED width, not a max — this window holds the size the user set and
    /// never breathes with its content.</summary>
    public void ApplyInnerWidth(double width)
    {
        var cap = double.IsNaN(width) ? 340 : Math.Max(150, width);
        _filterRow.MaxWidth = cap;
        _list.Width = cap;
    }

    /// <summary>Row text size, from the WINDOW this pane is drawn in (a per-window
    /// setting, like Rows). The template inherits the ListBox's FontSize.</summary>
    public double RowFont => Math.Clamp(HostPane.FontSize, 8, 24);

    /// <summary>The family last pushed onto the list, so a render compares a string
    /// instead of allocating a FontFamily per frame.</summary>
    private string _appliedFamily = "";

    /// <summary>Set by the hosting window: a transient note for its status slot
    /// ("· copied ✓") — the view has no status line of its own.</summary>
    internal Action<string>? StatusFlash;

    /// <summary>One row's height at the current font — the same +3 leading the original
    /// 11px/14px pairing had. The grip's row-drag and the viewport height both use it.</summary>
    public double RowHeight => Math.Round(RowFont + 3);

    /// <summary>Throw away what is drawn and rebuild from the buffer on the next render.
    /// Anything that changes which rows QUALIFY lands here — the incremental path only
    /// knows how to add what is new, not to re-judge what is already on screen.</summary>
    public void Invalidate() => _cursor = -1;

    /// <summary>Draw. <paramref name="active"/> is false for a tab sitting behind another
    /// one: it is not on screen, so it only keeps score of what it would have shown, for
    /// the dot on its tab.</summary>
    public void Render(bool active)
    {
        if (!active)
        {
            // Not visible, so never partially drawn: whatever is in _rows is stale the
            // moment this tab comes forward.
            Invalidate();
            if (_backgroundCursor == long.MaxValue)
            {
                // Just went to sleep: note where the buffer is now, so the dot counts what
                // happens NEXT rather than everything already scrolled past.
                _feed.Snapshot(Pane.Filters, 0, 0, out _backgroundCursor);
                return;
            }
            var missed = _feed.Snapshot(Pane.Filters, 200, _backgroundCursor, out _backgroundCursor);
            Unseen += missed.Count;
            // A background tab still rings the window's bell — the tags belong to the
            // WINDOW, and a match behind another tab is still a match.
            MaybeAlert(missed);
            return;
        }
        _backgroundCursor = long.MaxValue;   // re-primed when this tab goes back to sleep

        var f = Pane.Filters;
        if (_list.FontSize != RowFont) _list.FontSize = RowFont;
        var family = HostPane.FontFamily is { Length: > 0 } ff ? ff : "Consolas";
        if (_appliedFamily != family)
        {
            _appliedFamily = family;
            _list.FontFamily = new FontFamily(family);
        }
        _list.Height = RowsClamped() * RowHeight + 4;

        // The ScrollViewer only exists once the list has been templated, which is a
        // layout pass away from the first render — and the first render is the one that
        // fills the list. Without this the feed would open showing its OLDEST rows.
        if (_pendingBottom && Scroller() is { } late)
        {
            late.ScrollToBottom();
            _pendingBottom = false;
            Unseen = 0;
        }

        var rebuild = _cursor < 0;
        // RowOf runs in order and remembers the side as it goes, so a rebuild has to
        // forget the old tail first or the first fresh row inherits a stale neighbour.
        if (rebuild) _lastRight = null;
        var arrivals = _feed.Snapshot(f, MaxRows, rebuild ? 0 : _cursor, out var cursor);
        // Fresh arrivals only: a rebuild replays what is already history, and history
        // must not ring the bell (the startup replay is one giant rebuild).
        if (!rebuild) MaybeAlert(arrivals);
        var fresh = arrivals.Select(RowOf).ToList();
        _cursor = cursor;
        if (!rebuild && fresh.Count == 0)
        {
            // Still worth asking where the reader is: scrolling back to the bottom clears
            // the "new below" count even on a quiet frame.
            if (AtBottom()) Unseen = 0;
            return;
        }

        var scroller = Scroller();
        var atBottom = AtBottom();

        // A rebuild starts empty; so does a list showing only the placeholder, which is
        // not a row anyone wants pushed up the page.
        if (rebuild || _placeholder)
        {
            _rows.Clear();
            _placeholder = false;
            atBottom = true;   // a fresh list belongs at its newest end
        }
        foreach (var row in fresh) _rows.Add(row);

        var dropped = 0;
        while (_rows.Count > MaxRows) { _rows.RemoveAt(0); dropped++; }

        if (atBottom)
        {
            if (scroller is null) _pendingBottom = true;
            else scroller.ScrollToBottom();
            Unseen = 0;
        }
        else
        {
            // The reader is somewhere above, so the view must not move under them.
            // Appending below does not disturb the offset; rows falling off the TOP do,
            // by exactly the number removed.
            if (dropped > 0 && scroller is not null)
                scroller.ScrollToVerticalOffset(Math.Max(0, scroller.VerticalOffset - dropped));
            Unseen += fresh.Count;
        }

        // An empty match set renders as one dim row INSIDE the fixed-height list —
        // swapping the list for a message would change the window's height.
        if (_rows.Count == 0)
        {
            _rows.Add(new FeedRow("(nothing matching yet)", _palette.Dim));
            _placeholder = true;
        }
    }

    /// <summary>A scroll-to-bottom that could not happen yet because the list had not
    /// been templated. Retried on the next frame, so it costs at most 100 ms.</summary>
    private bool _pendingBottom = true;

    /// <summary>Where a background tab's score-keeping got to. MaxValue means "start from
    /// the newest" — a tab that has just been put to sleep should count what happens
    /// NEXT, not everything already in the buffer.</summary>
    private long _backgroundCursor = long.MaxValue;

    /// <summary>Is the reader parked at the newest end? A list too short to scroll always
    /// is, and so is one that has not been templated yet.</summary>
    private bool AtBottom()
    {
        if (Scroller() is not { } sv) return true;
        return sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 0.5;
    }

    /// <summary>The ListBox's internal ScrollViewer, once templated (null before the
    /// first layout pass).</summary>
    private ScrollViewer? Scroller()
    {
        if (VisualTreeHelper.GetChildrenCount(_list) == 0) return null;
        return VisualTreeHelper.GetChild(_list, 0) is Border b ? b.Child as ScrollViewer : null;
    }

    // ---- rows ----

    private FeedRow RowOf(FeedEntry e)
    {
        // Incoming means "done TO you" — the flag the filters already use for the `in`
        // pill, so the two never disagree about which side a row is on.
        var right = Pane.SplitSides && e.Incoming;
        var gap = Pane.SplitSides && _lastRight is { } previous && previous != right;
        _lastRight = right;

        // Right-aligned rows carry the clock as a SUFFIX: their ends sit flush against
        // the right edge, so trailing timestamps line up in a column the way leading
        // ones do on the left side.
        var spans = new List<FeedSpan>(4);
        if (!right) spans.Add(new FeedSpan($"{e.Time:HH:mm:ss}  ", _palette.Dim));
        // The line as the game wrote it, colour-coded by what the parser made of it. The
        // reformatted version is only a fallback for entries with no raw text (captured
        // before 1.68.1): a filtered feed is easier to read when its rows say exactly
        // what the log says.
        var body = e.Raw is { Length: > 0 } raw ? raw : Fallback(e);
        AddAccented(spans, body, e.Ability, BrushFor(e), bold: e.Kind == FeedKind.Xp,
            accent: e.Kind == FeedKind.Faction ? _palette.Faction : null);
        if (right) spans.Add(new FeedSpan($"  {e.Time:HH:mm:ss}", _palette.Dim));
        var summary = e.Kind == FeedKind.Summary;
        var alerted = !summary && AlertHit(body) is not null;
        return new FeedRow(spans)
        {
            Right = right,
            Gap = gap,
            // Frames: the kill summaries' own colour, or the Alert colour when one of
            // the window's watch-tags matched. Framed rows are click-to-copy — the
            // summary as a chat-safe flat line, an alerted line verbatim.
            Frame = summary ? _palette.Summary : alerted ? _palette.Alert : null,
            Copy = summary ? ChatSafe(body) : alerted ? body : null,
        };
    }

    /// <summary>The first of THIS TAB's enabled rules whose words appear in the line, or
    /// null. Per pane, not per host: each tab carries its own alerts (1.80). Single-char
    /// tags are ignored — "a" would frame the entire feed.</summary>
    private FeedAlertRule? AlertHit(string body)
    {
        if (Pane.Alerts is not { Count: > 0 } rules) return null;
        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            foreach (var tag in rule.Tags)
                if (tag.Length > 1 && body.Contains(tag, StringComparison.OrdinalIgnoreCase))
                    return rule;
        }
        return null;
    }

    /// <summary>Ring this tab's alerts for fresh arrivals that match one.
    ///
    /// The entries handed in have ALREADY passed this pane's filters — they come out of
    /// <see cref="DamageFeed.Snapshot"/>, which applies them — so an alert can only fire
    /// on a line the tab is actually showing. That is the whole reason the rules moved
    /// onto the pane: window-level tags were matched against each tab's own filtered
    /// stream, so which tab happened to be in front decided whether you heard anything.
    ///
    /// Each rule fires at most once per batch and carries its own sound; the cooldown
    /// lives in AudioCues, keyed per tab AND per rule, so two rules never mute each
    /// other and a burst on one of them is still one sound.</summary>
    private void MaybeAlert(List<FeedEntry> entries)
    {
        if (Pane.Alerts is not { Count: > 0 }) return;
        HashSet<FeedAlertRule>? fired = null;
        foreach (var e in entries)
        {
            if (e.Kind == FeedKind.Summary || e.Raw is not { Length: > 0 } raw) continue;
            if (AlertHit(raw) is not { } rule) continue;
            fired ??= [];
            if (fired.Add(rule)) _owner.FeedAlert(Pane, rule);
        }
    }

    /// <summary>A summary row as one flat line for EQ's chat box — the same rules as
    /// the popup ⧉: single line, typographic glyphs to ASCII (the game's font mangles
    /// them), and the feed's own ⤷ marker dropped.</summary>
    internal static string ChatSafe(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text
            .Replace("⤷", "").Replace(" · ", " - ").Replace("·", "-")
            .Replace("×", "x").Replace("—", "-"), " {2,}", " ").Trim();

    /// <summary>Add the line, with the ability/spell/item it names picked out in the
    /// accent colour. Matching on the text the log actually printed is what keeps the
    /// highlight honest — no accent is shown when the line does not literally contain the
    /// name (a third-party hit with no skill, say).</summary>
    private void AddAccented(List<FeedSpan> spans, string body, string ability,
        Brush baseBrush, bool bold = false, Brush? accent = null)
    {
        accent ??= _palette.Ability;
        var at = ability.Length >= 3
            ? body.IndexOf(ability, StringComparison.OrdinalIgnoreCase)
            : -1;
        if (at < 0)
        {
            spans.Add(new FeedSpan(body, baseBrush, bold));
            return;
        }
        if (at > 0) spans.Add(new FeedSpan(body[..at], baseBrush, bold));
        spans.Add(new FeedSpan(body.Substring(at, ability.Length), accent, bold));
        var rest = at + ability.Length;
        if (rest < body.Length) spans.Add(new FeedSpan(body[rest..], baseBrush, bold));
    }

    /// <summary>Row colour by what the line turned out to be — the one piece of reading
    /// help a verbatim log line can't give you itself. Spells, DoTs and procs share a gold
    /// base so a caster's window reads as a column of casts with the spell names picked
    /// out; melee keeps the who-colour it always had.</summary>
    private Brush BrushFor(FeedEntry e) => e.Kind switch
    {
        FeedKind.Summary => _palette.Summary,
        FeedKind.Cast => _palette.Cast,
        FeedKind.Mez => _palette.Mez,
        FeedKind.MezBreak => _palette.MezBreak,
        FeedKind.Consider => _palette.Consider,
        // The log kinds wear the GAME's chat colours (user screenshot): loot blue,
        // money green, stances blue, xp bold yellow. Zone and chat stay in the dim
        // context colour — the game gives every channel its own and the feed cannot
        // know which channel a line came from. Faction rows keep the context colour
        // too: their red lives on the NAME, painted by the accent in RowOf.
        FeedKind.Attack => _palette.Attack,
        FeedKind.Xp => _palette.Xp,
        FeedKind.Loot => _palette.Loot,
        FeedKind.Money => _palette.Money,
        FeedKind.Zone or FeedKind.Chat or FeedKind.Other
            or FeedKind.Faction => _palette.Other,
        FeedKind.Kill => _palette.Kill,
        FeedKind.Heal => _palette.Heal,
        FeedKind.Taken => _palette.Incoming,
        FeedKind.Miss or FeedKind.Resist or FeedKind.Fizzle => _palette.Dim,
        FeedKind.Spell or FeedKind.Dot or FeedKind.Aux => e.Crit ? _palette.Crit : _palette.Spell,
        _ => e.Crit ? _palette.Crit : e.Who switch
        {
            FeedWho.Pet => _palette.Pet,
            FeedWho.Group => _palette.Group,
            _ => _palette.You,
        },
    };

    /// <summary>What a row said before 1.68.1 started keeping the raw line.</summary>
    private static string Fallback(FeedEntry e)
    {
        var tag = e.Note is { Length: > 0 } n ? $" ({n})" : e.Crit ? " (Crit)" : "";
        var actor = e.Who == FeedWho.You ? "" : $"{e.Actor}: ";
        return e.Kind switch
        {
            FeedKind.Melee or FeedKind.Spell or FeedKind.Dot or FeedKind.Aux =>
                $"{actor}{e.Ability} → {e.Target}  {e.Amount:N0}{tag}",
            FeedKind.Taken =>
                $"{e.Actor}{(e.Ability.Length > 0 ? $" {e.Ability}" : "")} → you  {e.Amount:N0}",
            FeedKind.Heal => e.Incoming
                ? $"{e.Actor} heals you  +{e.Amount:N0}"
                : $"{e.Ability} → {e.Target}  +{e.Amount:N0}",
            FeedKind.Miss => e.Incoming ? "missed you" : "you miss",
            FeedKind.Kill => $"{e.Actor} slew {e.Target}",
            FeedKind.Resist => $"{(e.Ability.Length > 0 ? e.Ability : "spell")} resisted",
            _ => $"{(e.Ability.Length > 0 ? e.Ability : "spell")} fizzled",
        };
    }

    // ---- the filter popup ----

    /// <summary>The filter pills. Each owns its refresh closure; clicking saves and
    /// re-renders THIS pane only, so two feed windows never fight over a filter.</summary>
    private void BuildPills()
    {
        var f = Pane.Filters;
        void Group(string label)
        {
            _pillCurrentRow = new WrapPanel
            {
                MaxWidth = 460,
                Margin = new Thickness(0, _pillRows.Children.Count == 0 ? 0 : 3, 0, 0),
            };
            _pillCurrentRow.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 9,
                Width = 68,   // aligned label column, so the rows read as a table
                Foreground = PillOffFg,
                Margin = new Thickness(1, 3, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            _pillRows.Children.Add(_pillCurrentRow);
        }

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
                RefreshFilterRow();
                Invalidate();
                Render(active: true);
            };
            Refresh();
            _pillRefreshers.Add(Refresh);
            (_pillCurrentRow ?? throw new InvalidOperationException("pill before Group"))
                .Children.Add(pill);
        }

        Group("who");
        Pill("you", "Your own damage", () => f.You, () => f.You = !f.You);
        Pill("pet", "Your pet's damage", () => f.Pet, () => f.Pet = !f.Pet);
        Pill("grp", "Other players near you, from your log", () => f.Group, () => f.Group = !f.Group);
        Pill("in", "Damage you take", () => f.Incoming, () => f.Incoming = !f.Incoming);

        Group("what");
        Pill("melee", "Melee hits", () => f.Melee, () => f.Melee = !f.Melee);
        Pill("spell", "Direct spell damage", () => f.Spells, () => f.Spells = !f.Spells);
        Pill("dot", "Damage-over-time ticks", () => f.Dots, () => f.Dots = !f.Dots);
        Pill("ds", "Damage shields / automatic damage — yours on them AND theirs on you",
            () => f.DamageShields, () => f.DamageShields = !f.DamageShields);
        Pill("heal", "Heals, cast and received", () => f.Heals, () => f.Heals = !f.Heals);
        Pill("miss", "Misses, dodges, parries", () => f.Misses, () => f.Misses = !f.Misses);
        Pill("kill", "Killing blows", () => f.Kills, () => f.Kills = !f.Kills);
        Pill("r/f", "Resists and fizzles", () => f.ResistsFizzles, () => f.ResistsFizzles = !f.ResistsFizzles);
        Pill("cast", "Casting: begin casting, interrupts, regaining concentration, "
            + "buffs wearing off", () => f.Casts, () => f.Casts = !f.Casts);
        Pill("mez", "Mez landings (\"has been mesmerized\") and breaks "
            + "(\"has been awakened by\")", () => f.Mez, () => f.Mez = !f.Mez);

        Group("log");
        Pill("atk", "Auto attack is on/off, stance and invocation changes",
            () => f.Attack, () => f.Attack = !f.Attack);
        Pill("loot", "Loot, corpse coin, vendor sales, crafting",
            () => f.Loot, () => f.Loot = !f.Loot);
        Pill("xp", "Experience, AA, levels, skill-ups",
            () => f.Xp, () => f.Xp = !f.Xp);
        Pill("fact", "Faction standing changes — the faction's name in the game's red",
            () => f.Faction, () => f.Faction = !f.Faction);
        Pill("con", "NPC consider lines — \"… judges you amiable … (Lvl: 25)\", "
            + "with the NPC's name picked out", () => f.Consider, () => f.Consider = !f.Consider);
        Pill("zone", "Zone changes and /loc lines", () => f.Zone, () => f.Zone = !f.Zone);
        Pill("chat", "Tells, says, shouts, channel chat, auctions",
            () => f.Chat, () => f.Chat = !f.Chat);
        Pill("other", "Every line still left — emotes, mob flavor, system messages. "
            + "Nothing is hidden from the feed; this is where anything without a bucket "
            + "of its own lives.",
            () => f.Other, () => f.Other = !f.Other);

        Group("kill summary");
        Pill("sum·you", "After a mob dies, a line with YOUR damage and dps against it",
            () => f.SummaryYou, () => f.SummaryYou = !f.SummaryYou);
        Pill("sum·pet", "The same line for your pet", () => f.SummaryPet, () => f.SummaryPet = !f.SummaryPet);
        Pill("sum·grp", "The same line for everyone else who hit it",
            () => f.SummaryGroup, () => f.SummaryGroup = !f.SummaryGroup);

        Group("only");
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

    private void RefreshPills()
    {
        foreach (var refresh in _pillRefreshers) refresh();
    }

    /// <summary>How many filters are narrowing this window, for the button's badge. The
    /// count is of switches turned AWAY from the default, so a window showing everything
    /// reads "filters" and a tuned one reads "filters · 4".</summary>
    private int NarrowingCount()
    {
        var f = Pane.Filters;
        var d = FeedPane.DefaultFilters();
        var n = 0;
        void Cmp(bool a, bool b) { if (a != b) n++; }
        Cmp(f.You, d.You); Cmp(f.Pet, d.Pet); Cmp(f.Group, d.Group); Cmp(f.Incoming, d.Incoming);
        Cmp(f.Melee, d.Melee); Cmp(f.Spells, d.Spells); Cmp(f.Dots, d.Dots);
        Cmp(f.DamageShields, d.DamageShields); Cmp(f.Heals, d.Heals); Cmp(f.Misses, d.Misses);
        Cmp(f.Kills, d.Kills); Cmp(f.ResistsFizzles, d.ResistsFizzles);
        Cmp(f.Casts, d.Casts); Cmp(f.Mez, d.Mez); Cmp(f.Other, d.Other);
        Cmp(f.Attack, d.Attack); Cmp(f.Loot, d.Loot); Cmp(f.Xp, d.Xp);
        Cmp(f.Faction, d.Faction); Cmp(f.Consider, d.Consider);
        Cmp(f.Zone, d.Zone); Cmp(f.Chat, d.Chat);
        Cmp(f.SummaryYou, d.SummaryYou); Cmp(f.SummaryPet, d.SummaryPet);
        Cmp(f.SummaryGroup, d.SummaryGroup);
        Cmp(f.CritsOnly, d.CritsOnly); Cmp(f.OnlySlays, d.OnlySlays);
        Cmp(f.OnlyRipostes, d.OnlyRipostes); Cmp(f.OnlyCrippling, d.OnlyCrippling);
        if (f.MinDamage != d.MinDamage) n++;
        if (f.MeleeType != d.MeleeType) n++;
        return n;
    }

    // ---- the single filter line: [all] [filters ▾] [chip ✕]… [box] [+] ----

    private void CommitSearch()
    {
        var term = _searchBox.Text.Trim();
        _searchBox.Clear();
        if (term.Length == 0) return;
        var f = Pane.Filters;
        if (!f.SearchTerms.Any(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase)))
            f.SearchTerms.Add(term);
        _ui.Save();
        RefreshFilterRow();
        Invalidate();
        Render(active: true);
    }

    /// <summary>Rebuild the one filter line. Chips are cheap and a full rebuild keeps one
    /// source of truth (the pane's list). The text box is a persistent instance so
    /// half-typed input survives a chip add/remove.
    ///
    /// ONE line, not four: the mode button and the pills used to sit on rows of their own,
    /// which cost more of the window than the log did. The pills moved into a popup that
    /// stays open while you click through it, and "all" (the raw log) is a MODE — when it
    /// is on, the pills do not apply and their button is not shown, so the line only ever
    /// offers the filters that are live.</summary>
    private void RefreshFilterRow()
    {
        var f = Pane.Filters;
        _filterRow.Children.Clear();

        _modeButton.Foreground = f.RawMode ? PillOnFg : PillOffFg;
        _modeButton.Background = f.RawMode ? PillOnBg : PillOffBg;
        _modeButton.BorderBrush = f.RawMode ? PillOnBorder : PillOffBorder;
        _filterRow.Children.Add(_modeButton);

        if (f.RawMode)
        {
            _pillPopup.IsOpen = false;
        }
        else
        {
            var narrowing = NarrowingCount();
            _pillButton.Content = narrowing == 0 ? "filters ▾" : $"filters · {narrowing} ▾";
            _pillButton.Foreground = narrowing == 0 ? PillOffFg : PillOnFg;
            _pillButton.BorderBrush = narrowing == 0 ? PillOffBorder : PillOnBorder;
            _filterRow.Children.Add(_pillButton);
        }

        foreach (var term in f.SearchTerms.ToList())
        {
            var text = new TextBlock
            {
                Text = term,
                FontSize = 10,
                Foreground = PillOnFg,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var remove = FlatButton("✕", RemoveFg, $"Stop filtering by {term}");
            remove.Margin = new Thickness(3, 0, 0, 0);
            remove.Padding = new Thickness(2, 0, 2, 1);
            remove.BorderThickness = new Thickness(0);
            remove.Background = Brushes.Transparent;
            remove.Click += (_, _) =>
            {
                f.SearchTerms.RemoveAll(t => string.Equals(t, term, StringComparison.OrdinalIgnoreCase));
                _ui.Save();
                RefreshFilterRow();
                Invalidate();
                Render(active: true);
            };
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(text);
            body.Children.Add(remove);
            _filterRow.Children.Add(new Border
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

        _filterRow.Children.Add(_searchBox);
        var plus = FlatButton("+", PillOnFg, "Add the typed word as a chip (same as Enter)");
        plus.Click += (_, _) => CommitSearch();
        _filterRow.Children.Add(plus);
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

    /// <summary>Formatted for the heading: what this pane is showing right now.</summary>
    public string StatusSuffix => Pane.Filters.RawMode
        ? Unseen > 0 ? $"raw log · {Unseen} new below" : "raw log · live"
        : Unseen > 0 ? $"{Unseen} new below" : "live";
}
