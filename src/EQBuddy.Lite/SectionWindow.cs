using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQBuddy.Lite;

/// <summary>Which edge of its host a docked window hangs off. Below is the stack every
/// section starts in; the sides are how a second column exists — two windows dropped
/// beside each other share a top edge exactly rather than being nudged towards it.</summary>
public enum DockSide { Below, Right, Left }

/// <summary>
/// A torn-off panel section as its own floating window. The section's element is
/// re-parented here verbatim (its x:Name fields in MainWindow stay live, so Tick keeps
/// updating it untouched). Dragging the window near another EQdps window's bottom edge
/// magnetises it — snapped windows follow their host when it moves. ✕ returns the
/// section to the main panel.
/// </summary>
public sealed class SectionWindow : Window
{
    public string SectionKey { get; }

    /// <summary>The window this one is magnetised under, or null when free-floating.
    /// Maintained by MainWindow.SnapWindow; followers are repositioned whenever the
    /// host moves or resizes.</summary>
    public Window? DockHost { get; set; }

    /// <summary>Which side of <see cref="DockHost"/> this window sits on. Below (the
    /// stack) unless it was dropped against a host's left or right edge.</summary>
    public DockSide DockSide { get; set; } = DockSide.Below;

    private readonly MainWindow _owner;
    private readonly Grid _grid;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Button _close;
    private Action? _closeOverride;

    private const string DockBackTip = "Hook back under the main stack";

    /// <summary>What this window's ✕ does. Null (the default) re-docks it under the
    /// main stack. Spawned FEED windows set a real close here: re-docking a window the
    /// user asked to close looks exactly like the close silently failing.</summary>
    public Action? CloseOverride
    {
        get => _closeOverride;
        set
        {
            _closeOverride = value;
            _close.ToolTip = value is null ? DockBackTip : "Close this window for good";
        }
    }

    public SectionWindow(string sectionKey, FrameworkElement content, MainWindow owner)
    {
        SectionKey = sectionKey;
        _owner = owner;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        // A Button, not a bare glyph: a 10 px ✕ only hit-tests over its own strokes, so
        // most of what looks like the control isn't clickable. Templated flat and
        // transparent, it looks the same but the whole padded box is the target.
        _close = new Button
        {
            Content = "✕",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA3)),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            FontSize = 10,
            Focusable = false,
            Cursor = Cursors.Hand,
            Padding = new Thickness(5, 1, 4, 2),
            Margin = new Thickness(0, -3, -6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = DockBackTip,
            Template = (ControlTemplate)owner.FindResource("FlatButtonTemplate"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_close, "SectionClose");
        _close.Click += (_, _) =>
        {
            // A section the app ships can only be re-docked (hiding it is the ⚙'s job);
            // a window the USER created is one they expect ✕ to destroy.
            if (CloseOverride is { } close) close();
            else _owner.DockToStack(this);
        };

        // Resize grip: horizontal drag sets this section's width; on the FEED the
        // vertical half adjusts how many rows it shows. Double-click resets to auto.
        // Screen coordinates throughout — the window itself moves and resizes under
        // the drag, so window-relative positions would feed back into themselves.
        var grip = new TextBlock
        {
            Text = "◢",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x61, 0x6C)),
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(6, 2, -6, -8),
            ToolTip = "Drag to resize · double-click for auto width" +
                      (sectionKey.StartsWith("feed", StringComparison.Ordinal)
                          ? " · up/down changes rows shown" : ""),
        };
        var resizing = false;
        var resizeStart = default(Point);
        grip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;   // keep the window's own handler from starting a drag-move
            if (e.ClickCount == 2) { _owner.ResetSectionSize(this); return; }
            resizing = true;
            resizeStart = PointToScreen(e.GetPosition(this));
            _owner.BeginSectionResize(this);
            grip.CaptureMouse();
        };
        grip.MouseMove += (_, e) =>
        {
            if (!resizing || !grip.IsMouseCaptured) return;
            var at = PointToScreen(e.GetPosition(this));
            _owner.SectionResizeDelta(this, at.X - resizeStart.X, at.Y - resizeStart.Y);
        };
        grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!resizing) return;
            e.Handled = true;
            resizing = false;
            grip.ReleaseMouseCapture();
            _owner.EndSectionResize();
        };

        _grid = new Grid();
        _grid.Children.Add(content);
        _grid.Children.Add(_close);
        _grid.Children.Add(grip);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 6, 14, 12),
            MinWidth = 200,
            Child = _grid,
            LayoutTransform = _scale,
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            BeginUserDrag();
        };
        LocationChanged += (_, _) => { _owner.RepositionFollowers(this); _owner.RefreshPopupPosition(); };
        SizeChanged += (_, _) =>
        {
            // A left-docked window is positioned by its RIGHT edge, so its own width
            // change moves it; every other dock only moves what hangs off it.
            _owner.ReseatSelf(this);
            _owner.RepositionFollowers(this);
            _owner.RefreshPopupPosition();
        };
    }

    /// <summary>A user drag un-hooks first (you are pulling it away), then re-snaps on
    /// release wherever it landed.</summary>
    public void BeginUserDrag()
    {
        DockHost = null;
        DockSide = DockSide.Below;
        try { DragMove(); } catch (InvalidOperationException) { /* button already up */ }
        _owner.SnapWindow(this);
    }

    /// <summary>Deferred variant for the tear-off gesture: the window has just been
    /// shown and the mouse button is still down.</summary>
    public void BeginDragDeferred() =>
        Dispatcher.BeginInvoke(BeginUserDrag, DispatcherPriority.Input);

    public void SetScale(double scale) => _scale.ScaleX = _scale.ScaleY = scale;

}
