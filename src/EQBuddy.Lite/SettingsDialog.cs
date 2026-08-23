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
    private readonly ComboBox _history;
    private readonly Dictionary<string, ComboBox> _cues = new();

    public bool GroupBoardUseSync => _groupSync.IsChecked == true;
    public bool ShowGroupMotes => _groupMotes.IsChecked == true;

    /// <summary>Sections the user UNticked — i.e. wants gone from the UI.</summary>
    public List<string> HiddenSections =>
        _sections.Where(kv => kv.Value.IsChecked != true).Select(kv => kv.Key).ToList();

    public int FeedHistory =>
        _history.SelectedItem is ComboBoxItem { Tag: int n } ? n : 20_000;

    /// <summary>The chosen mode for one audio cue: "off", "sound", or "voice".</summary>
    public string Cue(string key) =>
        _cues[key].SelectedItem is ComboBoxItem { Tag: string mode } ? mode : "off";

    public SettingsDialog(bool groupBoardUseSync, bool showGroupMotes,
        IReadOnlyList<string> sectionKeys, IReadOnlyList<string> hiddenSections,
        int feedHistory, string cuePetBreak, string cueMezBreak, string cueInvisBreak)
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

        var historyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
        };
        historyRow.Children.Add(new TextBlock
        {
            Text = "Feed history:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _history = new ComboBox { MinWidth = 110 };
        int[] sizes = [2_000, 20_000, 100_000];
        // A hand-edited value that isn't one of the presets still shows as itself.
        if (!sizes.Contains(feedHistory)) sizes = [.. sizes, feedHistory];
        foreach (var n in sizes.OrderBy(n => n))
            _history.Items.Add(new ComboBoxItem
            {
                Tag = n,
                Content = $"{n:N0} lines",
                IsSelected = n == feedHistory,
            });
        historyRow.Children.Add(_history);
        historyRow.Children.Add(new TextBlock
        {
            Text = "  (scrollback depth — combat and raw log)",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(historyRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Audio cues:",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            Text = "A sound or the Windows voice for the moments that cost a fight if " +
                   "missed. ▶ previews the choice.",
        });
        var cueGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        cueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cueGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void CueRow(string key, string label, string phrase, string current)
        {
            var r = cueGrid.RowDefinitions.Count;
            cueGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
            };
            Grid.SetRow(name, r);
            cueGrid.Children.Add(name);

            var combo = new ComboBox { MinWidth = 90, Margin = new Thickness(0, 2, 6, 2) };
            foreach (var (tag, text) in new[] { ("off", "Off"), ("sound", "Sound"), ("voice", "Voice") })
                combo.Items.Add(new ComboBoxItem
                {
                    Tag = tag,
                    Content = text,
                    IsSelected = tag == current,
                });
            if (combo.SelectedItem is null) combo.SelectedIndex = 0;
            Grid.SetRow(combo, r);
            Grid.SetColumn(combo, 1);
            cueGrid.Children.Add(combo);
            _cues[key] = combo;

            var play = new Button
            {
                Content = "▶",
                Width = 26,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "Preview this cue as it would fire",
            };
            play.Click += (_, _) => AudioCues.Preview(Cue(key), phrase);
            Grid.SetRow(play, r);
            Grid.SetColumn(play, 2);
            cueGrid.Children.Add(play);
        }
        CueRow("pet", "Pet break", "pet break", cuePetBreak);
        CueRow("mez", "Mez break", "mez break", cueMezBreak);
        CueRow("invis", "Invis break", "invis break", cueInvisBreak);
        panel.Children.Add(cueGrid);

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
                // The two GROUP boards share a name but not a clock — the key alone
                // ("group2") would make the tick boxes a guessing game.
                Content = key switch
                {
                    "group" => "GROUP · FIGHT",
                    "group2" => "GROUP · SESSION",
                    "feed" => "FEED",
                    // Spawned feed windows: "feed2" → "FEED 2", and so on.
                    _ when key.StartsWith("feed", StringComparison.Ordinal) => "FEED " + key[4..],
                    _ => key.ToUpperInvariant(),
                },
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
