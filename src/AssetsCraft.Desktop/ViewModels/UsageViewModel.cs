using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Claude;

namespace AssetsCraft.Desktop.ViewModels;

public sealed record UsageRow(string Label, double Percent, string PercentText, string ResetText, bool IsHigh, bool IsFull);

/// <summary>Claude subscription usage meter (session / weekly limits).</summary>
public sealed partial class UsageViewModel : ObservableObject
{
    private readonly UsageMonitor _monitor;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _lastAutoRefresh = DateTime.MinValue;

    public UsageViewModel(UsageMonitor monitor)
    {
        _monitor = monitor;
        _monitor.Changed += () => Dispatcher.UIThread.Post(Sync);
        _clock.Tick += (_, _) =>
        {
            // keep "há X min" / "em 2h" fresh and re-read the limits every 10 minutes (free call)
            if (DateTime.Now - _lastAutoRefresh > TimeSpan.FromMinutes(10)) Refresh();
            else Sync();
        };
        _clock.Start();
        Sync();
        Refresh();
    }

    public ObservableCollection<UsageRow> Rows { get; } = [];

    [ObservableProperty] private string _updatedText = "";
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasRows;

    [RelayCommand]
    private void Refresh()
    {
        _lastAutoRefresh = DateTime.Now;
        _ = _monitor.RefreshAsync();
    }

    private void Sync()
    {
        Rows.Clear();
        foreach (var w in _monitor.Windows)
        {
            var pct = Math.Clamp(w.Percent, 0, 100);
            Rows.Add(new UsageRow(w.Label, pct, $"{pct:0}%", ResetText(w), pct >= 80, pct >= 100));
        }
        HasRows = Rows.Count > 0;
        IsRefreshing = _monitor.IsRefreshing;
        Error = _monitor.Error is { } e && !HasRows ? "Não foi possível ler o limite: " + e : null;
        UpdatedText = _monitor.UpdatedAt is { } at ? "atualizado " + Ago(at) : "";
    }

    private static string ResetText(UsageWindow w)
    {
        if (w.ResetsAt is not { } at) return w.ResetsText is { } raw ? "reinicia " + raw : "";
        var left = at - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "reiniciando";
        if (left < TimeSpan.FromHours(24))
            return left.TotalHours >= 1 ? $"reinicia em {(int)left.TotalHours}h {left.Minutes:00}min" : $"reinicia em {Math.Max(1, left.Minutes)} min";
        return "reinicia " + at.LocalDateTime.ToString("ddd dd/MM HH:mm", new System.Globalization.CultureInfo("pt-BR"));
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d < TimeSpan.FromMinutes(1)) return "agora";
        if (d < TimeSpan.FromHours(1)) return $"há {(int)d.TotalMinutes} min";
        if (d < TimeSpan.FromDays(1)) return $"há {(int)d.TotalHours} h";
        return at.LocalDateTime.ToString("dd/MM HH:mm");
    }
}
