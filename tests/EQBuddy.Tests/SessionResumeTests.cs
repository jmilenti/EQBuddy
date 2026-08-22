using EQBuddy.Core;

namespace EQBuddy.Tests;

/// <summary>
/// Resuming a session mid-log. A session reset notes how far into the log it happened,
/// and the next launch starts reading from that mark — otherwise a restart replays the
/// very lines the reset cleared and hands back the session the player just ended.
/// </summary>
public class SessionResumeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("eqbuddy-resume-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Before =
        "[Sat Aug 22 10:00:00 2026] You have entered Lower Guk.\n" +
        "[Sat Aug 22 10:00:01 2026] You slash a froglok ghoul lord for 1000 points of damage.\n" +
        "[Sat Aug 22 10:00:02 2026] You slash a froglok ghoul lord for 1000 points of damage.\n";

    private const string After =
        "[Sat Aug 22 11:00:00 2026] You slash a froglok ghoul lord for 7 points of damage.\n";

    /// <summary>Writes the log and returns the byte offset where the "after" half starts —
    /// the mark a reset at that instant would have recorded.</summary>
    private string WriteLog(out long resetOffset)
    {
        var path = Path.Combine(_root, "eqlog_Aset_qeynos.txt");
        File.WriteAllText(path, Before + After);
        resetOffset = System.Text.Encoding.Latin1.GetByteCount(Before);
        return path;
    }

    private static StatsSnapshot Ingest(LogWatcher watcher, SessionStats stats)
    {
        // Select kicks the full ingest onto a worker; wait for it rather than racing it.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!watcher.InitialIngestDone && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.True(watcher.InitialIngestDone, "log ingest did not finish");
        return stats.Snapshot();
    }

    [Fact]
    public void SelectingFromTheTopReplaysTheWholeLog()
    {
        var path = WriteLog(out _);
        var stats = new SessionStats();
        var watcher = new LogWatcher(stats);
        watcher.Select(path);

        Assert.Equal(2007, Ingest(watcher, stats).DamageDealt);
    }

    /// <summary>The point of the mark: everything before it stays cleared.</summary>
    [Fact]
    public void ResumingAtTheResetMarkSkipsWhatTheResetCleared()
    {
        var path = WriteLog(out var resetOffset);
        var stats = new SessionStats();
        var watcher = new LogWatcher(stats);
        watcher.Select(path, resetOffset, long.MaxValue);

        var s = Ingest(watcher, stats);
        Assert.Equal(7, s.DamageDealt);
        Assert.DoesNotContain(s.Zones, z => z.Text.Contains("Lower Guk")); // pre-reset zone line skipped
    }

    /// <summary>The offset a reset records is wherever the reader had got to, which is not
    /// necessarily a line boundary — the tail of a half-written line sits in the watcher's
    /// remainder buffer. Resuming there must drop the fragment, not mis-parse it.</summary>
    [Fact]
    public void ResumingMidLineDropsTheFragmentInsteadOfMisreadingIt()
    {
        var path = WriteLog(out var resetOffset);
        var stats = new SessionStats();
        var watcher = new LogWatcher(stats);
        watcher.Select(path, resetOffset - 20, long.MaxValue);   // lands inside the last "before" line

        Assert.Equal(7, Ingest(watcher, stats).DamageDealt);
    }

    /// <summary>A log emptied by the session janitor (or rotated by the game) leaves the
    /// saved mark pointing past the end. Reading from there must yield nothing rather than
    /// throw — the caller drops the mark and starts over.</summary>
    [Fact]
    public void AMarkPastTheEndOfAnEmptiedLogReadsNothing()
    {
        var path = Path.Combine(_root, "eqlog_Aset_qeynos.txt");
        File.WriteAllText(path, "");
        var stats = new SessionStats();
        var watcher = new LogWatcher(stats);
        watcher.Select(path, 4096, long.MaxValue);

        Assert.Equal(0, Ingest(watcher, stats).DamageDealt);
    }
}
