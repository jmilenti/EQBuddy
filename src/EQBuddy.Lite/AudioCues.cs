using System.IO;
using System.Windows;
using System.Windows.Media;

namespace EQBuddy.Lite;

/// <summary>
/// Audio cues for the moments that cost you a fight if you miss them: pet break, mez
/// break, invis dropping. Each is Off / Sound (any of the shared alert palette) / Voice
/// (the Windows voice saying a phrase you can edit — the voice reads "mez" as "may", so
/// the spelling is yours to fix). Line-shaped triggers ride
/// <see cref="EQBuddy.Core.LogWatcher.RawTap"/>; the pet-break trigger rides the snapshot
/// instead, because Core's pet claim already encodes every break signal (wear-off lines,
/// the pet turning on you) and re-deriving them here would drift.
/// </summary>
internal sealed class AudioCues
{
    private readonly LiteUiSettings _ui;
    private readonly Dictionary<string, DateTime> _lastFired = new();

    /// <summary>An AE mez breaking on six mobs is ONE event to a human ear.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    /// <summary>Shared by every cue and every preview. MediaPlayer is a UI-thread object
    /// and all play goes through the dispatcher, so one instance is enough.</summary>
    private static MediaPlayer? _player;

    public AudioCues(LiteUiSettings ui) => _ui = ui;

    /// <summary>Every raw log line, straight off the watcher thread.</summary>
    public void OnLine(string line)
    {
        // Strip the "[Sat Aug 22 17:20:01 2026] " prefix the same way the feed does.
        var text = line.Length > 27 && line[0] == '[' && line[25] == ']' ? line[27..] : line;

        // "A greater ice bones has been awakened by Xastazi." — the mez break line,
        // whoever broke it: the mezzer needs to hear it either way.
        if (text.Contains("has been awakened by", StringComparison.Ordinal))
            Fire("mez", _ui.CueMezBreak, _ui.CueMezSound, _ui.CueMezPhrase);

        // Invis ending. "You appear." is the break itself; the "starting to appear"
        // flicker is the few-seconds warning — both deserve the cue, the warning most
        // of all. (Shapes are the classic EQ lines; none appear in the logs gathered so
        // far, so if EQ Legends words them differently this is the place to fix.)
        else if (text.StartsWith("You appear", StringComparison.Ordinal)
                 || text.Contains("starting to appear", StringComparison.Ordinal)
                 || text.StartsWith("Your invisibility fades", StringComparison.Ordinal))
            Fire("invis", _ui.CueInvisBreak, _ui.CueInvisSound, _ui.CueInvisPhrase);
    }

    /// <summary>The pet claim just went away while play was live (Core dropped it: a
    /// charm wear-off line, or the pet turning on its master).</summary>
    public void PetLost() =>
        Fire("pet", _ui.CuePetBreak, _ui.CuePetSound, _ui.CuePetPhrase);

    /// <summary>Preview from the settings dialog: play a cue exactly as it would fire.</summary>
    public static void Preview(string mode, string sound, string phrase) =>
        Play(mode, sound, phrase);

    private void Fire(string key, string mode, string sound, string phrase)
    {
        if (mode is not ("sound" or "voice")) return;
        var now = DateTime.Now;
        lock (_lastFired)
        {
            if (_lastFired.TryGetValue(key, out var last) && now - last < Cooldown) return;
            _lastFired[key] = now;
        }
        // Off the watcher thread and onto the UI one: the voice is COM (SAPI) and
        // MediaPlayer is a DispatcherObject, so one STA home for both beats trusting
        // every caller's apartment.
        Application.Current?.Dispatcher.BeginInvoke(() => Play(mode, sound, phrase));
    }

    private static void Play(string mode, string sound, string phrase)
    {
        try
        {
            if (mode == "voice")
            {
                // Falls back to the sound when no voice is available (SpokenAlerts
                // returns false rather than throwing).
                var spoken = phrase is { Length: > 0 } ? phrase : "alert";
                if (EQBuddy.UI.Shared.SpokenAlerts.Speak(spoken)) return;
            }
            PlaySound(sound);
        }
        catch (Exception ex)
        {
            EQBuddy.Core.CoreLog.Error(ex);
        }
    }

    /// <summary>One of the shared alert palette by name, played from the Windows Media
    /// folder. Same catalog and same lookup the full app uses, so a sound named here
    /// means the same thing there. Anything unresolvable falls back to the system
    /// Asterisk rather than silence — a cue that plays nothing reads as broken.</summary>
    private static void PlaySound(string choice)
    {
        var name = EQBuddy.UI.Shared.AlertSoundCatalog.Normalize(
            choice is { Length: > 0 } ? choice : "Ding");
        var entry = Array.Find(EQBuddy.UI.Shared.AlertSoundCatalog.Sounds, s => s.Name == name);
        var file = entry.WindowsMediaFile is { } media
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media", media)
            : name;   // a custom path, hand-edited into the settings file
        if (File.Exists(file))
        {
            _player ??= new MediaPlayer();
            // MediaPlayer defaults to HALF volume — that was the whole "alerts are very
            // quiet" report against the full app.
            _player.Volume = 1.0;
            _player.Open(new Uri(file));
            _player.Play();
            return;
        }
        System.Media.SystemSounds.Asterisk.Play();
    }
}
