using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AssetsCraft.Desktop.ViewModels;

namespace AssetsCraft.Desktop.Views;

/// <summary>Settings of the linked game project (palette).</summary>
public partial class GameSettingsDialog : Window
{
    public GameSettingsDialog() => InitializeComponent();

    public GameSettingsDialog(GameSettingsViewModel vm) : this() => DataContext = vm;

    private GameSettingsViewModel? Vm => DataContext as GameSettingsViewModel;

    private async void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Paleta de cores do projeto",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Paleta (imagem, .hex, .gpl, .pal, .txt)")
                {
                    Patterns = ["*.png", "*.gif", "*.bmp", "*.hex", "*.gpl", "*.pal", "*.txt", "*.json"],
                },
                FilePickerFileTypes.All,
            ],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ImportAsync(path);
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!await ConfirmDialog.AskAsync(this, "Remover a paleta?",
                "Os próximos sprites deste projeto voltam a ter cores livres. O que já foi criado não muda.", "Remover"))
            return;
        vm.Remove();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
