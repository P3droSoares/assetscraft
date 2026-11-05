using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AssetsCraft.Core.Sprites;
using AssetsCraft.Desktop.ViewModels;

namespace AssetsCraft.Desktop.Views;

/// <summary>Chooses the sprite (latest version) a new animation conversation starts from.</summary>
public partial class BasePickerDialog : Window
{
    public BasePickerDialog() => InitializeComponent();

    public BasePickerDialog(IReadOnlyList<BaseCandidate> candidates) : this()
    {
        List.ItemsSource = candidates;
        List.SelectedIndex = candidates.Count > 0 ? 0 : -1;
    }

    private BaseCandidate? Selected => List.SelectedItem as BaseCandidate;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = Selected is not null;
        Preview.Sheet = Selected is { } c ? SpriteSheet.Load(c.SheetFull) : null;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Selected is { } c) Close(c);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(Selected);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
