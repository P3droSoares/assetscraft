using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AssetsCraft.Desktop.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string title, string body, string confirmText) : this()
    {
        TitleText.Text = title;
        BodyText.Text = body;
        ConfirmButton.Content = confirmText;
    }

    public static Task<bool> AskAsync(Window owner, string title, string body, string confirmText) =>
        new ConfirmDialog(title, body, confirmText).ShowDialog<bool>(owner);

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
