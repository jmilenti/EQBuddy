using System.Windows;
using System.Windows.Controls;

namespace EQBuddy.Lite;

/// <summary>Main menu ▸ "Layout functions…": everything that moves a whole layout at
/// once. SAVED LAYOUTS are named presets kept in lite-ui.json (save the current
/// arrangement under a name, load or delete one later — raid layout, solo layout).
/// The SHARE half is the old import/export: the layout as one pasteable EQDPS1 string.
/// A preset IS that string, saved under a name instead of pasted to a friend.</summary>
public sealed class LayoutShareDialog : Window
{
    private readonly TextBox _box;
    private readonly TextBlock _status;
    private readonly ComboBox _presetList;
    private readonly TextBox _nameBox;

    /// <summary>The layout the user asked to apply — from Load or Import — null until
    /// one of them succeeded.</summary>
    internal LayoutShare.Payload? Applied { get; private set; }

    private readonly string _mine;
    private readonly Dictionary<string, string> _presets;
    private readonly Action _persist;

    internal LayoutShareDialog(string mine, Dictionary<string, string> presets, Action persist)
    {
        _mine = mine;
        _presets = presets;
        _persist = persist;
        Title = "Layout functions";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 520;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Text = "A layout is the whole arrangement: feed windows and tabs with their "
                 + "filters, names and colours, section widths and fonts, what is docked "
                 + "where, and what is hidden. Your group code, log path, and session "
                 + "are never included.",
        });

        // ---- saved layouts: named presets, kept locally ---------------------
        panel.Children.Add(new TextBlock
        {
            Text = "Saved layouts",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        var loadRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        _presetList = new ComboBox { MinWidth = 220 };
        loadRow.Children.Add(_presetList);
        var load = new Button
        {
            Content = "Load",
            Width = 70,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Apply the selected saved layout, replacing the current one",
        };
        load.Click += (_, _) =>
        {
            if (_presetList.SelectedItem is not string name
                || !_presets.TryGetValue(name, out var text))
            {
                Say("Pick a saved layout to load.");
                return;
            }
            if (LayoutShare.Import(text) is not { } payload)
            {
                // A hand-edited settings file can hold anything; say so, don't crash.
                Say($"\"{name}\" is not a readable layout — was lite-ui.json edited?");
                return;
            }
            Applied = payload;
            DialogResult = true;
        };
        loadRow.Children.Add(load);
        var delete = new Button
        {
            Content = "Delete",
            Width = 70,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Forget the selected saved layout (your current arrangement is untouched)",
        };
        delete.Click += (_, _) =>
        {
            if (_presetList.SelectedItem is not string name) { Say("Pick a saved layout to delete."); return; }
            _presets.Remove(name);
            _persist();
            RefreshPresets(null);
            Say($"Deleted \"{name}\".", ok: true);
        };
        loadRow.Children.Add(delete);
        panel.Children.Add(loadRow);

        var saveRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _nameBox = new TextBox
        {
            MinWidth = 220,
            MaxLength = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "A name for the current arrangement — \"raid\", \"solo\", \"two monitors\"…",
        };
        saveRow.Children.Add(_nameBox);
        var save = new Button
        {
            Content = "Save current",
            Width = 148,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Save the current arrangement under this name (same name = overwrite)",
        };
        save.Click += (_, _) =>
        {
            var name = _nameBox.Text.Trim();
            if (name.Length == 0) { Say("Give the layout a name first."); return; }
            var existed = _presets.ContainsKey(name);
            _presets[name] = _mine;
            _persist();
            RefreshPresets(name);
            Say(existed ? $"\"{name}\" updated with the current layout." : $"Saved as \"{name}\".", ok: true);
        };
        saveRow.Children.Add(save);
        panel.Children.Add(saveRow);

        // ---- share as text: the old import/export ---------------------------
        panel.Children.Add(new TextBlock
        {
            Text = "Share as text",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 4),
        });
        _box = new TextBox
        {
            Text = mine,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 96,
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
            Text = "The box holds your current layout. Export copies it; Import applies "
                 + "whatever string is in the box.",
        };
        panel.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var copy = new Button
        {
            Content = "Export",
            Width = 84,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Copy YOUR current layout to the clipboard (whatever is typed in "
                + "the box is not what is exported — your live layout is)",
        };
        copy.Click += (_, _) =>
        {
            try
            {
                _box.Text = _mine;
                Clipboard.SetText(_mine);
                Say("Exported — your layout is on the clipboard.", ok: true);
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
        var apply = new Button
        {
            Content = "Import",
            Width = 84,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Apply the layout string in the box, replacing your current layout",
        };
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
        RefreshPresets(null);
    }

    /// <summary>Refill the preset list (sorted, so it reads the same every time) and
    /// select <paramref name="select"/> when given, else the first entry.</summary>
    private void RefreshPresets(string? select)
    {
        var names = _presets.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        _presetList.ItemsSource = names;
        _presetList.SelectedItem = select is { } s && names.Contains(s) ? s
            : names.Count > 0 ? names[0] : null;
        _presetList.IsEnabled = names.Count > 0;
    }

    private void Say(string text, bool ok = false)
    {
        _status.Text = text;
        _status.Foreground = ok
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.Firebrick;
    }
}
