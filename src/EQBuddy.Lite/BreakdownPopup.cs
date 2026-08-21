using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>Satellite panel showing one group member's damage breakdown, parked to the
/// right of the main window. One at a time; clicking the same name again closes it.
/// Built in code, same visual language as the main panel.</summary>
public sealed class BreakdownPopup : Window
{
    public string MemberName { get; }

    private readonly TextBlock _header;
    private readonly TextBlock _rows;
    private readonly TextBlock _motesHeader;
    private readonly TextBlock _motesRows;
    private bool _motesExpanded;

    public BreakdownPopup(string memberName, Window owner)
    {
        MemberName = memberName;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        _header = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE5, 0xEC)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        };
        var close = new TextBlock
        {
            Text = "✕",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA3)),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(12, 1, 0, 0),
        };
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };

        var head = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(_header);

        _rows = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF5)),
            Margin = new Thickness(0, 6, 0, 0),
        };

        _motesHeader = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xC4, 0x6B)),
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 7, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Show/hide mote tiers",
        };
        _motesHeader.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _motesExpanded = !_motesExpanded;
            RefreshMotesVisibility();
        };
        _motesRows = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xC4, 0x6B)),
            Margin = new Thickness(14, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var stack = new StackPanel { MinWidth = 170 };
        stack.Children.Add(head);
        stack.Children.Add(_rows);
        stack.Children.Add(_motesHeader);
        stack.Children.Add(_motesRows);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x14, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 10),
            Child = stack,
        };
    }

    private string _motesSummary = "";
    private string _motesDetail = "";

    /// <summary>motesSummary empty hides the motes section; motesDetail empty makes the
    /// summary line plain text (nothing to expand).</summary>
    public void Update(string header, string rows, string motesSummary, string motesDetail)
    {
        _header.Text = header;
        _rows.Text = rows;
        _motesSummary = motesSummary;
        _motesDetail = motesDetail;
        RefreshMotesVisibility();
    }

    private void RefreshMotesVisibility()
    {
        if (_motesSummary.Length == 0)
        {
            _motesHeader.Visibility = Visibility.Collapsed;
            _motesRows.Visibility = Visibility.Collapsed;
            return;
        }
        var expandable = _motesDetail.Length > 0;
        _motesHeader.Text = expandable
            ? (_motesExpanded ? "▾ " : "▸ ") + _motesSummary
            : _motesSummary;
        _motesHeader.Visibility = Visibility.Visible;
        _motesRows.Text = _motesDetail;
        _motesRows.Visibility = expandable && _motesExpanded
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
