using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>Right-click ▸ Alert tags…: the watch rules for ONE feed tab. Several rules,
/// each with its own words, its own sound and its own on/off switch — "my name in chat"
/// and "the rare is up" are different events and deserve different noises.
///
/// One row per rule, added and removed live; code-built like the other small dialogs.
/// Tags are comma-separated so a rule stays one line tall and several rules fit on
/// screen at once — the pre-1.80 dialog gave the single tag list a multi-line box,
/// which does not stack.</summary>
public sealed class FeedAlertsDialog : Window
{
    private readonly StackPanel _rules;
    private readonly List<RuleRow> _rows = [];
    private readonly TextBlock _empty;

    /// <summary>The edited rules, only meaningful after the dialog returns true. A rule
    /// with no usable tags is dropped — an empty rule can never fire, and keeping it
    /// would just be a row that does nothing next time the dialog opens.</summary>
    public List<FeedAlertRule> Alerts => _rows
        .Select(r => r.ToRule())
        .Where(r => r.Tags.Count > 0)
        .ToList();

    public FeedAlertsDialog(string title, IReadOnlyList<FeedAlertRule> alerts)
    {
        Title = $"{title} alerts";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 620 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Alerts for THIS tab. When a line this tab is showing contains one of "
                 + "a rule's words, the line gets a highlight frame (click it to copy) "
                 + "and that rule's sound plays. Separate words with commas; matching "
                 + "ignores case. A rule only ever fires on a line its own tab's filters "
                 + "let through — so an alert can't ring for something you can't see.",
        });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        Columns(head);
        Head(head, "", 0);
        Head(head, "Name", 1);
        Head(head, "Words (comma-separated)", 2);
        Head(head, "Sound", 3);
        panel.Children.Add(head);

        _rules = new StackPanel();
        panel.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 260,
            Content = _rules,
        });

        _empty = new TextBlock
        {
            Text = "No alerts on this tab yet — add one below.",
            Opacity = 0.7,
            Margin = new Thickness(2, 6, 0, 2),
        };
        panel.Children.Add(_empty);

        foreach (var rule in alerts) AddRow(rule);

        var add = new Button
        {
            Content = "+ Add alert",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 8, 0, 0),
        };
        add.Click += (_, _) => AddRow(new FeedAlertRule());
        panel.Children.Add(add);

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
    }

    /// <summary>The one column layout both the header and every rule row use, so the
    /// headings sit over the fields they name.</summary>
    private static void Columns(Grid grid)
    {
        foreach (var w in new double[] { 26, 120, 250, 130, 30, 26 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
    }

    private static void Head(Grid grid, string text, int column)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Opacity = 0.7,
            Margin = new Thickness(2, 0, 6, 0),
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private void AddRow(FeedAlertRule rule)
    {
        var row = new RuleRow(rule, Remove);
        _rows.Add(row);
        _rules.Children.Add(row.Root);
        RefreshEmpty();
    }

    private void Remove(RuleRow row)
    {
        _rows.Remove(row);
        _rules.Children.Remove(row.Root);
        RefreshEmpty();
    }

    private void RefreshEmpty() =>
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>One rule's controls, and the rule it turns back into on OK.</summary>
    private sealed class RuleRow
    {
        public Grid Root { get; }

        private readonly CheckBox _enabled;
        private readonly TextBox _name;
        private readonly TextBox _tags;
        private readonly ComboBox _sound;

        public RuleRow(FeedAlertRule rule, Action<RuleRow> remove)
        {
            Root = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            Columns(Root);

            _enabled = new CheckBox
            {
                IsChecked = rule.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Off keeps the rule but stops it firing or framing",
            };
            Place(_enabled, 0);

            _name = new TextBox
            {
                Text = rule.Name,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "What to call this alert (optional)",
            };
            Place(_name, 1);

            _tags = new TextBox
            {
                Text = string.Join(", ", rule.Tags),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Words to watch for, separated by commas. A line containing "
                    + "any one of them fires this alert.",
            };
            Place(_tags, 2);

            _sound = new ComboBox
            {
                Margin = new Thickness(0, 0, 6, 0),
                ItemsSource = EQBuddy.UI.Shared.AlertSoundCatalog.Names,
            };
            var normalized = EQBuddy.UI.Shared.AlertSoundCatalog.Normalize(
                rule.Sound is { Length: > 0 } s ? s : "Exclamation");
            _sound.SelectedItem =
                Array.IndexOf(EQBuddy.UI.Shared.AlertSoundCatalog.Names, normalized) >= 0
                    ? normalized
                    : "Exclamation";
            Place(_sound, 3);

            var preview = new Button
            {
                Content = "▶",
                Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Play this alert's sound",
            };
            preview.Click += (_, _) => AudioCues.Preview("sound", Sound, "");
            Place(preview, 4);

            var drop = new Button
            {
                Content = "✕",
                Padding = new Thickness(4, 1, 4, 2),
                Foreground = Brushes.IndianRed,
                ToolTip = "Remove this alert",
            };
            drop.Click += (_, _) => remove(this);
            Place(drop, 5);
        }

        private void Place(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            Root.Children.Add(element);
        }

        private string Sound => _sound.SelectedItem as string ?? "Exclamation";

        public FeedAlertRule ToRule() => new()
        {
            Name = _name.Text.Trim(),
            Sound = Sound,
            Enabled = _enabled.IsChecked == true,
            Tags = _tags.Text
                .Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }
}
