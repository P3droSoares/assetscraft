using CommunityToolkit.Mvvm.ComponentModel;
using AssetsCraft.Core.Sessions;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>Chat bubble. One subclass per kind so the view can pick a DataTemplate by type.</summary>
public abstract partial class MessageViewModel : ObservableObject
{
    protected MessageViewModel(ChatMessage m) => Id = m.Id;

    public Guid Id { get; }

    [ObservableProperty] private string _text = "";

    public virtual void Update(ChatMessage m) => Text = m.Text;

    public static MessageViewModel From(ChatMessage m)
    {
        MessageViewModel vm = m.Role switch
        {
            ChatRole.User => new UserMessageViewModel(m),
            ChatRole.Assistant => new AssistantMessageViewModel(m),
            ChatRole.Tool => new ToolMessageViewModel(m),
            _ => new NoticeMessageViewModel(m),
        };
        vm.Update(m);
        return vm;
    }
}

public sealed class UserMessageViewModel(ChatMessage m) : MessageViewModel(m)
{
    public IReadOnlyList<SentAttachment> Attachments { get; } =
        (m.Attachments ?? []).Select(a => new SentAttachment(
            Path.GetFileName(a),
            PendingAttachment.BadgeFor(ProjectSession.ReferenceKind(a)))).ToList();

    public bool HasAttachments => Attachments.Count > 0;
}

public sealed partial class AssistantMessageViewModel(ChatMessage m) : MessageViewModel(m)
{
    [ObservableProperty] private bool _isStreaming;

    public override void Update(ChatMessage m)
    {
        base.Update(m);
        IsStreaming = m.IsStreaming;
    }
}

public sealed partial class ToolMessageViewModel(ChatMessage m) : MessageViewModel(m)
{
    public string ToolName { get; } = m.ToolName ?? "?";

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _output;
    [ObservableProperty] private bool _isPending;
    [ObservableProperty] private bool _isError;

    public override void Update(ChatMessage m)
    {
        base.Update(m);
        var i = m.Text.IndexOf('\n');
        Summary = i < 0 ? m.Text : m.Text[..i] + " …";
        Output = string.IsNullOrWhiteSpace(m.ToolOutput) ? null : m.ToolOutput.TrimEnd();
        IsPending = m.IsPending;
        IsError = m.IsError;
    }

    public string Details => string.IsNullOrEmpty(Output) ? Text : $"{Text}\n\n{Output}";

    partial void OnOutputChanged(string? value) => OnPropertyChanged(nameof(Details));
}

public sealed partial class NoticeMessageViewModel(ChatMessage m) : MessageViewModel(m)
{
    public bool IsError { get; } = m.Role == ChatRole.Error;
}
