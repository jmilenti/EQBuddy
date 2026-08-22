using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>Satellite panel showing one group member's damage breakdown, parked to the
/// right of the main window. One at a time; clicking the same name again closes it.
/// Built in code, same visual language as the main panel. Motes deliberately aren't
/// here — the whole group's hauls sit together in the MOTES section instead.</summary>
public sealed class BreakdownPopup : Window
{
    public string MemberName { get; }

    private readonly TextBlock _header;
    private readonly TextBlock _rows;

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
            Margin = new Thickness(8, 1, 0, 0),
        };
        close.MouseLeftButtonDown += (_, e) => { e.Handled = true; Close(); };

        // Copy the whole breakdown as plain text — for pasting to friends who aren't
        // running the app. The tick is the only feedback a clipboard needs.
        var copy = new TextBlock
        {
            Text = "⧉",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x97, 0xA3)),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(12, 1, 0, 0),
            ToolTip = "Copy this breakdown to the clipboard",
        };
        copy.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            try
            {
                // _rows is assigned later in this constructor; the handler can only
                // run once the popup exists, so the suppression is truthful.
                Clipboard.SetText($"{_header.Text}\n{_rows!.Text}");
                copy.Text = "✓";
                var revert = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.2),
                };
                revert.Tick += (_, _) => { copy.Text = "⧉"; revert.Stop(); };
                revert.Start();
            }
            catch (Exception ex)
            {
                // The clipboard is a shared resource another app can hold open.
                EQBuddy.Core.CoreLog.Error(ex);
            }
        };

        var head = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(copy);
        head.Children.Add(_header);

        _rows = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xE3, 0xF5)),
            Margin = new Thickness(0, 6, 0, 0),
        };

        var stack = new StackPanel { MinWidth = 170 };
        stack.Children.Add(head);
        stack.Children.Add(_rows);

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

    public void Update(string header, string rows)
    {
        _header.Text = header;
        _rows.Text = rows;
    }
}
