using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AssetsCraft.Desktop.ViewModels;

namespace AssetsCraft.Desktop.Views;

public partial class ProjectView : UserControl
{
    private ProjectViewModel? _vm;

    public ProjectView()
    {
        InitializeComponent();

        // Tunnel so Enter is seen before the multi-line TextBox inserts a newline.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Files dropped anywhere (chat or result panel) become attachments of the next message.
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDropReference);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.MessagesUpdated -= ScrollToEnd;
        _vm = DataContext as ProjectViewModel;
        if (_vm is not null)
        {
            _vm.MessagesUpdated += ScrollToEnd;
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        // Only follow the conversation when the user is already near the bottom.
        var sv = MessagesScroll;
        var nearBottom = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 160;
        if (nearBottom || sv.Extent.Height <= sv.Viewport.Height)
            Dispatcher.UIThread.Post(() => sv.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a line break (handled by the TextBox itself)
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm is not null)
        {
            e.Handled = true;
            if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
        }
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async void OnDeleteSpriteClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedSprite is not { } sprite || Owner is not { } owner) return;
        if (!await ConfirmDialog.AskAsync(owner, $"Excluir a versão {sprite.VersionLabel}?", _vm.DeleteSpritePrompt(sprite), "Excluir versão")) return;
        _vm.DeleteSprite(sprite);
    }

    private async void OnDeleteAssetClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not MediaFile asset || Owner is not { } owner) return;
        if (!await ConfirmDialog.AskAsync(owner, $"Remover o asset “{asset.Name}”?", _vm.DeleteAssetPrompt(asset), "Remover")) return;
        _vm.DeleteAsset(asset);
    }

    private async void OnSavePngClick(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedSprite is { } sprite) await SaveCopyAsync(sprite.FullPath, sprite.Title + ".png", "Salvar folha de sprites (PNG)");
    }

    private async void OnSaveGifClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MediaFile gif) await SaveCopyAsync(gif.FullPath, gif.Name, "Salvar GIF");
    }

    private async Task SaveCopyAsync(string source, string suggested, string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _vm is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            DefaultExtension = System.IO.Path.GetExtension(source).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is not { } dest) return;
        await _vm.ExportFileAsync(source, dest);
    }

    private void OnModelPicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not ModelChoice choice) return;
        _vm.SelectModel(choice);
        ModelButton.Flyout?.Hide();
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _vm is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Anexar ao pedido",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Imagem, sprite, vídeo, PDF ou texto")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg", "*.aseprite", "*.ase",
                                "*.mp4", "*.mov", "*.webm", "*.mkv", "*.avi", "*.m4v",
                                "*.pdf", "*.txt", "*.md", "*.json", "*.csv"],
                },
                FilePickerFileTypes.All,
            ],
        });
        _vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        InputBox.Focus();
    }

    private void OnDropReference(object? sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetFiles() is not { } files) return;
        _vm.AddAttachments(files.Select(f => f.TryGetLocalPath()).OfType<string>().Where(File.Exists));
        e.Handled = true;
    }

    /// <summary>Exports PNG + JSON + GIFs of the selected version into a folder (named after the sprite).</summary>
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || _vm?.SelectedSprite is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Exportar “{_vm.SuggestedExportName}” (PNG + JSON + GIFs) para a pasta…",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } dest) return;
        await _vm.ExportSelectedAsync(dest, withGifs: true);
    }

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Sheet is not { } sheet || _vm.SelectedSprite is not { } sprite || Owner is not { } owner) return;
        await new PreviewWindow(sheet, sprite.Name, _vm.SelectedAnimation).ShowDialog(owner);
    }
}
