namespace AssetsCraft.Core.Claude;

/// <summary>Typed events parsed from the Claude Code stream-json output.</summary>
public abstract record ClaudeEvent;

public sealed record SessionInitEvent(string SessionId, string? Model) : ClaudeEvent;

/// <summary>Partial text token from the model (requires --include-partial-messages).</summary>
public sealed record TextDeltaEvent(string Text) : ClaudeEvent;

/// <summary>Complete text block of an assistant message.</summary>
public sealed record AssistantTextEvent(string Text) : ClaudeEvent;

public sealed record ToolUseEvent(string Id, string Name, string InputSummary) : ClaudeEvent;

public sealed record ToolResultEvent(string ToolUseId, string Content, bool IsError) : ClaudeEvent;

public sealed record TurnCompletedEvent(
    bool IsError,
    string? Subtype,
    string? Result,
    double? CostUsd,
    long? DurationMs,
    int? NumTurns) : ClaudeEvent;

public sealed record ProcessExitedEvent(int ExitCode, string StdErrTail) : ClaudeEvent;

/// <summary>Usage-limit snapshot emitted by the CLI while a conversation runs.</summary>
public sealed record RateLimitEvent(IReadOnlyList<UsageWindow> Windows) : ClaudeEvent;
