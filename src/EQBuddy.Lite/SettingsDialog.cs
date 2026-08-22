using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EQBuddy.Lite;

/// <summary>The ⚙ dialog: which features read from group sync and which stay on your
/// own log. Built in code like SyncDialog — one small dialog doesn't earn a XAML file.
/// These are viewing choices only: while a group code is set, sync keeps publishing
/// YOUR numbers to the group either way.</summary>
public sealed class SettingsDialog : Window
{
    private readonly CheckBox _groupSync;
    private readonly CheckBox _groupMotes;
    private readonly Dictionary<string, CheckBox> _sections = new();

    public bool GroupBoardUseSync => _groupSync.IsChecked == true;
    public bool ShowGroupMotes => _groupMotes.IsChecked == true;

    /// <summary>Sections the user UNticked — i.e. wants gone from the UI.</summary>
    public List<string> HiddenSections =>
        _sections.Where(kv => kv.Value.IsChecked != true).Select(kv => kv.Key).ToList();

    public SettingsDialog(bool groupBoardUseSync, bool showGroupMotes,
        IReadOnlyList<string> sectionKeys, IReadOnlyList<string> hiddenSections)
    {
        Title = "EQdps settings";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Choose what each feature reads from. Group sync gives exact numbers " +
                   "but needs everyone on the same group code; your own log works alone " +
                   "but only sees what happens near you (~ rows, approximate). While a " +
                   "code is set your numbers are shared either way — these only change " +
                   "what YOU see.",
        });

        _groupSync = new CheckBox
        {
            IsChecked = groupBoardUseSync,
            Margin = new Thickness(0, 0, 0, 8),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "GROUP board uses group sync when available\n" +
                       "(off: the board always shows your own log's ~ rows)",
            },
        };
        panel.Children.Add(_groupSync);

        _groupMotes = new CheckBox
        {
            IsChecked = showGroupMotes,
            Margin = new Thickness(0, 0, 0, 12),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Show group members' motes in the MOTES section\n" +
                       "(sync only — your log never sees anyone else's loot)",
            },
        };
        panel.Children.Add(_groupMotes);

        panel.Children.Add(new TextBlock
        {
            Text = "Show sections:",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            Text = "Unticked sections disappear from the stack entirely (the windows " +
                   "above and below close up). Tick again to bring one back — it " +
                   "re-hooks under the bottom of the stack.",
        });
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var key in sectionKeys)
        {
            var box = new CheckBox
            {
                IsChecked = !hiddenSections.Contains(key),
                Margin = new Thickness(0, 0, 8, 4),
                Content = key.ToUpperInvariant(),
            };
            _sections[key] = box;
            grid.Children.Add(box);
        }
        panel.Children.Add(grid);

        var ok = new Button { Content = "OK", Width = 72, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Width = 72, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }
}
