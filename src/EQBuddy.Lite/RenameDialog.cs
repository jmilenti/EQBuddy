using System.Windows;
using System.Windows.Controls;

namespace EQBuddy.Lite;

/// <summary>A one-line rename prompt (feed tabs). Built in code like the other small
/// dialogs; Enter accepts, Escape cancels, empty means "back to the default name".</summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    public string Value => _box.Text;

    public RenameDialog(string current)
    {
        Title = "Rename";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MinWidth = 260 };
        panel.Children.Add(new TextBlock
        {
            Text = "Name for this feed tab (empty = back to the default):",
            Margin = new Thickness(0, 0, 0, 6),
        });
        _box = new TextBox { Text = current, MaxLength = 24 };
        _box.SelectAll();
        panel.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }
}
