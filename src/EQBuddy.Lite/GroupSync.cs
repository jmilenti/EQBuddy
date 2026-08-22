using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>One damage source in a member's breakdown ("Ignite", "melee", …).
/// Hits is swings/casts/procs landed; 0 from clients too old to send it.</summary>
public sealed record BreakdownEntry(string Name, long Total, int Hits = 0);

/// <summary>One mote tier a member has looted ("Mote of Greater Potential" ×3).</summary>
public sealed record MoteEntry(string Name, int Count);

/// <summary>A member's mote haul as shared over sync.</summary>
public sealed record SyncedMotes(int Total, double PerHour, IReadOnlyList<MoteEntry> Tiers)
{
    public static readonly SyncedMotes None = new(0, 0, []);
}

/// <summary>One group member as reported by the relay (their own app parsing their
/// own log — exact numbers, unlike the log-inferred board).</summary>
public sealed record SyncedMember(string Name, double Dps, double SessionDps,
    IReadOnlyList<BreakdownEntry> Breakdown, SyncedMotes Motes);

/// <summary>
/// Opt-in group DPS sync. While a group code is set, every few seconds we POST our
/// own numbers to the relay and get the whole group's back — that single request
/// is both the send and the receive. No code, no network; the log-inferred board
/// stays the default. Sync settings live in lite-sync.json, NOT AppSettings:
/// settings.json is shared with the full app, whose loader drops unknown keys.
/// </summary>
public sealed class GroupSync : IDisposable
{
    /// <summary>The relay (relay/ in this repo, deployed to Cloudflare) at its custom
    /// domain; the older workers.dev URL stays live as an alias.</summary>
    public const string DefaultRelay = "https://eqdps.dystopia-tech.com";

    private static readonly string SettingsPath = AppPaths.File("lite-sync.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private sealed class SyncSettings
    {
        public string RelayUrl { get; set; } = DefaultRelay;
        public string GroupCode { get; set; } = "";
    }

    private readonly SyncSettings _settings = LoadSettings();
    private CancellationTokenSource? _loop;

    /// <summary>Latest own numbers, written by the UI tick, read by the sync loop —
    /// so the loop never touches SessionStats from its own thread.</summary>
    private volatile OwnStats? _own;
    public sealed record OwnStats(string Name, double Dps, double SessionDps,
        IReadOnlyList<BreakdownEntry> Top, MotesSummary Motes);

    /// <summary>Latest group roster from the relay; empty when off or unreachable.</summary>
    public IReadOnlyList<SyncedMember> Members { get; private set; } = [];

    /// <summary>Null when healthy or off; otherwise a short reason for the label.</summary>
    public string? LastError { get; private set; }

    public string GroupCode => _settings.GroupCode;
    public string RelayUrl => _settings.RelayUrl;
    public bool Active => _settings.GroupCode.Length > 0 && _settings.RelayUrl.Length > 0;

    public void Publish(string name, double dps, double sessionDps,
        IReadOnlyList<BreakdownEntry> top, MotesSummary motes) =>
        _own = new OwnStats(name, dps, sessionDps, top, motes);

    /// <summary>Set (or clear, with an empty code) the group and restart the loop.</summary>
    public void Configure(string groupCode, string relayUrl)
    {
        _settings.GroupCode = groupCode.Trim().ToUpperInvariant();
        _settings.RelayUrl = relayUrl.Trim().TrimEnd('/');
        SaveSettings();

        _loop?.Cancel();
        _loop = null;
        Members = [];
        LastError = null;
        if (!Active) return;

        var cts = _loop = new CancellationTokenSource();
        Task.Run(() => RunLoop(cts.Token));
    }

    public void Start()
    {
        if (Active) Configure(_settings.GroupCode, _settings.RelayUrl);
    }

    private async Task RunLoop(CancellationToken ct)
    {
        var url = $"{_settings.RelayUrl}/v1/group/{Uri.EscapeDataString(_settings.GroupCode)}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Nothing to report until the log names our character; poll again shortly.
                if (_own is { Name.Length: > 0 } own)
                {
                    using var response = await Http.PostAsJsonAsync(url,
                        new
                        {
                            name = own.Name,
                            dps = own.Dps,
                            sdps = own.SessionDps,
                            top = own.Top.Select(t => new { n = t.Name, t = t.Total, h = t.Hits }),
                            motes = new
                            {
                                tot = own.Motes.Total,
                                ph = own.Motes.PerHour,
                                tiers = own.Motes.Tiers.Select(t => new { n = t.Item, c = t.Count }),
                            },
                        }, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var roster = await response.Content.ReadFromJsonAsync<Roster>(ct);
                        Members = roster?.Members?
                            .Select(m => new SyncedMember(m.Name ?? "?", m.Dps, m.Sdps,
                                m.Top?.Where(t => t.N is { Length: > 0 })
                                    .Select(t => new BreakdownEntry(t.N!, t.T, t.H))
                                    .ToList() ?? [],
                                m.Motes is { } mm
                                    ? new SyncedMotes(mm.Tot, mm.Ph,
                                        mm.Tiers?.Where(t => t.N is { Length: > 0 })
                                            .Select(t => new MoteEntry(t.N!, t.C))
                                            .ToList() ?? [])
                                    : SyncedMotes.None))
                            .ToList() ?? [];
                        LastError = null;
                    }
                    else
                    {
                        LastError = response.StatusCode == System.Net.HttpStatusCode.Conflict
                            ? "group full" : $"relay error {(int)response.StatusCode}";
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = "relay unreachable";
                CoreLog.Error(ex);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed class Roster
    {
        public List<RosterMember>? Members { get; set; }
    }

    private sealed class RosterMember
    {
        public string? Name { get; set; }
        public double Dps { get; set; }
        public double Sdps { get; set; }
        public List<TopEntry>? Top { get; set; }
        public RosterMotes? Motes { get; set; }
    }

    private sealed class TopEntry
    {
        public string? N { get; set; }
        public long T { get; set; }
        public int H { get; set; }
    }

    private sealed class RosterMotes
    {
        public int Tot { get; set; }
        public double Ph { get; set; }
        public List<MoteTierEntry>? Tiers { get; set; }
    }

    private sealed class MoteTierEntry
    {
        public string? N { get; set; }
        public int C { get; set; }
    }

    /// <summary>The default relay URL before the EQdps rename. A settings file that
    /// saved it meant "the default", not a deliberate choice — follow the default.</summary>
    private const string LegacyRelay = "https://eqbuddy-relay.milentis-jason.workers.dev";

    private static SyncSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(SettingsPath), JsonOpts) is { } s)
            {
                if (string.Equals(s.RelayUrl, LegacyRelay, StringComparison.OrdinalIgnoreCase))
                    s.RelayUrl = DefaultRelay;
                return s;
            }
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
        }
        return new SyncSettings();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, JsonOpts));
        }
        catch (Exception ex)
        {
            CoreLog.Error(ex);
        }
    }

    public void Dispose() => _loop?.Cancel();
}
