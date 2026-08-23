using System.Windows;
using System.Windows.Controls;

namespace EQBuddy.Lite;

/// <summary>Share a layout as a pasteable string: yours is in the box ready to copy, and
/// pasting someone else's over it and pressing Apply rebuilds your panel to match.</summary>
public sealed class LayoutShareDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _status;

    /// <summary>The string the user asked to apply — null unless Apply succeeded.</summary>
    internal LayoutShare.Payload? Applied { get; private set; }

    internal LayoutShareDialog(string mine)
    {
        Title = "Share layout";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "Your whole panel layout as one string — feed windows and tabs with "
                 + "their filters and colours, section widths, what is docked where, and "
                 + "what is hidden. Copy it to share; paste someone else's in and press "
                 + "Apply to take theirs. Your group code, log path, and session are NOT "
                 + "included.",
        });

        _box = new TextBox
        {
            Text = mine,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 120,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
        };
        _box.SelectAll();
        panel.Children.Add(_box);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.Gray,
            Text = "Applying replaces your current layout.",
        };
        panel.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var copy = new Button { Content = "Copy", Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_box.Text);
                Say("Copied to the clipboard.", ok: true);
            }
            catch (Exception ex)
            {
                // Another process can hold the clipboard open; that is not our bug, but
                // it is our job to say so rather than look like nothing happened.
                EQBuddy.Core.CoreLog.Error(ex);
                Say("Windows would not give up the clipboard — select the text and copy it.");
            }
        };
        var paste = new Button { Content = "Paste", Width = 84, Margin = new Thickness(0, 0, 8, 0) };
        paste.Click += (_, _) =>
        {
            try
            {
                if (Clipboard.GetText() is { Length: > 0 } text) _box.Text = text;
            }
            catch (Exception ex) { EQBuddy.Core.CoreLog.Error(ex); }
        };
        var apply = new Button { Content = "Apply", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        apply.Click += (_, _) =>
        {
            if (LayoutShare.Import(_box.Text) is not { } payload)
            {
                Say("That is not an EQdps layout string (they start with EQDPS1:).");
                return;
            }
            Applied = payload;
            DialogResult = true;
        };
        var close = new Button { Content = "Close", Width = 84, IsCancel = true };
        buttons.Children.Add(copy);
        buttons.Children.Add(paste);
        buttons.Children.Add(apply);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        Content = panel;
        Loaded += (_, _) => _box.Focus();
    }

    private void Say(string text, bool ok = false)
    {
        _status.Text = text;
        _status.Foreground = ok
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.Firebrick;
    }
}
