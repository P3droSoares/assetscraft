using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;
using AssetsCraft.Core.Sessions;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>One entry of the asset-type menu (Sprites, Tiles, Animação…).</summary>
public sealed partial class KindNavItem(AssetKindInfo info) : ObservableObject
{
    public AssetKindInfo Info { get; } = info;
    public AssetKind Kind => Info.Kind;
    public string Label => Info.Label;
    public bool IsAvailable => Info.Available;
    public string Hint => Info.Hint;
    [ObservableProperty] private int _count;
    public string CountText => IsAvailable ? Count.ToString() : "em breve";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));
}

/// <summary>A sprite that can be the base of a new animation: its latest version.</summary>
public sealed record BaseCandidate(ConversationItem Item, string SheetFull)
{
    public string Title => ProjectStore.SpriteTitle(SheetFull);
    public string Info => $"{System.IO.Path.GetFileName(SheetFull)} · {Item.Name}";
}

/// <summary>
/// Main window. Everything happens inside a linked game project (MonoGame or any folder): the sidebar shows
/// its asset types, and each type lists its conversations. Each conversation is one internal workspace
/// (SpriteProject in Core) with its own chat, versions, assets and references.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ProjectStore _store;
    private readonly GameStore _games;
    private readonly SessionManager _sessions;
    private readonly AssetsCraftOptions _options;
    private readonly Dictionary<string, ProjectViewModel> _cache = [];
    private readonly List<ConversationItem> _all = [];

    public MainWindowViewModel(ProjectStore store, GameStore games, SessionManager sessions, AssetsCraftOptions options)
    {
        _store = store;
        _games = games;
        _sessions = sessions;
        _options = options;
        Usage = new UsageViewModel(sessions.Usage);

        foreach (var k in AssetKinds.All) Kinds.Add(new KindNavItem(k));
        SelectedKind = Kinds[0];
        foreach (var p in store.All()) AddItem(new ConversationItem(p, this));
        RefreshGames();
        ActiveGame = games.Active;
        foreach (var item in _all) _ = item.RefreshSizeAsync();
    }

    public ObservableCollection<ConversationItem> Conversations { get; } = [];
    public ObservableCollection<GameProject> Games { get; } = [];
    public ObservableCollection<KindNavItem> Kinds { get; } = [];
    public UsageViewModel Usage { get; }

    [ObservableProperty] private ConversationItem? _selectedConversation;
    [ObservableProperty] private ProjectViewModel? _current;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isDeleting;
    [ObservableProperty] private string _totalSizeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGame), nameof(HasNoGame), nameof(GameName), nameof(GameInfo))]
    private GameProject? _activeGame;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewLabel), nameof(KindHint), nameof(KindAvailable), nameof(EmptyKindText))]
    private KindNavItem _selectedKind = null!;

    public bool HasGame => ActiveGame is not null;
    public bool HasNoGame => ActiveGame is null;
    public string GameName => ActiveGame?.Name ?? "Nenhum projeto";
    public string GameInfo => ActiveGame is null ? "" : (ActiveGame.IsMonoGame ? "MonoGame · " : "") + ActiveGame.Path;
    public string NewLabel => SelectedKind.Info.NewLabel;
    public string KindHint => SelectedKind.Hint;
    public bool KindAvailable => SelectedKind.IsAvailable;
    public string EmptyKindText => SelectedKind.Kind == AssetKind.Animation
        ? "Nenhuma animação ainda. Clique em “Nova animação” e escolha um sprite já criado como base."
        : $"Nenhum {SelectedKind.Info.Singular} ainda. Clique em “{NewLabel}” e descreva o que quer.";
    public bool HasNoConversations => Conversations.Count == 0;

    /// <summary>Set by the view: shows a confirmation dialog and returns the user's choice.</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Set by the view: folder picker for linking a game project.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Set by the view: lets the user choose the base sprite of a new animation.</summary>
    public Func<IReadOnlyList<BaseCandidate>, Task<BaseCandidate?>>? PickBaseSprite { get; set; }

    /// <summary>Raised so the view can focus the inline rename box.</summary>
    public event Action<ConversationItem>? RenameStarted;

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
            foreach (var item in _all.Where(i => i.Project.GameId is null))
            {
                item.Project.GameId = value.Id;
                _store.Save(item.Project);
            }
        }
        RefreshConversations();
    }

    partial void OnSelectedKindChanged(KindNavItem value) => RefreshConversations();

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
    private void SelectKind(KindNavItem kind) => SelectedKind = kind;

    [RelayCommand]
    private async Task UnlinkGame()
    {
        if (ActiveGame is not { } game || Confirm is null) return;
        if (!await Confirm($"Desvincular “{game.Name}”?",
                "O projeto do jogo não é alterado e as conversas dele continuam no app; elas voltam a aparecer quando você vincular a mesma pasta de novo.",
                "Desvincular")) return;
        _games.Unlink(game);
        RefreshGames();
        ActiveGame = _games.Active;
    }

    /// <summary>The conversation list shows only the active game's conversations of the selected type.</summary>
    private void RefreshConversations()
    {
        var keep = SelectedConversation;
        var gameId = ActiveGame?.Id;
        Conversations.Clear();
        foreach (var item in _all.Where(i => gameId is not null && i.Project.GameId == gameId && i.Project.Kind == SelectedKind.Kind)
                     .OrderByDescending(i => i.Project.UpdatedAt))
            Conversations.Add(item);
        foreach (var k in Kinds)
            k.Count = _all.Count(i => gameId is not null && i.Project.GameId == gameId && i.Project.Kind == k.Kind);
        SelectedConversation = Conversations.Contains(keep!) ? keep : Conversations.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoConversations));
        UpdateTotal();
    }

    // ------------------------------------------------------------------ rename

    public void BeginRename(ConversationItem item)
    {
        foreach (var other in Conversations.Where(c => c.IsRenaming && c != item)) other.IsRenaming = false;
        SelectedConversation = item;
        item.StartEditing();
        RenameStarted?.Invoke(item);
    }

    public void CommitRename(ConversationItem item)
    {
        if (!item.IsRenaming) return;
        item.IsRenaming = false;
        var name = item.EditName.Trim();
        if (name.Length == 0 || name == item.Name) return;
        if (name.Length > 80) name = name[..80].TrimEnd();
        _store.Rename(item.Project, name);
        item.NotifyRenamed();
        if (_cache.TryGetValue(item.Project.Id, out var vm)) vm.NotifyRenamed();
    }

    public void CancelRename(ConversationItem item) => item.IsRenaming = false;

    partial void OnSelectedConversationChanged(ConversationItem? value)
    {
        if (value is null)
        {
            Current = null;
            return;
        }
        if (!_cache.TryGetValue(value.Project.Id, out var vm))
        {
            vm = new ProjectViewModel(_sessions.Get(value.Project), value, () => _games.Get(value.Project.GameId), AnimateFromAsync);
            _cache[value.Project.Id] = vm;
        }
        Current = vm;
    }

    private void AddItem(ConversationItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationItem.SizeBytes)) UpdateTotal();
        };
        _all.Add(item);
    }

    private void UpdateTotal()
    {
        var total = Conversations.Sum(c => c.SizeBytes);
        TotalSizeText = Conversations.Count == 0 ? "" : PendingAttachment.FormatSize(total);
    }

    // ------------------------------------------------------------------ create

    /// <summary>
    /// Starts a conversation of the selected type in the active game. No name is asked: it begins as
    /// "Nova conversa" and is titled automatically from the first request. An untouched empty conversation of
    /// the same type is reused instead of piling up blanks. Animations first ask for the base sprite.
    /// </summary>
    [RelayCommand]
    private async Task CreateConversation()
    {
        Error = null;
        if (ActiveGame is not { } game)
        {
            await LinkGame();
            if (ActiveGame is null) return;
            game = ActiveGame;
        }
        if (!SelectedKind.IsAvailable) return;

        if (SelectedKind.Kind == AssetKind.Animation)
        {
            await CreateAnimationAsync(null);
            return;
        }

        var empty = Conversations.FirstOrDefault(c =>
            c.Project.AutoName
            && c.Name == AssetsCraft.Core.Claude.ConversationTitler.DefaultName
            && !_sessions.Get(c.Project).Messages.Any(m => m.Role == ChatRole.User));
        if (empty is not null)
        {
            SelectedConversation = empty;
            FocusComposerRequested?.Invoke();
            return;
        }

        try
        {
            var kind = SelectedKind.Kind;
            var project = await Task.Run(() => _store.Create(gameId: game.Id, kind: kind));
            Open(project);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>"Animar" on a sprite: new animation conversation with that sprite (latest version) as base.</summary>
    private Task AnimateFromAsync(ProjectViewModel sprite) =>
        CreateAnimationAsync(_all.FirstOrDefault(i => i.Project.Id == sprite.Project.Id));

    private async Task CreateAnimationAsync(ConversationItem? from)
    {
        var candidates = _all
            .Where(i => i.Project.GameId == ActiveGame?.Id && i.Project.Kind == AssetKind.Sprite)
            .Select(i => (item: i, sheet: _sessions.Get(i.Project).ListSprites().FirstOrDefault()))
            .Where(x => x.sheet is not null)
            .Select(x => new BaseCandidate(x.item, x.sheet!.FullName))
            .ToList();

        BaseCandidate? pick = from is null ? null : candidates.FirstOrDefault(c => c.Item == from);
        if (pick is null)
        {
            if (candidates.Count == 0)
            {
                Error = "Para animar, crie antes um sprite em Sprites (o idle). A animação parte dele.";
                return;
            }
            if (PickBaseSprite is null) return;
            pick = await PickBaseSprite(candidates);
            if (pick is null) return;
        }

        try
        {
            var project = await Task.Run(() => _store.CreateAnimation(pick.Item.Project, pick.SheetFull));
            SelectedKind = Kinds.First(k => k.Kind == AssetKind.Animation);
            Open(project);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void Open(SpriteProject project)
    {
        var item = new ConversationItem(project, this);
        AddItem(item);
        RefreshConversations();
        SelectedConversation = item;
        _ = item.RefreshSizeAsync();
        FocusComposerRequested?.Invoke();

        var session = _sessions.Get(project);
        if (_options.AutoInstallDependencies) _ = Task.Run(session.InstallDependenciesAsync);
    }

    /// <summary>Raised so the view focuses the message box of the current conversation.</summary>
    public event Action? FocusComposerRequested;

    // ------------------------------------------------------------------ delete

    /// <summary>
    /// Deletes the conversation after confirmation: stops its processes and removes every file it created
    /// (workspace, sprites, assets, references, chat, Claude Code session files). What was already sent to
    /// the game project stays there.
    /// </summary>
    [RelayCommand]
    private async Task DeleteConversation(ConversationItem? item)
    {
        item ??= SelectedConversation;
        if (item is null || IsDeleting) return;

        // Never delete without an explicit confirmation.
        if (Confirm is null) return;
        var ok = await Confirm(
            $"Excluir a conversa “{item.Name}”?",
            $"Tudo desta conversa ({item.SizeText}) será apagado do computador: versões geradas, assets, referências, " +
            "histórico e arquivos internos do Claude. O que já foi enviado ao projeto do jogo continua lá. Isso não pode ser desfeito.",
            "Excluir conversa");
        if (!ok) return;

        IsDeleting = true;
        Error = null;
        try
        {
            var index = Conversations.IndexOf(item);
            if (SelectedConversation == item)
                SelectedConversation = Conversations.Where(c => c != item).ElementAtOrDefault(Math.Max(0, index - 1));
            if (_cache.Remove(item.Project.Id, out var vm)) vm.Dispose();
            _all.Remove(item);
            RefreshConversations();
            await Task.Delay(150);

            var report = await _sessions.DeleteProjectAsync(item.Project);
            if (!report.Success)
                Error = "Alguns arquivos não puderam ser apagados agora:\n" + string.Join("\n", report.Failed.Take(5));
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
