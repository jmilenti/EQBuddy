using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using EQBuddy.Core;

namespace EQBuddy.Lite;

/// <summary>One group member as reported by the relay (their own app parsing their
/// own log — exact numbers, unlike the log-inferred board).</summary>
public sealed record SyncedMember(string Name, double Dps, double SessionDps);

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
    public sealed record OwnStats(string Name, double Dps, double SessionDps);

    /// <summary>Latest group roster from the relay; empty when off or unreachable.</summary>
    public IReadOnlyList<SyncedMember> Members { get; private set; } = [];

    /// <summary>Null when healthy or off; otherwise a short reason for the label.</summary>
    public string? LastError { get; private set; }

    public string GroupCode => _settings.GroupCode;
    public string RelayUrl => _settings.RelayUrl;
    public bool Active => _settings.GroupCode.Length > 0 && _settings.RelayUrl.Length > 0;

    public void Publish(string name, double dps, double sessionDps) =>
        _own = new OwnStats(name, dps, sessionDps);

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
                        new { name = own.Name, dps = own.Dps, sdps = own.SessionDps }, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var roster = await response.Content.ReadFromJsonAsync<Roster>(ct);
                        Members = roster?.Members?
                            .Select(m => new SyncedMember(m.Name ?? "?", m.Dps, m.Sdps))
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
    }

    private static SyncSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(SettingsPath), JsonOpts) is { } s)
                return s;
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
