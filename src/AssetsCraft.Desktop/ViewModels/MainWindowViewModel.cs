using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;
using AssetsCraft.Core.Sessions;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>One option of "Novo asset" (Sprite, Tiles; SFX/Música/Cutscene "em breve").</summary>
public sealed record NewAssetChoice(AssetKindInfo Info)
{
    public AssetKind Kind => Info.Kind;
    public string Label => Info.Kind == AssetKind.Sprite ? "Sprite" : Info.Label;
    public string Hint => Info.Kind switch
    {
        AssetKind.Sprite => "Desenha a pose parada; depois você anima no mesmo lugar.",
        AssetKind.Tile => "Tiles estáticos que encaixam entre si.",
        _ => "Em breve",
    };
    public bool IsAvailable => Info.Available;
}

/// <summary>
/// Main window. Everything happens inside a linked game project (MonoGame or any folder). The sidebar lists
/// its assets; each asset goes through one linear flow (Descrever → Refinar → Animar → Enviar) shown by
/// <see cref="AssetViewModel"/>. Under the hood an asset is one or more conversations (SpriteProject in Core):
/// the sprite itself plus one animation conversation per base version.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ProjectStore _store;
    private readonly GameStore _games;
    private readonly SessionManager _sessions;
    private readonly AssetsCraftOptions _options;
    private readonly Dictionary<string, ProjectViewModel> _cache = [];
    private readonly Dictionary<AssetItem, AssetViewModel> _assetCache = [];
    private readonly List<AssetItem> _allAssets = [];

    public MainWindowViewModel(ProjectStore store, GameStore games, SessionManager sessions, AssetsCraftOptions options)
    {
        _store = store;
        _games = games;
        _sessions = sessions;
        _options = options;
        Usage = new UsageViewModel(sessions.Usage);

        foreach (var k in AssetKinds.All.Where(k => k.Kind != AssetKind.Animation)) NewChoices.Add(new NewAssetChoice(k));
        BuildAssets(store.All());
        RefreshGames();
        ActiveGame = games.Active;
    }

    public ObservableCollection<AssetItem> Assets { get; } = [];
    public ObservableCollection<GameProject> Games { get; } = [];
    public ObservableCollection<NewAssetChoice> NewChoices { get; } = [];
    public UsageViewModel Usage { get; }

    [ObservableProperty] private AssetItem? _selectedAsset;
    [ObservableProperty] private AssetViewModel? _current;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private string _totalSizeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(HasNoGame), nameof(GameName), nameof(GameInfo))]
    private GameProject? _activeGame;

    public bool HasGame => ActiveGame is not null;
    public bool HasNoGame => ActiveGame is null;
    public string GameName => ActiveGame?.Name ?? "Nenhum projeto";
    public string GameInfo => ActiveGame is null ? "" : (ActiveGame.IsMonoGame ? "MonoGame · " : "") + ActiveGame.Path;
    public bool HasAssets => Assets.Count > 0;
    public bool ShowWelcome => HasGame && Assets.Count == 0;

    /// <summary>Set by the view: shows a confirmation dialog and returns the user's choice.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the view: folder picker for linking a game project.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised so the view can focus the inline rename box.</summary>
    public event Action<AssetItem>? RenameStarted;

    /// <summary>Raised so the view focuses the message box of the current conversation.</summary>
    public event Action? FocusComposerRequested;

    public void RequestComposerFocus() => FocusComposerRequested?.Invoke();

    // ------------------------------------------------------------------ assets

    /// <summary>Folds animation conversations into the sprite they animate; orphans become their own asset.</summary>
    private void BuildAssets(IEnumerable<SpriteProject> projects)
    {
        var list = projects.ToList();
        var roots = new Dictionary<string, AssetItem>();
        foreach (var p in list.Where(p => p.Kind != AssetKind.Animation || list.All(b => b.Id != p.BaseConversationId)))
        {
            var asset = new AssetItem(Track(new ConversationItem(p)), this);
            roots[p.Id] = asset;
            _allAssets.Add(asset);
        }
        foreach (var p in list.Where(p => p.Kind == AssetKind.Animation && p.BaseConversationId is { } b && roots.ContainsKey(b)))
            roots[p.BaseConversationId!].AddAnimation(Track(new ConversationItem(p)));
        foreach (var a in _allAssets) a.RefreshState();
    }

    private ConversationItem Track(ConversationItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationItem.SizeBytes)) UpdateTotal();
        };
        _ = item.RefreshSizeAsync();
        return item;
    }

    /// <summary>The sidebar shows the active game's assets, most recently touched first.</summary>
    private void RefreshAssets()
    {
        var keep = SelectedAsset;
        var gameId = ActiveGame?.Id;
        Assets.Clear();
        foreach (var a in _allAssets.Where(a => gameId is not null && a.Project.GameId == gameId).OrderByDescending(a => a.UpdatedAt))
            Assets.Add(a);
        SelectedAsset = keep is not null && Assets.Contains(keep) ? keep : Assets.FirstOrDefault();
        OnPropertyChanged(nameof(HasAssets));
        OnPropertyChanged(nameof(ShowWelcome));
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var total = Assets.Sum(a => a.SizeBytes);
        TotalSizeText = Assets.Count == 0 ? "" : PendingAttachment.FormatSize(total);
    }

    partial void OnSelectedAssetChanged(AssetItem? value)
    {
        if (value is null)
        {
            Current = null;
            return;
        }
        if (!_assetCache.TryGetValue(value, out var vm))
        {
            vm = new AssetViewModel(value, this);
            _assetCache[value] = vm;
        }
        else vm.RefreshSteps();
        Current = vm;
    }

    /// <summary>Conversation view model, created once per conversation and kept while the app runs.</summary>
    public ProjectViewModel ViewModelFor(ConversationItem item, IAssetFlow flow)
    {
        if (!_cache.TryGetValue(item.Project.Id, out var vm))
        {
            vm = new ProjectViewModel(_sessions.Get(item.Project), item, () => _games.Get(item.Project.GameId), flow);
            _cache[item.Project.Id] = vm;
        }
        return vm;
    }

    // ------------------------------------------------------------------ game project

    private void RefreshGames()
    {
        Games.Clear();
        foreach (var g in _games.All()) Games.Add(g);
    }

    partial void OnActiveGameChanged(GameProject? value)
    {
        if (value is not null)
        {
            _games.SetActive(value);
            // conversations from before projects existed join the first project linked
            foreach (var c in _allAssets.SelectMany(a => a.Conversations).Where(c => c.Project.GameId is null))
            {
                c.Project.GameId = value.Id;
                _store.Save(c.Project);
            }
        }
        RefreshAssets();
    }

    [RelayCommand]
    private async Task LinkGame()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (folder is null) return;
        try
        {
            var game = _games.Link(folder);
            RefreshGames();
            ActiveGame = Games.FirstOrDefault(g => g.Id == game.Id);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = $"Não foi possível vincular: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SelectGame(GameProject game) => ActiveGame = game;

    [RelayCommand]
    private async Task UnlinkGame()
    {
        if (ActiveGame is not { } game || Confirm is null) return;
        if (!await Confirm($"Desvincular “{game.Name}”?",
                "O projeto do jogo não é alterado e os assets dele continuam no app; eles voltam a aparecer quando você vincular a mesma pasta de novo.",
                "Desvincular")) return;
        _games.Unlink(game);
        RefreshGames();
        ActiveGame = _games.Active;
    }

    // ------------------------------------------------------------------ rename

    public void BeginRename(AssetItem asset)
    {
        foreach (var other in Assets.Where(a => a.IsRenaming && a != asset)) other.IsRenaming = false;
        SelectedAsset = asset;
        asset.StartEditing();
        RenameStarted?.Invoke(asset);
    }

    public void CommitRename(AssetItem asset)
    {
        if (!asset.IsRenaming) return;
        asset.IsRenaming = false;
        var name = asset.EditName.Trim();
        if (name.Length == 0 || name == asset.Name) return;
        if (name.Length > 80) name = name[..80].TrimEnd();
        _store.Rename(asset.Project, name);
        asset.Root.NotifyRenamed();
        if (_cache.TryGetValue(asset.Project.Id, out var vm)) vm.NotifyRenamed();
        if (_assetCache.TryGetValue(asset, out var avm)) avm.NotifyRenamed();
    }

    public void CancelRename(AssetItem asset) => asset.IsRenaming = false;

    // ------------------------------------------------------------------ create

    /// <summary>
    /// Starts a new asset of that type in the active game. No name is asked: it begins as "Nova conversa" and is
    /// titled from the first request. An untouched empty asset of the same type is reused instead of piling up blanks.
    /// </summary>
    [RelayCommand]
    private async Task NewAsset(NewAssetChoice? choice)
    {
        choice ??= NewChoices[0];
        Error = null;
        if (!choice.IsAvailable) return;
        if (ActiveGame is not { } game)
        {
            await LinkGame();
            if (ActiveGame is null) return;
            game = ActiveGame;
        }

        var empty = Assets.FirstOrDefault(a =>
            a.Kind == choice.Kind && a.Project.AutoName && a.Animations.Count == 0
            && a.Name == AssetsCraft.Core.Claude.ConversationTitler.DefaultName
            && !_sessions.Get(a.Project).Messages.Any(m => m.Role == ChatRole.User));
        if (empty is not null)
        {
            SelectedAsset = empty;
            FocusComposerRequested?.Invoke();
            return;
        }

        try
        {
            var project = await Task.Run(() => _store.Create(gameId: game.Id, kind: choice.Kind));
            var asset = new AssetItem(Track(new ConversationItem(project)), this);
            asset.RefreshState();
            _allAssets.Add(asset);
            RefreshAssets();
            SelectedAsset = asset;
            FocusComposerRequested?.Invoke();
            InstallInBackground(project);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Animation conversation of a sprite, with that version as the fixed base.</summary>
    public async Task<ConversationItem?> CreateAnimationAsync(AssetItem asset, string sheetFull)
    {
        try
        {
            var project = await Task.Run(() => _store.CreateAnimation(asset.Project, sheetFull));
            var item = Track(new ConversationItem(project));
            asset.AddAnimation(item);
            InstallInBackground(project);
            return item;
        }
        catch (Exception ex)
        {
            Error = $"Não foi possível começar a animação: {ex.Message}";
            return null;
        }
    }

    private void InstallInBackground(SpriteProject project)
    {
        var session = _sessions.Get(project);
        if (_options.AutoInstallDependencies) _ = Task.Run(session.InstallDependenciesAsync);
    }

    // ------------------------------------------------------------------ delete

    /// <summary>
    /// Deletes the asset after confirmation: stops its processes and removes every file of the sprite and of
    /// its animations (workspaces, versions, materials, references, chats, Claude Code session files). What was
    /// already sent to the game project stays there.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsset(AssetItem? asset)
    {
        asset ??= SelectedAsset;
        if (asset is null || IsDeleting) return;

        // Never delete without an explicit confirmation.
        if (Confirm is null) return;
        var extra = asset.Animations.Count == 0 ? ""
            : $" Isso inclui {asset.Animations.Count} {(asset.Animations.Count == 1 ? "conjunto" : "conjuntos")} de animações.";
        var ok = await Confirm(
            $"Excluir “{asset.Name}”?",
            $"Tudo deste asset ({PendingAttachment.FormatSize(asset.SizeBytes)}) será apagado do computador: versões, animações, arquivos anexados, " +
            $"conversa e arquivos internos do Claude.{extra} O que já foi enviado ao projeto do jogo continua lá. Isso não pode ser desfeito.",
            "Excluir asset");
        if (!ok) return;

        IsDeleting = true;
        Error = null;
        try
        {
            var index = Assets.IndexOf(asset);
            if (SelectedAsset == asset)
                SelectedAsset = Assets.Where(a => a != asset).ElementAtOrDefault(Math.Max(0, index - 1));
            _assetCache.Remove(asset);
            _allAssets.Remove(asset);
            RefreshAssets();
            await Task.Delay(150);

            var failed = new List<string>();
            foreach (var c in asset.Conversations.ToList())
            {
                if (_cache.Remove(c.Project.Id, out var vm)) vm.Dispose();
                var report = await _sessions.DeleteProjectAsync(c.Project);
                if (!report.Success) failed.AddRange(report.Failed);
            }
            if (failed.Count > 0)
                Error = "Alguns arquivos não puderam ser apagados agora:\n" + string.Join("\n", failed.Take(5));
        }
        catch (Exception ex)
        {
            Error = $"Falha ao excluir: {ex.Message}";
        }
        finally
        {
            IsDeleting = false;
        }
    }
}
