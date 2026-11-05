using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using AssetsCraft.Core.Projects;
using AssetsCraft.Core.Sessions;
using AssetsCraft.Desktop.ViewModels;
using AssetsCraft.Desktop.Views;

namespace AssetsCraft.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Every child process (Claude, ffmpeg, npm…) is tracked and killed with the app — see ChildProcesses.
        AssetsCraft.Core.ChildProcesses.Install();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = LoadOptions();
            var store = new ProjectStore(Options.Create(options));
            var sessions = new SessionManager(store, Options.Create(options));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(store, new GameStore(store.Root), sessions, options),
            };

            // Stop Claude Code / node child processes when the app closes.
            desktop.ShutdownRequested += (_, _) =>
            {
                try { sessions.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }
                AssetsCraft.Core.ChildProcesses.KillAll(); // whatever is still alive
            };
            desktop.Exit += (_, _) => AssetsCraft.Core.ChildProcesses.KillAll();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>appsettings.json next to the executable, overridable by settings.json in the app data folder.</summary>
    private static AssetsCraftOptions LoadOptions()
    {
        var userSettings = Path.Combine(AssetsCraftOptions.DefaultDataRoot, "settings.json");
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(userSettings, optional: true)
            .Build();

        var options = new AssetsCraftOptions();
        config.GetSection("AssetsCraft").Bind(options);
        return options;
    }
}
