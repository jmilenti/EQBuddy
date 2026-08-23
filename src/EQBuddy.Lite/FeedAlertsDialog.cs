using System.Windows;
using System.Windows.Controls;

namespace EQBuddy.Lite;

/// <summary>Right-click ▸ Alert tags…: watch words for one feed WINDOW. Any fresh line
/// containing a tag plays the chosen sound and wears the Alert frame (click-to-copy) —
/// a rare's name, a camp word, your own name in chat. Code-built like the other small
/// dialogs.</summary>
public sealed class FeedAlertsDialog : Window
{
    private readonly TextBox _box;
    private readonly ComboBox _sound;

    /// <summary>The edited tags — one per line in the box, trimmed, empties dropped.</summary>
    public List<string> Tags => _box.Text
        .Split('\n')
        .Select(t => t.Trim('\r', ' ', '\t'))
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string Sound => _sound.SelectedItem as string ?? "Exclamation";

    public FeedAlertsDialog(string title, IReadOnlyList<string> tags, string sound)
    {
        Title = $"{title} alert tags";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "One tag per line. When a new log line in this window contains one "
                 + "of them, the line gets a highlight frame (click it to copy) and the "
                 + "sound below plays. Matching ignores case.",
        });

        _box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 96,
            Text = string.Join(Environment.NewLine, tags),
        };
        panel.Children.Add(_box);

        var soundRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        soundRow.Children.Add(new TextBlock
        {
            Text = "Sound",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _sound = new ComboBox
        {
            MinWidth = 150,
            ItemsSource = EQBuddy.UI.Shared.AlertSoundCatalog.Names,
        };
        var normalized = EQBuddy.UI.Shared.AlertSoundCatalog.Normalize(
            sound is { Length: > 0 } ? sound : "Exclamation");
        _sound.SelectedItem =
            Array.IndexOf(EQBuddy.UI.Shared.AlertSoundCatalog.Names, normalized) >= 0
                ? normalized
                : "Exclamation";
        soundRow.Children.Add(_sound);
        var preview = new Button
        {
            Content = "▶",
            Padding = new Thickness(8, 1, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Play the selected sound",
        };
        preview.Click += (_, _) => AudioCues.Preview("sound", Sound, "");
        soundRow.Children.Add(preview);
        panel.Children.Add(soundRow);

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
}
