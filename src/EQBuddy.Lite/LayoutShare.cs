using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>
/// A whole panel layout as ONE pasteable string — feed panes with their filters and
/// colours, section widths, the dock graph, what is hidden, what is collapsed. A string
/// rather than a file because a layout gets shared the way anything else does in this
/// game: pasted into a channel, a Discord message, or a guild wiki, where a file
/// attachment is a nuisance and a download is a thing people rightly hesitate over.
///
/// Shape is <c>EQDPS1:</c> + base64url of gzipped JSON. Gzip because the JSON runs to a
/// few KB and compresses ~5×, base64url because a plain base64 <c>+/=</c> gets mangled
/// by chat clients that linkify or wrap it.
/// </summary>
internal static class LayoutShare
{
    private const string Prefix = "EQDPS1:";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The transferable half of the UI settings. Deliberately NOT everything:
    /// the group code, the session-reset mark, and the log path are personal, and a
    /// layout that carried them would hand a stranger your sync channel.</summary>
    internal sealed class Payload
    {
        public List<FeedPane> FeedPanes { get; set; } = [];
        public Dictionary<string, double> SectionWidths { get; set; } = new();
        public Dictionary<string, string> SectionDocks { get; set; } = new();
        public Dictionary<string, string> SectionDockSides { get; set; } = new();
        public List<string> HiddenSections { get; set; } = [];

        /// <summary>Window positions as OFFSETS from the main panel, not absolute screen
        /// coordinates: the sharer's second monitor is not the recipient's, and an
        /// absolute layout lands half of itself off-screen.</summary>
        public Dictionary<string, double[]> SectionOffsets { get; set; } = new();

        public bool ShowMotes { get; set; }
        public bool ShowLoot { get; set; }
        public bool ShowFights { get; set; }
        public bool ShowSpawns { get; set; }
        public bool ShowGroup { get; set; }
        public bool ShowGroup2 { get; set; }
    }

    /// <summary>Pack the current layout. <paramref name="origin"/> is the main window's
    /// position, which the saved offsets are measured from.</summary>
    public static string Export(LiteUiSettings ui, double originLeft, double originTop)
    {
        var payload = new Payload
        {
            FeedPanes = ui.FeedPanes,
            SectionWidths = ui.SectionWidths,
            SectionDocks = ui.SectionDocks,
            SectionDockSides = ui.SectionDockSides,
            HiddenSections = ui.HiddenSections,
            ShowMotes = ui.ShowMotes,
            ShowLoot = ui.ShowLoot,
            ShowFights = ui.ShowFights,
            ShowSpawns = ui.ShowSpawns,
            ShowGroup = ui.ShowGroup,
            ShowGroup2 = ui.ShowGroup2,
        };
        foreach (var (key, pos) in ui.SectionPositions)
            if (pos is [var x, var y] && !double.IsNaN(x) && !double.IsNaN(y))
                payload.SectionOffsets[key] = [
                    Math.Round(x - originLeft, 1), Math.Round(y - originTop, 1)];

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json);
        return Prefix + Base64Url(packed.ToArray());
    }

    /// <summary>Unpack a shared string, or null when it is not one / is corrupt. Never
    /// throws: the input is whatever was on the clipboard.</summary>
    public static Payload? Import(string? text)
    {
        if (text is null) return null;
        var trimmed = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var bytes = FromBase64Url(trimmed[Prefix.Length..]);
            using var packed = new MemoryStream(bytes);
            using var gzip = new GZipStream(packed, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain, 81_920);
            var payload = JsonSerializer.Deserialize<Payload>(plain.ToArray(), Json);
            // A layout with no feed panes at all would leave the app with no feed and no
            // + to press; treat it as junk rather than applying it.
            return payload is { FeedPanes.Count: > 0 } ? payload : null;
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
            return null;
        }
    }

    /// <summary>Copy a payload over the live settings. Positions come back as absolute
    /// coordinates measured from THIS panel, so the shared arrangement lands around
    /// wherever the recipient keeps their window.</summary>
    public static void Apply(Payload payload, LiteUiSettings ui,
        double originLeft, double originTop)
    {
        ui.FeedPanes = payload.FeedPanes;
        ui.SectionWidths = payload.SectionWidths;
        ui.SectionDocks = payload.SectionDocks;
        ui.SectionDockSides = payload.SectionDockSides;
        ui.HiddenSections = payload.HiddenSections;
        ui.ShowMotes = payload.ShowMotes;
        ui.ShowLoot = payload.ShowLoot;
        ui.ShowFights = payload.ShowFights;
        ui.ShowSpawns = payload.ShowSpawns;
        ui.ShowGroup = payload.ShowGroup;
        ui.ShowGroup2 = payload.ShowGroup2;
        ui.SectionPositions = new Dictionary<string, double[]>();
        foreach (var (key, off) in payload.SectionOffsets)
            if (off is [var dx, var dy])
                ui.SectionPositions[key] = [originLeft + dx, originTop + dy];
    }

    /// <summary>How many windows and tabs a payload describes, for the confirmation.</summary>
    public static string Describe(Payload p)
    {
        var open = p.FeedPanes.Count(x => !x.Closed);
        var windows = p.FeedPanes.Count(x => !x.Closed && x.Host.Length == 0);
        var tabs = open - windows;
        return $"{windows} feed window{(windows == 1 ? "" : "s")}"
            + (tabs > 0 ? $" ({tabs} extra tab{(tabs == 1 ? "" : "s")})" : "")
            + $", {p.SectionWidths.Count} sized section{(p.SectionWidths.Count == 1 ? "" : "s")}"
            + (p.HiddenSections.Count > 0 ? $", {p.HiddenSections.Count} hidden" : "");
    }

    // Base64url: chat clients mangle '+' and '/', and a trailing '=' run invites a
    // copy-paste that clips it.
    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
