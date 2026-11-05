namespace AssetsCraft.Core.Sessions;

public enum ChatRole { User, Assistant, Tool, System, Error }

public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ChatRole Role { get; init; }
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>Assistant text still receiving deltas.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>Reference files attached to a user message (paths relative to the project).</summary>
    public List<string>? Attachments { get; set; }

    // Tool calls
    public string? ToolUseId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolOutput { get; set; }
    public bool IsError { get; set; }
    public bool IsPending { get; set; }
}
