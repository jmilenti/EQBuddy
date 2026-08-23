using System.Windows;

namespace EQBuddy.Lite;

/// <summary>
/// Audio cues for the moments that cost you a fight if you miss them: pet break, mez
/// break, invis dropping. Each is Off / Sound (a distinct system sound) / Voice (the
/// Windows voice saying what happened — "pet break" beats a beep you have to decode
/// mid-pull). Line-shaped triggers ride <see cref="EQBuddy.Core.LogWatcher.RawTap"/>;
/// the pet-break trigger rides the snapshot instead, because Core's pet claim already
/// encodes every break signal (wear-off lines, the pet turning on you) and re-deriving
/// them here would drift.
/// </summary>
internal sealed class AudioCues
{
    private readonly LiteUiSettings _ui;
    private readonly Dictionary<string, DateTime> _lastFired = new();

    /// <summary>An AE mez breaking on six mobs is ONE event to a human ear.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    public AudioCues(LiteUiSettings ui) => _ui = ui;

    /// <summary>Every raw log line, straight off the watcher thread.</summary>
    public void OnLine(string line)
    {
        // Strip the "[Sat Aug 22 17:20:01 2026] " prefix the same way the feed does.
        var text = line.Length > 27 && line[0] == '[' && line[25] == ']' ? line[27..] : line;

        // "A greater ice bones has been awakened by Xastazi." — the mez break line,
        // whoever broke it: the mezzer needs to hear it either way.
        if (text.Contains("has been awakened by", StringComparison.Ordinal))
            Fire("mez", _ui.CueMezBreak, "mez break", System.Media.SystemSounds.Exclamation);

        // Invis ending. "You appear." is the break itself; the "starting to appear"
        // flicker is the few-seconds warning — both deserve the cue, the warning most
        // of all. (Shapes are the classic EQ lines; none appear in the logs gathered so
        // far, so if EQ Legends words them differently this is the place to fix.)
        else if (text.StartsWith("You appear", StringComparison.Ordinal)
                 || text.Contains("starting to appear", StringComparison.Ordinal)
                 || text.StartsWith("Your invisibility fades", StringComparison.Ordinal))
            Fire("invis", _ui.CueInvisBreak, "invis break", System.Media.SystemSounds.Asterisk);
    }

    /// <summary>The pet claim just went away while play was live (Core dropped it: a
    /// charm wear-off line, or the pet turning on its master).</summary>
    public void PetLost() =>
        Fire("pet", _ui.CuePetBreak, "pet break", System.Media.SystemSounds.Hand);

    /// <summary>Preview from the settings dialog: play a cue exactly as it would fire.</summary>
    public static void Preview(string mode, string phrase)
    {
        var sound = phrase switch
        {
            "mez break" => System.Media.SystemSounds.Exclamation,
            "invis break" => System.Media.SystemSounds.Asterisk,
            _ => System.Media.SystemSounds.Hand,
        };
        Play(mode, phrase, sound);
    }

    private void Fire(string key, string mode, string phrase, System.Media.SystemSound sound)
    {
        if (mode is not ("sound" or "voice")) return;
        var now = DateTime.Now;
        lock (_lastFired)
        {
            if (_lastFired.TryGetValue(key, out var last) && now - last < Cooldown) return;
            _lastFired[key] = now;
        }
        // Off the watcher thread and onto the UI one: the voice is COM (SAPI), and one
        // STA home for it beats trusting every caller's apartment.
        Application.Current?.Dispatcher.BeginInvoke(() => Play(mode, phrase, sound));
    }

    private static void Play(string mode, string phrase, System.Media.SystemSound sound)
    {
        try
        {
            if (mode == "voice")
            {
                // Falls back to the sound when no voice is available (SpokenAlerts
                // returns false rather than throwing).
                if (EQBuddy.UI.Shared.SpokenAlerts.Speak(phrase)) return;
            }
            sound.Play();
        }
        catch (Exception ex)
        {
            EQBuddy.Core.CoreLog.Error(ex);
        }
    }
}
