using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;
using AssetsCraft.Core.Sessions;
using AssetsCraft.Core.Sprites;

namespace AssetsCraft.Desktop.ViewModels;

public sealed record MediaFile(string FullPath, string Name, string Info, bool IsLatest = false, bool IsSent = false)
{
    public string VersionLabel => ProjectViewModel.VersionOf(Name);
    public string Title => ProjectViewModel.TitleOf(Name);
}

public sealed record BackdropChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ProjectViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<AssetKind, string[]> SuggestionsByKind = new()
    {
        [AssetKind.Sprite] =
        [
            "Herói cavaleiro 32x32 em pixel art, pose idle de frente",
            "Slime verde 16x16, paleta sweetie16",
            "Baú de tesouro 16x16 fechado",
            "Inimigo morcego 16x16, asas abertas",
        ],
        [AssetKind.Tile] =
        [
            "Tileset de grama e terra 16x16 que encaixa nas bordas (8 tiles)",
            "Tiles de parede de masmorra em pedra 16x16",
            "4 tiles de água com bordas de areia, 16x16",
        ],
        [AssetKind.Animation] =
        [
            "Andar para a direita, 6 quadros, 10 fps",
            "Pulo: preparação, subida, queda e aterrissagem",
            "Ataque com espada, 4 quadros, sem loop",
            "Respiração no idle: 4 quadros sutis",
        ],
    };

    public static readonly BackdropChoice[] Backdrops =
    [
        new("xadrez", "Xadrez"),
        new("escuro", "Escuro"),
        new("claro", "Claro"),
        new("verde", "Grama"),
    ];

    public static readonly int[] ZoomChoices = [0, 1, 2, 4, 6, 8, 12, 16];
    public static readonly double[] SpeedChoices = [0.25, 0.5, 1, 1.5, 2];

    private static readonly System.Text.RegularExpressions.Regex VersionRe =
        new(@"^(?<base>.+?)-v(?<n>\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly ProjectSession _session;
    private readonly Func<GameProject?> _game;
    private readonly Func<ProjectViewModel, Task>? _animate;
    private bool _syncQueued;
    private bool _wasBusy;

    /// <param name="game">Linked game project of this conversation (read each time: it can be re-linked).</param>
    /// <param name="animate">Opens a new animation conversation using this sprite as base.</param>
    public ProjectViewModel(ProjectSession session, ConversationItem item, Func<GameProject?> game, Func<ProjectViewModel, Task>? animate = null)
    {
        _session = session;
        _game = game;
        _animate = animate;
        Item = item;
        _session.Changed += OnSessionChanged;
        _session.Renamed += OnRenamed;
        PendingAttachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            SendCommand.NotifyCanExecuteChanged();
        };
        _wasBusy = session.IsBusy;
        // first open after an update (new shared packages) or an old private node_modules: fix it silently
        if (!session.DependenciesReady && !session.IsInstalling) _ = Task.Run(session.InstallDependenciesAsync);
        RefreshModels();
        Sync();
        RefreshFiles();
        SelectedSprite = Sprites.FirstOrDefault();
    }

    public SpriteProject Project => _session.Project;

    /// <summary>Sidebar entry (holds the conversation's size shown next to the title).</summary>
    public ConversationItem Item { get; }
    public ProjectSession Session => _session;
    public string Name => Project.Name;
    public void NotifyRenamed() => OnPropertyChanged(nameof(Name));
    public IReadOnlyList<string> SuggestionList => SuggestionsByKind.GetValueOrDefault(Project.Kind, []);

    // ------------------------------------------------------------------ asset type

    public AssetKindInfo KindInfo => AssetKinds.Get(Project.Kind);
    public bool IsSprite => Project.Kind == AssetKind.Sprite;
    public bool IsAnimation => Project.Kind == AssetKind.Animation;
    public string KindBadge => IsAnimation && Project.BaseSpriteName is { } b ? $"Animação · base {TitleOf(b)}" : KindInfo.Singular;
    public string EmptyTitle => Project.Kind switch
    {
        AssetKind.Animation => "Que animação vamos fazer?",
        AssetKind.Tile => "Que tiles vamos criar?",
        _ => "Que sprite vamos criar?",
    };
    public string EmptyText => Project.Kind switch
    {
        AssetKind.Animation => "O sprite base fica fixo. Descreva o movimento (andar, pular, atacar…), quantos quadros e a velocidade. " +
                               "Cada pedido acrescenta uma animação à folha; veja ao lado, quadro a quadro.",
        AssetKind.Tile => "Descreva o terreno ou a parede, o tamanho do tile e a paleta. O Claude desenha tiles estáticos que encaixam entre si.",
        _ => "Descreva o sprite (tamanho, estilo, paleta) e, se quiser, anexe referências — ou arraste-as para cá. " +
             "O Claude desenha só a pose idle, sem animação; depois, use “Animar” para criar as animações dele.",
    };
    public string ComposerHint => Project.Kind switch
    {
        AssetKind.Animation => "Descreva a animação… (Enter envia, Shift+Enter quebra linha)",
        AssetKind.Tile => "Descreva os tiles… (Enter envia, Shift+Enter quebra linha)",
        _ => "Descreva o sprite que você quer… (Enter envia, Shift+Enter quebra linha)",
    };

    // ------------------------------------------------------------------ game project

    public GameProject? Game => _game();
    public bool HasGame => Game is not null;

    /// <summary>Where the selected version would go: "Content/Sprites/heroi.png".</summary>
    public string SendTarget
    {
        get
        {
            if (Game is not { } g || SelectedSprite is null) return "";
            var name = IsAnimation && Project.BaseSpriteName is { } b ? TitleOf(b) : SelectedSprite.Title;
            return System.IO.Path.GetRelativePath(g.Path, ConversationFiles.SendDestination(g, Project.Kind, name)).Replace('\\', '/');
        }
    }

    public string SentInfo => Project.SentVersion is { } v && Game is { } g
        ? $"No projeto: {v} → {System.IO.Path.GetRelativePath(g.Path, Project.SentPath ?? "").Replace('\\', '/')}"
        : "Ainda não enviado ao projeto";

    /// <summary>The destination already has a PNG (another version, or edited in the game) that would be replaced.</summary>
    public bool SendWouldReplace => Game is { } g && SelectedSprite is not null && File.Exists(System.IO.Path.Combine(g.Path, SendTarget));

    public async Task SendSelectedToGameAsync()
    {
        if (Game is not { } game || SelectedSprite is not { } sprite) return;
        try
        {
            var dest = await Task.Run(() => _session.SendToGame(sprite.FullPath, game));
            ExportError = null;
            ExportInfo = $"Enviado ao projeto {game.Name}: {System.IO.Path.GetRelativePath(game.Path, dest).Replace('\\', '/')}";
            RefreshFiles();
            OnPropertyChanged(nameof(SentInfo));
        }
        catch (Exception ex)
        {
            ExportError = $"Não foi possível enviar: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Animate()
    {
        if (_animate is not null && IsSprite && Sprites.Count > 0) await _animate(this);
    }
    public IReadOnlyList<BackdropChoice> BackdropList => Backdrops;
    public IReadOnlyList<int> ZoomList => ZoomChoices;
    public IReadOnlyList<double> SpeedList => SpeedChoices;

    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<MediaFile> Sprites { get; } = [];
    public ObservableCollection<MediaFile> Assets { get; } = [];

    // ------------------------------------------------------------------ sprite viewer

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private MediaFile? _selectedSprite;

    /// <summary>Metadata of the selected version (frames + animations).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SheetSummary), nameof(HasManyAnimations))]
    private SpriteSheet? _sheet;

    public ObservableCollection<SpriteAnimation> Animations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameLabel), nameof(AnimationInfo))]
    private SpriteAnimation? _selectedAnimation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    private bool _isPlaying = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameLabel))]
    private int _position;

    [ObservableProperty] private int _zoom;
    [ObservableProperty] private double _speed = 1;
    [ObservableProperty] private BackdropChoice _backdrop = Backdrops[0];
    [ObservableProperty] private bool _showSheet;
    [ObservableProperty] private bool _showGrid;

    /// <summary>GIFs rendered for the selected version (out/gif/&lt;versão&gt;-&lt;animação&gt;.gif).</summary>
    public ObservableCollection<MediaFile> Gifs { get; } = [];
    public bool HasGifs => Gifs.Count > 0;

    public bool HasSelection => SelectedSprite is not null;
    public bool HasManyAnimations => Animations.Count > 1;
    public string SheetSummary => Sheet?.Summary ?? "";
    public string PlayLabel => IsPlaying ? "Pausar" : "Tocar";
    public string SelectedVersionLabel => SelectedSprite?.VersionLabel ?? "";

    public string FrameLabel
    {
        get
        {
            if (SelectedAnimation is not { } a || Sheet is null) return "";
            var seq = a.Sequence();
            var pos = Math.Clamp(Position, 0, Math.Max(0, seq.Count - 1));
            var frame = seq.Count > 0 ? Sheet.Frames[seq[pos]] : null;
            return $"quadro {pos + 1}/{seq.Count}" + (frame is null ? "" : $" · {frame.DurationMs} ms");
        }
    }

    public string AnimationInfo => SelectedAnimation is not { } a ? ""
        : $"{a.Count} {(a.Count == 1 ? "quadro" : "quadros")} · {a.Fps:0.#} fps" + (a.Loop ? " · loop" : " · uma vez") + (a.PingPong ? " · ping-pong" : "");

    partial void OnSelectedSpriteChanged(MediaFile? value)
    {
        OnPropertyChanged(nameof(SelectedVersionLabel));
        OnPropertyChanged(nameof(SendTarget));
        OnPropertyChanged(nameof(SentInfo));
        OnPropertyChanged(nameof(HasGame));
        LoadSheet();
    }

    partial void OnSelectedAnimationChanged(SpriteAnimation? value)
    {
        Position = 0;
        IsPlaying = true;
    }

    private void LoadSheet()
    {
        var keepAnim = SelectedAnimation?.Name;
        Sheet = SelectedSprite is null ? null : SpriteSheet.Load(SelectedSprite.FullPath);
        Replace(Animations, Sheet?.Animations ?? []);
        OnPropertyChanged(nameof(HasManyAnimations));
        SelectedAnimation = Animations.FirstOrDefault(a => a.Name == keepAnim) ?? Animations.FirstOrDefault();
        Replace(Gifs, SelectedSprite is null ? [] : ConversationFiles.Gifs(SelectedSprite.FullPath).Select(g =>
            new MediaFile(g, System.IO.Path.GetFileName(g), Size(new FileInfo(g).Length))));
        OnPropertyChanged(nameof(HasGifs));
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!IsPlaying && SelectedAnimation is { Loop: false } a && Position >= a.Sequence().Count - 1) Position = 0;
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    private void StepFrame(string? delta)
    {
        if (SelectedAnimation is not { } a) return;
        IsPlaying = false;
        var count = a.Sequence().Count;
        var d = int.TryParse(delta, out var n) ? n : 1;
        Position = ((Position + d) % count + count) % count;
    }

    [RelayCommand]
    private void SelectAnimationByIndex(int index)
    {
        if (index >= 0 && index < Animations.Count) SelectedAnimation = Animations[index];
    }

    /// <summary>"heroi-v3.png" → "v3" (or the name without extension when not versioned).</summary>
    public static string VersionOf(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var m = VersionRe.Match(stem);
        return m.Success ? "v" + m.Groups["n"].Value : stem;
    }

    /// <summary>"heroi-v3.png" → "heroi".</summary>
    public static string TitleOf(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var m = VersionRe.Match(stem);
        return m.Success ? m.Groups["base"].Value : stem;
    }

    public string DeleteSpritePrompt(MediaFile sprite) =>
        $"A versão “{sprite.Name}” será apagada do computador, junto com os metadados (.json), os GIFs e a pré-visualização dela. " +
        (sprite.IsLatest ? "Atenção: é a versão mais recente; a anterior passa a ser a atual. " : "É uma versão antiga; a atual não muda. ") +
        "O arquivo-fonte do sprite continua, então o Claude pode gerar de novo. Isso não pode ser desfeito.";

    public void DeleteSprite(MediaFile sprite)
    {
        ConversationFiles.DeleteSprite(Project, Rel(sprite.FullPath));
        if (SelectedSprite?.FullPath == sprite.FullPath) SelectedSprite = null;
        RefreshFiles();
    }

    public string DeleteAssetPrompt(MediaFile asset) =>
        $"O asset “{asset.Name}” será apagado desta conversa. O arquivo original no seu computador não muda. " +
        "Sprites que já foram gerados com ele continuam iguais.";

    public void DeleteAsset(MediaFile asset)
    {
        ConversationFiles.DeleteAsset(Project, Rel(asset.FullPath));
        RefreshFiles();
    }

    private string Rel(string full) => System.IO.Path.GetRelativePath(Project.Path, full).Replace('\\', '/');

    /// <summary>Reference files that will be sent with the next message.</summary>
    public ObservableCollection<PendingAttachment> PendingAttachments { get; } = [];
    public bool HasAttachments => PendingAttachments.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isInstalling;

    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _hasNodeModules;

    public bool IsEmpty => Messages.Count == 0;

    // ------------------------------------------------------------------ model picker

    public ObservableCollection<ModelChoice> ModelChoices { get; } = [];

    [ObservableProperty] private string _modelLabel = "";

    private void RefreshModels()
    {
        var current = _session.Model;
        ModelChoices.Clear();
        foreach (var m in AssetsCraft.Core.Claude.ModelCatalog.All)
            ModelChoices.Add(new ModelChoice(m, string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase)));
        var opt = AssetsCraft.Core.Claude.ModelCatalog.Find(current);
        var active = AssetsCraft.Core.Claude.ModelCatalog.ShortName(_session.ActiveModel);
        ModelLabel = current is null && active.Length > 0 ? $"Padrão · {active}" : opt.Label;
    }

    public void SelectModel(ModelChoice choice)
    {
        if (IsBusy) return;
        try
        {
            _session.SetModel(choice.Option.Id);
        }
        catch (InvalidOperationException)
        {
            return; // busy
        }
        RefreshModels();
    }
    public bool HasNoSprites => Sprites.Count == 0;
    public bool HasNoAssets => Assets.Count == 0;

    /// <summary>Raised after messages change so the view can auto-scroll.</summary>
    public event Action? MessagesUpdated;

    // ------------------------------------------------------------------ commands

    private bool CanSend() => !IsBusy && !IsInstalling && (!string.IsNullOrWhiteSpace(Input) || HasAttachments);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Input;
        var files = PendingAttachments.Select(a => a.Path).ToList();
        Input = "";
        PendingAttachments.Clear();
        _wasBusy = true;
        IsBusy = true;
        await _session.SendAsync(text, files);
        _ = Item.RefreshSizeAsync(); // references were copied into the conversation
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path) || PendingAttachments.Any(a => a.Path == path)) continue;
            if (PendingAttachments.Count >= 20) break;
            PendingAttachments.Add(new PendingAttachment(path));
        }
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachment attachment) => PendingAttachments.Remove(attachment);

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task Stop() => _session.InterruptAsync();

    [RelayCommand]
    private void UseSuggestion(string text) => Input = text;

    private bool CanNewConversation() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNewConversation))]
    private void NewConversation()
    {
        _session.ClearChat();
        _ = Item.RefreshSizeAsync();
    }

    [RelayCommand]
    private Task InstallDependencies() => _session.InstallDependenciesAsync();

    [RelayCommand]
    private void RefreshFiles()
    {
        var keep = SelectedSprite?.FullPath;
        Replace(Sprites, _session.ListSprites().Select((f, i) =>
        {
            var size = SpriteSheet.ReadPngSize(f.FullName);
            return new MediaFile(
                f.FullName,
                f.Name,
                $"{size.W}×{size.H} · {f.LastWriteTime:dd/MM HH:mm}",
                IsLatest: i == 0,
                IsSent: f.Name == Project.SentVersion);
        }));
        Replace(Assets, _session.ListAssets().Where(f => f.Name != ".gitkeep").Select(f => new MediaFile(
            f.FullName, System.IO.Path.GetRelativePath(Project.AssetsDir, f.FullName).Replace('\\', '/'), Size(f.Length))));
        HasNodeModules = Project.HasNodeModules;
        OnPropertyChanged(nameof(HasNoSprites));
        OnPropertyChanged(nameof(HasNoAssets));
        _ = Item.RefreshSizeAsync();
        var selected = Sprites.FirstOrDefault(v => v.FullPath == keep) ?? Sprites.FirstOrDefault();
        if (selected == SelectedSprite) LoadSheet(); // same version: metadata/GIFs may have changed
        else SelectedSprite = selected;
    }

    [RelayCommand]
    private void OpenSpriteExternally()
    {
        if (SelectedSprite is not null) OpenWithSystem(SelectedSprite.FullPath);
    }

    [RelayCommand]
    private void OpenFile(MediaFile file) => OpenWithSystem(file.FullPath);

    [RelayCommand]
    private void ShowInFolder()
    {
        if (SelectedSprite is not null) OpenWithSystem(System.IO.Path.GetDirectoryName(SelectedSprite.FullPath)!);
    }

    public string SuggestedExportName => SelectedSprite?.Title ?? "sprite";

    /// <summary>
    /// Exports the selected version into a folder the user picked: sheet PNG + JSON metadata (+ GIFs).
    /// Files are named after the sprite (without the -vN suffix), ready to drop into a game project.
    /// </summary>
    public async Task ExportSelectedAsync(string folder, bool withGifs)
    {
        if (SelectedSprite is null) return;
        Status = $"Exportando {SelectedSprite.Name}…";
        try
        {
            var written = await ConversationFiles.ExportSpriteAsync(SelectedSprite.FullPath, folder, SuggestedExportName, withGifs);
            ExportError = null;
            ExportInfo = $"Exportado em {folder}: " + string.Join(", ", written.Select(System.IO.Path.GetFileName));
        }
        catch (Exception ex)
        {
            ExportError = ex.Message;
        }
        finally
        {
            Status = null;
        }
    }

    /// <summary>Saves a copy of one file (the sheet PNG or a GIF) where the user wants.</summary>
    public async Task ExportFileAsync(string source, string destination)
    {
        try
        {
            await using var src = File.OpenRead(source);
            await using var dst = File.Create(destination);
            await src.CopyToAsync(dst);
            ExportError = null;
            ExportInfo = "Exportado: " + System.IO.Path.GetFileName(destination);
        }
        catch (Exception ex)
        {
            ExportError = ex.Message;
        }
    }

    [ObservableProperty] private string? _exportError;
    [ObservableProperty] private string? _exportInfo;

    public async Task AddAssetsAsync(IEnumerable<string> localPaths)
    {
        foreach (var path in localPaths)
        {
            if (!File.Exists(path)) continue;
            Status = $"Copiando {System.IO.Path.GetFileName(path)}…";
            await using var src = File.OpenRead(path);
            await _session.SaveAssetAsync(System.IO.Path.GetFileName(path), src);
        }
        Status = _session.Status;
        RefreshFiles();
    }

    public static void OpenWithSystem(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // no handler registered
        }
    }

    // ------------------------------------------------------------------ sync with session

    private void OnSessionChanged()
    {
        // Coalesce bursts of text deltas into one UI update.
        if (_syncQueued) return;
        _syncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _syncQueued = false;
            Sync();
        }, DispatcherPriority.Background);
    }

    private void Sync()
    {
        var snapshot = _session.Messages;
        var i = 0;
        for (; i < snapshot.Count; i++)
        {
            var m = snapshot[i];
            if (i < Messages.Count && Messages[i].Id == m.Id)
            {
                Messages[i].Update(m);
                continue;
            }
            while (Messages.Count > i) Messages.RemoveAt(Messages.Count - 1);
            Messages.Add(MessageViewModel.From(m));
        }
        while (Messages.Count > snapshot.Count) Messages.RemoveAt(Messages.Count - 1);

        IsBusy = _session.IsBusy;
        IsInstalling = _session.IsInstalling;
        if (_session.Model is null && !ModelLabel.Contains('·') && _session.ActiveModel is not null) RefreshModels();
        Status = _session.Status;

        // Claude renders while it works: show new versions as soon as a tool finishes, not only at the end
        var toolsDone = snapshot.Count(m => m.Role == ChatRole.Tool && !m.IsPending);
        var finishedTurn = _wasBusy && !IsBusy;
        _wasBusy = IsBusy;
        if (finishedTurn || toolsDone != _toolsDone || !IsInstalling && HasNodeModules != Project.HasNodeModules)
        {
            _toolsDone = toolsDone;
            var before = Sprites.FirstOrDefault()?.FullPath;
            RefreshFiles();
            var newest = Sprites.FirstOrDefault();
            if (newest is not null && newest.FullPath != before) SelectedSprite = newest;
        }

        OnPropertyChanged(nameof(IsEmpty));
        MessagesUpdated?.Invoke();
    }

    private int _toolsDone = -1;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static string Size(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.#} MB";
        if (bytes >= kb) return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }

    private void OnRenamed() => Dispatcher.UIThread.Post(() =>
    {
        NotifyRenamed();
        Item.NotifyRenamed();
    });

    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _session.Renamed -= OnRenamed;
    }
}
