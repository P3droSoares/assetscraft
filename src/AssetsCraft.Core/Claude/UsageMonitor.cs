using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetsCraft.Core.Claude;

/// <summary>One usage window of the Claude subscription (e.g. 5-hour session, weekly).</summary>
public sealed record UsageWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt, string? ResetsText);

/// <summary>
/// Tracks Claude Code usage limits.
/// Sources: <c>claude -p /usage</c> (free: no model turn, run with --no-session-persistence) and the
/// <c>rate_limit_event</c> lines that every conversation emits while running.
/// The last snapshot is cached on disk so the meter shows something right after startup.
/// </summary>
public sealed partial class UsageMonitor
{
    private readonly ClaudeCodeOptions _options;
    private readonly string _workDir;
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private Dictionary<string, UsageWindow> _windows = [];

    public UsageMonitor(ClaudeCodeOptions options, string dataRoot)
    {
        _options = options;
        _workDir = Path.Combine(dataRoot, "usage");
        Directory.CreateDirectory(_workDir);
        _cacheFile = Path.Combine(_workDir, "usage.json");
        LoadCache();
    }

    public DateTimeOffset? UpdatedAt { get; private set; }
    public string? Error { get; private set; }
    public bool IsRefreshing { get; private set; }

    /// <summary>Raised from background threads.</summary>
    public event Action? Changed;

    public IReadOnlyList<UsageWindow> Windows
    {
        get
        {
            lock (_lock)
                return _windows.Values.OrderBy(w => Order(w.Key)).ThenBy(w => w.Label).ToList();
        }
    }

    // ------------------------------------------------------------------ /usage

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return; // already refreshing
        IsRefreshing = true;
        Changed?.Invoke();
        try
        {
            var psi = CommandResolver.Create(_options.ExecutablePath,
                ["-p", "/usage", "--output-format", "json", "--no-session-persistence",
                 "--setting-sources", _options.SettingSources ?? "project,local"], _workDir);
            using var p = ChildProcesses.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }

            using var doc = JsonDocument.Parse(await stdout);
            var text = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            var parsed = ParseUsageText(text);
            if (parsed.Count == 0) throw new InvalidOperationException("resposta do /usage sem limites (conta por API key?)");

            lock (_lock)
            {
                foreach (var w in parsed) _windows[w.Key] = w;
            }
            UpdatedAt = DateTimeOffset.Now;
            Error = null;
            SaveCache();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
            CleanupResidue();
            Changed?.Invoke();
        }
    }

    /// <summary>Parses lines like "Current week (all models): 5% used · resets Nov 8, 10pm (America/Sao_Paulo)".</summary>
    public static List<UsageWindow> ParseUsageText(string text)
    {
        var list = new List<UsageWindow>();
        foreach (Match m in UsageLine().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim();
            var pct = double.Parse(m.Groups["pct"].Value, CultureInfo.InvariantCulture);
            var resetsRaw = m.Groups["resets"].Success ? m.Groups["resets"].Value.Trim() : null;
            var (key, label) = name switch
            {
                "Current session" => ("five_hour", "Sessão (5h)"),
                "Current week (all models)" => ("seven_day", "Semana · todos os modelos"),
                _ when name.StartsWith("Current week (") => ("seven_day_" + name[14..^1].ToLowerInvariant(), "Semana · " + name[14..^1]),
                _ => (name.ToLowerInvariant(), name),
            };
            var resetsAt = ParseReset(resetsRaw);
            list.Add(new UsageWindow(key, label, pct, resetsAt, resetsAt is null ? resetsRaw : null));
        }
        return list;
    }

    private static DateTimeOffset? ParseReset(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var s = TimeZoneSuffix().Replace(raw, "").Trim(); // "Nov 8, 10pm" | "Nov 8, 10:30pm" | "8pm"
        string[] formats = ["MMM d, htt", "MMM d, h:mmtt", "htt", "h:mmtt"];
        if (!DateTime.TryParseExact(s, formats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var dt))
            return null;
        var now = DateTime.Now;
        if (char.IsDigit(s[0])) dt = now.Date + dt.TimeOfDay;      // time only → today (or tomorrow)
        if (dt < now.AddMinutes(-5)) dt = char.IsDigit(s[0]) ? dt.AddDays(1) : dt.AddYears(1);
        return new DateTimeOffset(dt);
    }

    // ------------------------------------------------------------------ live events

    /// <summary>Applies a rate_limit_event payload (rate_limit_info.unifiedWindows) from a running conversation.</summary>
    public void Apply(IReadOnlyList<UsageWindow> windows)
    {
        if (windows.Count == 0) return;
        lock (_lock)
        {
            foreach (var w in windows)
            {
                // keep the nicer label / per-model windows from /usage
                var label = _windows.TryGetValue(w.Key, out var old) ? old.Label : w.Label;
                _windows[w.Key] = w with { Label = label };
            }
        }
        UpdatedAt = DateTimeOffset.Now;
        SaveCache();
        Changed?.Invoke();
    }

    public static List<UsageWindow> ParseRateLimitEvent(JsonElement root)
    {
        var list = new List<UsageWindow>();
        if (!root.TryGetProperty("rate_limit_info", out var info)
            || !info.TryGetProperty("unifiedWindows", out var windows)
            || windows.ValueKind != JsonValueKind.Object)
            return list;

        foreach (var w in windows.EnumerateObject())
        {
            if (!w.Value.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) continue;
            DateTimeOffset? resets = w.Value.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).ToLocalTime()
                : null;
            var label = w.Name switch
            {
                "five_hour" => "Sessão (5h)",
                "seven_day" => "Semana · todos os modelos",
                _ => w.Name.Replace('_', ' '),
            };
            list.Add(new UsageWindow(w.Name, label, Math.Round(u.GetDouble() * 100, 1), resets, null));
        }
        return list;
    }

    // ------------------------------------------------------------------ cache / residue

    private sealed record Cache(DateTimeOffset UpdatedAt, List<UsageWindow> Windows);

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFile)) return;
            var c = JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cacheFile));
            if (c is null) return;
            _windows = c.Windows.ToDictionary(w => w.Key);
            UpdatedAt = c.UpdatedAt;
        }
        catch { /* ignore corrupt cache */ }
    }

    private void SaveCache()
    {
        try
        {
            List<UsageWindow> snapshot;
            lock (_lock) snapshot = _windows.Values.ToList();
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(new Cache(UpdatedAt ?? DateTimeOffset.Now, snapshot)));
        }
        catch { /* best effort */ }
    }

    /// <summary>The /usage call runs with --no-session-persistence; remove anything Claude Code still created for it.</summary>
    private void CleanupResidue()
    {
        try
        {
            var sanitized = Regex.Replace(Path.GetFullPath(_workDir).TrimEnd(Path.DirectorySeparatorChar), "[^A-Za-z0-9]", "-");
            var dir = Path.Combine(Projects.ProjectCleanup.ClaudeConfigDir, "projects", sanitized);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static int Order(string key) => key switch
    {
        "five_hour" => 0,
        "seven_day" => 1,
        _ => 2,
    };

    [GeneratedRegex(@"^(?<name>Current [^:\n]+):\s*(?<pct>\d+(?:\.\d+)?)%\s*used(?:\s*·\s*resets\s+(?<resets>[^\n]+))?\s*$", RegexOptions.Multiline)]
    private static partial Regex UsageLine();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TimeZoneSuffix();
}
