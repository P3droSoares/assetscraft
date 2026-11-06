namespace AssetsCraft.Core.Projects;

public sealed class SpriteProject
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Path { get; init; }
    public string? SessionId { get; set; }

    /// <summary>Every Claude Code session id used by this project (needed to clean up on delete).</summary>
    public List<string> SessionIds { get; set; } = [];

    /// <summary>True while the name is the automatic placeholder/generated title (user never renamed it).</summary>
    public bool AutoName { get; set; }

    /// <summary>Model chosen for this conversation (null = Claude Code default).</summary>
    public string? Model { get; set; }

    /// <summary>Linked game project this asset belongs to (<see cref="GameStore"/>).</summary>
    public string? GameId { get; set; }

    /// <summary>Section of the sidebar: sprite, tile, animation…</summary>
    public AssetKind Kind { get; set; } = AssetKind.Sprite;

    /// <summary>Animation conversations: the sprite conversation they animate, and its name/version at the time.</summary>
    public string? BaseConversationId { get; set; }
    public string? BaseSpriteName { get; set; }

    /// <summary>Last version sent to the game project ("heroi-v3.png") and where it went.</summary>
    public string? SentVersion { get; set; }
    public string? SentPath { get; set; }

    /// <summary>Copy of the file the first send replaced in the game (restored by "Remover do jogo").</summary>
    public string? SentBackup { get; set; }

    /// <summary>Topmost folder the first send had to create in the game (removed again if left empty).</summary>
    public string? SentCreatedDir { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string OutputDir => System.IO.Path.Combine(Path, "out");
    public string AssetsDir => System.IO.Path.Combine(Path, "assets");
    public string ReferencesDir => System.IO.Path.Combine(Path, "references");
    public string SpritesDir => System.IO.Path.Combine(Path, "sprites");
    public bool HasNodeModules => Directory.Exists(System.IO.Path.Combine(Path, "node_modules"));
}

public sealed class AssetsCraftOptions
{
    /// <summary>Internal data folder (projects.json + one workspace per project). Empty = per-user app data:
    /// %LOCALAPPDATA%\AssetsCraft on Windows, ~/.local/share/AssetsCraft on Linux. "~" is expanded.</summary>
    public string WorkspacesRoot { get; set; } = "";

    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "AssetsCraft");

    /// <summary>Workspace template copied into each new project (tools/sprite.mjs + CLAUDE.md).
    /// Relative paths are resolved against the app base directory.</summary>
    public string TemplatePath { get; set; } = "template/workspace";

    /// <summary>Run "npm install" automatically when a project is created.</summary>
    public bool AutoInstallDependencies { get; set; } = true;

    public Claude.ClaudeCodeOptions Claude { get; set; } = new();
}
