using System.Windows;
using System.Windows.Controls;

namespace EQBuddy.Lite;

/// <summary>Group-sync setup: pick a code, share it with your group. Built in code —
/// one small dialog doesn't earn a XAML file. Clearing the code turns sync off.</summary>
public sealed class SyncDialog : Window
{
    private readonly TextBox _code;
    private readonly TextBox _relay;
    private readonly TextBox _viewer;

    /// <summary>The address needed to actually SEE the board in a browser — relay root
    /// plus /view/CODE — kept current as either field changes, selectable for copying.</summary>
    private void UpdateViewerUrl()
    {
        var relay = _relay.Text.Trim().TrimEnd('/');
        var code = _code.Text.Trim().ToUpperInvariant();
        _viewer.Text = relay.Length > 0 && code.Length > 0
            ? $"{relay}/view/{code}"
            : "(enter a group code above)";
    }

    public string GroupCode => _code.Text.Trim().ToUpperInvariant();
    public string RelayUrl => _relay.Text.Trim();

    public SyncDialog(string currentCode, string currentRelay)
    {
        Title = "Group sync";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Everyone in your group runs EQBuddy Lite and enters the same " +
                   "group code — each app then shares its own character name and DPS " +
                   "with the group (and nothing else). Leave the code empty to turn " +
                   "sync off; the board falls back to reading your own log.",
        });

        panel.Children.Add(new TextBlock { Text = "Group code (letters/numbers, 3–16):" });
        _code = new TextBox { Text = currentCode, Margin = new Thickness(0, 2, 0, 10), MaxLength = 16 };
        panel.Children.Add(_code);

        panel.Children.Add(new TextBlock { Text = "Relay server:" });
        _relay = new TextBox { Text = currentRelay, Margin = new Thickness(0, 2, 0, 10) };
        panel.Children.Add(_relay);

        panel.Children.Add(new TextBlock { Text = "Watch in any browser (share this link):" });
        _viewer = new TextBox
        {
            IsReadOnly = true,
            Margin = new Thickness(0, 2, 0, 12),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontStyle = FontStyles.Italic,
        };
        panel.Children.Add(_viewer);
        _code.TextChanged += (_, _) => UpdateViewerUrl();
        _relay.TextChanged += (_, _) => UpdateViewerUrl();
        UpdateViewerUrl();

        var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        _code.Focus();
        _code.CaretIndex = _code.Text.Length;
    }
}
