using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AssetsCraft.Desktop.ViewModels;

namespace AssetsCraft.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        // debug only (ASSETSCRAFT_DEBUG_PLAYER=<dir>): render this window to a PNG — no screen capture involved
        if (Environment.GetEnvironmentVariable("ASSETSCRAFT_DEBUG_PLAYER") is { Length: > 0 } dir)
        {
            Opened += async (_, _) =>
            {
                if (Environment.GetEnvironmentVariable("ASSETSCRAFT_DEBUG_TALL") is not null) Height = 1900;
                await System.Threading.Tasks.Task.Delay(8000);
                if (Environment.GetEnvironmentVariable("ASSETSCRAFT_DEBUG_TALL") is not null)
                {
                    foreach (var sv in this.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.Name == "SideScroll")) sv.ScrollToEnd();
                    await System.Threading.Tasks.Task.Delay(500);
                }
                var size = new Avalonia.PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                rtb.Render(this);
                rtb.Save(System.IO.Path.Combine(dir, "window.png"));
            };
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.RenameStarted -= FocusRenameBox;
            _vm.FocusComposerRequested -= FocusComposer;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is null) return;
        _vm.Confirm = (title, body, confirm) => ConfirmDialog.AskAsync(this, title, body, confirm);
        _vm.PickFolder = PickGameFolderAsync;
        _vm.RenameStarted += FocusRenameBox;
        _vm.FocusComposerRequested += FocusComposer;
    }

    private async Task<string?> PickGameFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Pasta do projeto do jogo (ex.: a pasta do .csproj MonoGame)",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private void OnGamePicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not AssetsCraft.Core.Projects.GameProject game) return;
        GameButton.Flyout?.Hide();
        _vm.SelectGameCommand.Execute(game);
    }

    private void OnLinkGame(object? sender, RoutedEventArgs e)
    {
        GameButton.Flyout?.Hide();
        _vm?.LinkGameCommand.Execute(null);
    }

    private void OnUnlinkGame(object? sender, RoutedEventArgs e)
    {
        GameButton.Flyout?.Hide();
        _vm?.UnlinkGameCommand.Execute(null);
    }

    private void FocusComposer() => Dispatcher.UIThread.Post(() =>
        this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "InputBox")?.Focus(),
        DispatcherPriority.Background);

    private void OnNewPicked(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not NewAssetChoice choice) return;
        NewButton.Flyout?.Hide();
        _vm.NewAssetCommand.Execute(choice);
    }

    /// <summary>Focuses and selects the inline rename box of the asset being renamed.</summary>
    private void FocusRenameBox(AssetItem item)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(t => t.Classes.Contains("rename") && t.DataContext == item);
            if (box is null) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not AssetItem item) return;
        if (e.Key == Key.Enter)
        {
            _vm.CommitRename(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CancelRename(item);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && (sender as Control)?.DataContext is AssetItem item) _vm.CommitRename(item);
    }
}
