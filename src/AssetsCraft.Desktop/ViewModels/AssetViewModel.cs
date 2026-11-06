using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Desktop.ViewModels;

public enum StepState { Done, Active, Open, Locked }

/// <summary>One pill of the stepper at the top of an asset.</summary>
public sealed partial class StepViewModel(AssetStep step, int number) : ObservableObject
{
    public AssetStep Step { get; } = step;
    public int Number { get; } = number;
    public string Label => Step switch
    {
        AssetStep.Describe => "Descrever",
        AssetStep.Refine => "Refinar",
        AssetStep.Animate => "Animar",
        _ => "Enviar ao jogo",
    };
    public bool IsFirst => Number == 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsActive), nameof(IsLocked), nameof(ShowNumber))]
    private StepState _state;

    [ObservableProperty] private string _hint = "";

    public bool IsDone => State == StepState.Done;
    public bool IsActive => State == StepState.Active;
    public bool IsLocked => State == StepState.Locked;
    public bool ShowNumber => State != StepState.Done;
}

/// <summary>Hooks a conversation view model needs from the asset it belongs to.</summary>
public interface IAssetFlow
{
    /// <summary>"Animar vN →": opens (or starts) the animation step from that version of the sprite.</summary>
    Task AnimateAsync(ProjectViewModel sprite);
    Task<bool> ConfirmAsync(string title, string body, string confirm);
    /// <summary>Files of a conversation changed (new version, sent to the game…).</summary>
    void OnFilesChanged();
}

/// <summary>
/// The open asset: a stepper (Descrever → Refinar → Animar → Enviar) on top of the conversation of the
/// current step. Describe/Refine are the sprite's own conversation; Animar is an animation conversation that
/// starts from an approved version of the sprite. The user never has to switch sections to go forward.
/// </summary>
public sealed partial class AssetViewModel : ObservableObject, IAssetFlow
{
    private readonly MainWindowViewModel _owner;

    public AssetViewModel(AssetItem asset, MainWindowViewModel owner)
    {
        Asset = asset;
        _owner = owner;
        var n = 1;
        foreach (var s in asset.Steps) Steps.Add(new StepViewModel(s, n++));
        // reopen where the user left off: the newest animation if there is one, otherwise the sprite itself
        _current = _owner.ViewModelFor(asset.Animations.FirstOrDefault() ?? asset.Root, this);
        _shownAnimation = Current.IsAnimation ? asset.Animations.FirstOrDefault() : null;
        RefreshAnimationChoices();
        RefreshSteps();
    }

