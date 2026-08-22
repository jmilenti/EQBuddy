using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQBuddy.Lite;

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

    private readonly MainWindow _owner;
    private readonly Grid _grid;
    private readonly ScaleTransform _scale = new(1, 1);

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

        var close = new TextBlock
        {
            Text = "✕",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA3)),
            FontSize = 10,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Return to the main panel",
        };
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; _owner.Reattach(SectionKey); };

        _grid = new Grid();
        _grid.Children.Add(content);
        _grid.Children.Add(close);

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
        LocationChanged += (_, _) => _owner.RepositionFollowers(this);
        SizeChanged += (_, _) => _owner.RepositionFollowers(this);
    }

    /// <summary>A user drag un-hooks first (you are pulling it away), then re-snaps on
    /// release wherever it landed.</summary>
    public void BeginUserDrag()
    {
        DockHost = null;
        try { DragMove(); } catch (InvalidOperationException) { /* button already up */ }
        _owner.SnapWindow(this);
    }

    /// <summary>Deferred variant for the tear-off gesture: the window has just been
    /// shown and the mouse button is still down.</summary>
    public void BeginDragDeferred() =>
        Dispatcher.BeginInvoke(BeginUserDrag, DispatcherPriority.Input);

    public void SetScale(double scale) => _scale.ScaleX = _scale.ScaleY = scale;

    /// <summary>Hand the section element back so it can rejoin the main panel.</summary>
    public FrameworkElement ReleaseContent()
    {
        var content = (FrameworkElement)_grid.Children[0];
        _grid.Children.Clear();
        return content;
    }
}
