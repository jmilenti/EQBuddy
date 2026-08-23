using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>The feed window's "Colours…" dialog: one row per kind of line, each a hex
/// value with a live swatch. Per WINDOW, not global — a caster's window and a tank's
/// window want different things out of the same log. Built in code like the other small
/// dialogs; one of these doesn't earn a XAML file.</summary>
public sealed class FeedColorsDialog : Window
{
    private readonly List<(string Role, TextBox Box, Border Swatch, Func<FeedColors, string> Default)> _rows = [];

    /// <summary>The edited colours, only meaningful after the dialog returns true.</summary>
    public FeedColors Colors { get; } = new();

    /// <summary>Apply the result to every feed window, not just the one that opened this.</summary>
    public bool ApplyToAll { get; private set; }

    public FeedColorsDialog(string title, FeedColors current)
    {
        Title = $"{title} colours";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "Row colours for this feed window. \"Spell / DoT / proc\" is the base "
                 + "colour of a casting line and \"Ability name\" is the spell or skill "
                 + "picked out inside it. Hex like #E8B24A; a bad value falls back to the "
                 + "default.",
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        void Row(string label, string role, string value, Func<FeedColors, string> fallback)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 3, 10, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(name, r);
            grid.Children.Add(name);

            var box = new TextBox
            {
                Text = value,
                Width = 92,
                Margin = new Thickness(0, 2, 6, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(box, r);
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var swatch = new Border
            {
                Width = 34,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(swatch, r);
            Grid.SetColumn(swatch, 2);
            grid.Children.Add(swatch);

            box.TextChanged += (_, _) => Paint(box, swatch);
            Paint(box, swatch);
            _rows.Add((role, box, swatch, fallback));
        }

        Row("You", nameof(FeedColors.You), current.You, c => c.You);
        Row("Pet", nameof(FeedColors.Pet), current.Pet, c => c.Pet);
        Row("Other players", nameof(FeedColors.Group), current.Group, c => c.Group);
        Row("Damage you take", nameof(FeedColors.Incoming), current.Incoming, c => c.Incoming);
        Row("Heals", nameof(FeedColors.Heal), current.Heal, c => c.Heal);
        Row("Criticals", nameof(FeedColors.Crit), current.Crit, c => c.Crit);
        Row("Kills", nameof(FeedColors.Kill), current.Kill, c => c.Kill);
        Row("Spell / DoT / proc", nameof(FeedColors.Spell), current.Spell, c => c.Spell);
        Row("Ability name", nameof(FeedColors.Ability), current.Ability, c => c.Ability);
        Row("Casting messages", nameof(FeedColors.Cast), current.Cast, c => c.Cast);
        Row("Everything else", nameof(FeedColors.Other), current.Other, c => c.Other);
        Row("Kill summaries", nameof(FeedColors.Summary), current.Summary, c => c.Summary);
        Row("Timestamps / misses", nameof(FeedColors.Dim), current.Dim, c => c.Dim);
        panel.Children.Add(grid);

        var all = new CheckBox
        {
            Content = "Use these colours in every feed window",
            Margin = new Thickness(0, 12, 0, 0),
        };
        panel.Children.Add(all);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var reset = new Button { Content = "Defaults", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            var d = FeedPane.DefaultColors();
            foreach (var (_, box, _, fallback) in _rows) box.Text = fallback(d);
        };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            foreach (var (role, box, _, fallback) in _rows)
            {
                var text = FeedPalette.Parse(box.Text) is { } c
                    ? FeedPalette.Hex(c)
                    : fallback(FeedPane.DefaultColors());
                typeof(FeedColors).GetProperty(role)!.SetValue(Colors, text);
            }
            ApplyToAll = all.IsChecked == true;
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Content = panel;
    }

    private static void Paint(TextBox box, Border swatch) =>
        swatch.Background = FeedPalette.Frozen(box.Text, Brushes.Transparent);
}