    public AssetItem Asset { get; }
    public string Name => Asset.Name;
    public ObservableCollection<StepViewModel> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingAnimation), nameof(BaseText))]
    private ProjectViewModel _current;

    /// <summary>Animation conversation shown in the Animar step (a sprite can be animated from several versions).</summary>
    [ObservableProperty] private ConversationItem? _shownAnimation;

    public ObservableCollection<AnimationChoice> AnimationChoices { get; } = [];
    public bool HasManyAnimationChoices => AnimationChoices.Count > 1;

    [ObservableProperty] private AnimationChoice? _selectedAnimationChoice;

    public bool IsShowingAnimation => Current.IsAnimation;
    public string BaseText => Current.Project.BaseSpriteName is { } b ? $"base {ProjectViewModel.VersionOf(b)}" : "";

    public void NotifyRenamed() => OnPropertyChanged(nameof(Name));

    partial void OnSelectedAnimationChoiceChanged(AnimationChoice? value)
    {
        if (value is not null && value.Item != ShownAnimation) Show(value.Item);
    }

    private void RefreshAnimationChoices()
    {
        AnimationChoices.Clear();
        foreach (var a in Asset.Animations) AnimationChoices.Add(new AnimationChoice(a));
        SelectedAnimationChoice = AnimationChoices.FirstOrDefault(c => c.Item == ShownAnimation);
        OnPropertyChanged(nameof(HasManyAnimationChoices));
    }

    private void Show(ConversationItem item)
    {
        Current = _owner.ViewModelFor(item, this);
        ShownAnimation = Current.IsAnimation ? item : ShownAnimation;
        SelectedAnimationChoice = AnimationChoices.FirstOrDefault(c => c.Item == ShownAnimation);
        RefreshSteps();
        _owner.RequestComposerFocus();
    }

    // ------------------------------------------------------------------ stepper

    private AssetStep ViewStep => Current.IsAnimation ? AssetStep.Animate
        : Current.HasNoSprites ? AssetStep.Describe
        : AssetStep.Refine;

    public void RefreshSteps()
    {
        Asset.RefreshState();
        var view = ViewStep;
        foreach (var s in Steps)
        {
            s.State = s.Step switch
            {
                _ when s.Step == view => StepState.Active,
                AssetStep.Describe => Asset.HasVersions ? StepState.Done : StepState.Open,
                AssetStep.Refine => !Asset.HasVersions ? StepState.Locked : Asset.Progress > AssetStep.Refine ? StepState.Done : StepState.Open,
                AssetStep.Animate => !Asset.HasVersions && Asset.Kind != AssetKind.Animation ? StepState.Locked
                    : Asset.Animations.Count > 0 || Asset.Kind == AssetKind.Animation && Asset.HasVersions ? StepState.Done : StepState.Open,
                _ => Asset.IsSent ? StepState.Done : Current.HasSelection ? StepState.Open : StepState.Locked,
            };
            s.Hint = s.Step switch
            {
                AssetStep.Describe => "Conte ao Claude o que você quer: tamanho, estilo, paleta. Anexe referências se tiver.",
                AssetStep.Refine => s.IsLocked ? "Disponível quando a primeira versão ficar pronta."
                    : "Peça ajustes no chat; cada ajuste vira uma nova versão, e as antigas continuam salvas.",
                AssetStep.Animate => s.IsLocked ? "Disponível quando o sprite tiver uma versão."
                    : "Crie andar, pular, atacar… a partir da versão aprovada. O design do sprite fica fixo.",
                _ => s.IsLocked ? "Disponível quando houver uma versão para enviar."
                    : (Current.SendTarget.Length > 0 ? $"Copia o PNG para {Current.SendTarget}" : "Copia o PNG para o projeto do jogo")
                      + ". Pede confirmação antes e pode ser desfeito com “Remover do jogo”.",
            };
        }
    }

    [RelayCommand]
    private async Task GoToStep(StepViewModel step)
    {
        if (step.IsLocked) return;
        switch (step.Step)
        {
            case AssetStep.Describe:
            case AssetStep.Refine:
                if (Asset.Kind != AssetKind.Animation) Show(Asset.Root);
                break;
            case AssetStep.Animate:
                if (ShownAnimation is { } shown) Show(shown);
                else if (Asset.Animations.FirstOrDefault() is { } latest) Show(latest);
                else if (Asset.Kind == AssetKind.Animation) Show(Asset.Root);
                else await AnimateAsync(_owner.ViewModelFor(Asset.Root, this));
                break;
            case AssetStep.Send:
                await Current.SendToGameCommand.ExecuteAsync(null);
                break;
        }
    }

    // ------------------------------------------------------------------ IAssetFlow

    public async Task AnimateAsync(ProjectViewModel sprite)
    {
        if (!Asset.CanAnimate || sprite.SelectedSprite is not { } version) return;

        // same base version already animated: just go there
        var same = Asset.Animations.FirstOrDefault(a => a.Project.BaseSpriteName == version.Name);
        if (same is not null)
        {
            Show(same);
            return;
        }
        if (Asset.Animations.FirstOrDefault() is { } previous && previous.Project.BaseSpriteName is { } oldBase)
        {
            var ok = await ConfirmAsync(
                $"Animar a partir da {version.VersionLabel}?",
                $"As animações atuais partem da {ProjectViewModel.VersionOf(oldBase)}. Começar novas animações com a {version.VersionLabel} " +
                "como base? As animações atuais continuam salvas e você troca entre elas no topo da etapa Animar.",
                $"Animar a {version.VersionLabel}");
            if (!ok)
            {
                Show(previous);
                return;
            }
        }

        var created = await _owner.CreateAnimationAsync(Asset, version.FullPath);
        if (created is null) return;
        ShownAnimation = created;
        RefreshAnimationChoices();
        Show(created);
    }

    public Task<bool> ConfirmAsync(string title, string body, string confirm) =>
        _owner.Confirm?.Invoke(title, body, confirm) ?? Task.FromResult(false);

    public void OnFilesChanged()
    {
        // the conversation view model reports its files while being built, before Current is set
        if (Current is not null) RefreshSteps();
    }

    // ------------------------------------------------------------------ header menu

    [RelayCommand]
    private void Rename() => _owner.BeginRename(Asset);

    [RelayCommand]
    private void Delete() => _owner.DeleteAssetCommand.Execute(Asset);
}

public sealed record AnimationChoice(ConversationItem Item)
{
    public string Label => Item.Project.BaseSpriteName is { } b ? $"Base {ProjectViewModel.VersionOf(b)}" : Item.Name;
}
