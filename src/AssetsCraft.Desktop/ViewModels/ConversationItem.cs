using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>Sidebar entry for a conversation: name (renamable inline), size on disk and its "⋯" menu actions.</summary>
public sealed partial class ConversationItem(SpriteProject project, MainWindowViewModel owner) : ObservableObject
{
    private int _measuring;
    private bool _again;

    public SpriteProject Project { get; } = project;
    public string Name => Project.Name;
    public string UpdatedText => Project.UpdatedAt.LocalDateTime.ToString("dd/MM HH:mm");

    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _sizeText = "…";

    // ------------------------------------------------------------------ rename

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = "";

    [RelayCommand]
    private void Rename() => owner.BeginRename(this);

    [RelayCommand]
    private void Delete() => owner.DeleteConversationCommand.Execute(this);

    public void StartEditing()
    {
        EditName = Name;
        IsRenaming = true;
    }

    public void NotifyRenamed() => OnPropertyChanged(nameof(Name));

    // ------------------------------------------------------------------ size

    /// <summary>
    /// Recomputes the size (workspace incl. generated sprites, assets, references, chat + Claude Code files
    /// for this conversation; the shared node_modules is not counted). Coalesces concurrent requests.
    /// </summary>
    public async Task RefreshSizeAsync()
    {
        if (Interlocked.Exchange(ref _measuring, 1) == 1)
        {
            _again = true;
            return;
        }
        try
        {
            do
            {
                _again = false;
                var bytes = await Task.Run(() => ProjectCleanup.MeasureBytes(Project));
                Dispatcher.UIThread.Post(() =>
                {
                    SizeBytes = bytes;
                    SizeText = PendingAttachment.FormatSize(bytes);
                    OnPropertyChanged(nameof(UpdatedText));
                });
            } while (_again);
        }
        catch
        {
            // folder vanished (deleted) — ignore
        }
        finally
        {
            Interlocked.Exchange(ref _measuring, 0);
        }
    }
}
