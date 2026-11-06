using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>
/// One internal conversation (workspace) of an asset: the sprite/tiles conversation itself, or one of the
/// animation conversations of a sprite. The sidebar shows <see cref="AssetItem"/>s, not these.
/// </summary>
public sealed partial class ConversationItem(SpriteProject project) : ObservableObject
{
    private int _measuring;
    private bool _again;

    public SpriteProject Project { get; } = project;
    public string Name => Project.Name;

    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _sizeText = "…";

    public void NotifyRenamed() => OnPropertyChanged(nameof(Name));

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
