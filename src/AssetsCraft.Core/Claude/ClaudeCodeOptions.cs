namespace AssetsCraft.Core.Claude;

/// <summary>
/// Options used to launch the local Claude Code CLI in headless (stream-json) mode.
/// The CLI reuses the login already configured on this machine (`claude` / `/login`).
/// </summary>
public sealed class ClaudeCodeOptions
{
    /// <summary>Path or name of the Claude Code executable. Resolved via PATH when not absolute.</summary>
    public string ExecutablePath { get; set; } = "claude";

    /// <summary>Optional model alias or full id (e.g. "sonnet", "opus"). Null uses the CLI default.</summary>
    public string? Model { get; set; }

    /// <summary>default | acceptEdits | plan | bypassPermissions. Headless mode cannot prompt, so
    /// tools not covered by the mode or <see cref="AllowedTools"/> are denied automatically.</summary>
    public string PermissionMode { get; set; } = "acceptEdits";

    /// <summary>Tool permission rules passed to --allowedTools (e.g. "Bash(ffmpeg:*)").
    /// Keep defaults in appsettings.json: list initializers here would be appended to by the config binder.</summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Tool rules passed to --disallowedTools.</summary>
    public List<string> DisallowedTools { get; set; } = [];

    /// <summary>Extra text appended to Claude Code's system prompt.</summary>
    public string? AppendSystemPrompt { get; set; }

    /// <summary>Passed to --setting-sources. "project,local" ignores the user's global ~/.claude settings
    /// (hooks, plugins, output styles) so the app behaves the same on every machine; login still works.
    /// Empty = Claude Code default (user + project + local).</summary>
    public string? SettingSources { get; set; } = "project,local";

    /// <summary>Any extra raw CLI arguments.</summary>
    public List<string> ExtraArgs { get; set; } = [];

    /// <summary>Copy with more text appended to the system prompt (per-conversation instructions).</summary>
    public ClaudeCodeOptions WithAppendedPrompt(string? extra) => new()
    {
        ExecutablePath = ExecutablePath,
        Model = Model,
        PermissionMode = PermissionMode,
        AllowedTools = [.. AllowedTools],
        DisallowedTools = [.. DisallowedTools],
        AppendSystemPrompt = string.Join("\n\n", new[] { AppendSystemPrompt, extra }.Where(s => !string.IsNullOrWhiteSpace(s))),
        SettingSources = SettingSources,
        ExtraArgs = [.. ExtraArgs],
    };
}
