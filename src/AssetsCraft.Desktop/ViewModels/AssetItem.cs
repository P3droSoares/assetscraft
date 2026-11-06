using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;
using AssetsCraft.Core.Sessions;
using AssetsCraft.Core.Sprites;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>The linear flow of an asset. Tiles skip <see cref="Animate"/>.</summary>
public enum AssetStep { Describe, Refine, Animate, Send }

/// <summary>
/// One asset of the game in the sidebar: a sprite (with its animation conversations folded in), a tileset,
/// or an old animation whose base sprite was deleted. Shows a thumbnail and how far along the flow it is.
/// </summary>
public sealed partial class AssetItem : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public AssetItem(ConversationItem root, MainWindowViewModel owner)
    {
        Root = root;
        _owner = owner;
        root.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationItem.Name)) OnPropertyChanged(nameof(Name));
            if (e.PropertyName == nameof(ConversationItem.SizeBytes)) UpdateSize();
        };
    }

    public ConversationItem Root { get; }
    public SpriteProject Project => Root.Project;
    public AssetKind Kind => Project.Kind;
    public string Name => Root.Name;

    /// <summary>Animation conversations of this sprite, newest first.</summary>
    public List<ConversationItem> Animations { get; } = [];
    public IEnumerable<ConversationItem> Conversations => Animations.Prepend(Root);

    public bool CanAnimate => Kind == AssetKind.Sprite;
    public string KindLabel => Kind switch
    {
        AssetKind.Sprite => "Sprite",
        AssetKind.Tile => "Tiles",
        AssetKind.Animation => "Animação",
        _ => AssetKinds.Get(Kind).Label,
    };

    /// <summary>Steps this kind goes through, in order.</summary>
    public IReadOnlyList<AssetStep> Steps => Kind switch
    {
        AssetKind.Sprite => [AssetStep.Describe, AssetStep.Refine, AssetStep.Animate, AssetStep.Send],
        AssetKind.Animation => [AssetStep.Animate, AssetStep.Send],
        _ => [AssetStep.Describe, AssetStep.Refine, AssetStep.Send],
    };

    public DateTimeOffset UpdatedAt => Conversations.Max(c => c.Project.UpdatedAt);

    public void AddAnimation(ConversationItem item)
    {
        Animations.Add(item);
        Animations.Sort((a, b) => b.Project.CreatedAt.CompareTo(a.Project.CreatedAt));
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationItem.SizeBytes)) UpdateSize();
        };
        UpdateSize();
    }

    // ------------------------------------------------------------------ progress

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbAnimation), nameof(HasThumb))]
    private SpriteSheet? _thumb;

    public SpriteAnimation? ThumbAnimation => Thumb?.Animations.FirstOrDefault();
    public bool HasThumb => Thumb is not null;

    [ObservableProperty] private AssetStep _progress;
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private bool _isSent;
    [ObservableProperty] private bool _hasVersions;

    /// <summary>Re-reads the generated files: thumbnail, versions and how far the asset got.</summary>
    public void RefreshState()
    {
        var versions = ProjectSession.ListSprites(Project);
        HasVersions = versions.Count > 0;
        var thumbFile = versions.FirstOrDefault();
        try { Thumb = thumbFile is null ? null : SpriteSheet.Load(thumbFile.FullName); }
        catch { Thumb = null; }

        IsSent = Conversations.Any(c => c.Project.SentVersion is not null);
        var animated = Kind == AssetKind.Animation ? HasVersions : Animations.Count > 0;
        Progress = IsSent ? AssetStep.Send
            : animated ? AssetStep.Animate
            : HasVersions ? AssetStep.Refine
            : Kind == AssetKind.Animation ? AssetStep.Animate
            : AssetStep.Describe;

        var version = thumbFile is null ? "" : ProjectViewModel.VersionOf(thumbFile.Name);
        StepText = IsSent ? "no projeto"
            : Progress switch
            {
                AssetStep.Describe => "a descrever",
                AssetStep.Refine => $"refinando · {version}",
                AssetStep.Animate => "animando",
                _ => "",
            };
    }

    // ------------------------------------------------------------------ size

    [ObservableProperty] private string _sizeText = "";
    public long SizeBytes => Conversations.Sum(c => c.SizeBytes);

    private void UpdateSize()
    {
        SizeText = PendingAttachment.FormatSize(SizeBytes);
        OnPropertyChanged(nameof(SizeBytes));
    }

    // ------------------------------------------------------------------ rename / delete

    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editName = "";

    [RelayCommand]
    private void Rename() => _owner.BeginRename(this);

    [RelayCommand]
    private void Delete() => _owner.DeleteAssetCommand.Execute(this);

    public void StartEditing()
    {
        EditName = Name;
        IsRenaming = true;
    }
}
