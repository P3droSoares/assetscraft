using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AssetsCraft.Core.Projects;

/// <summary>Persists the project list in &lt;WorkspacesRoot&gt;/projects.json.</summary>
public sealed partial class ProjectStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _indexFile;
    private readonly string _templatePath;
    private List<SpriteProject> _projects;

    public ProjectStore(IOptions<AssetsCraftOptions> options)
    {
        var o = options.Value;
        Root = string.IsNullOrWhiteSpace(o.WorkspacesRoot) ? AssetsCraftOptions.DefaultDataRoot : ExpandHome(o.WorkspacesRoot);
        ProjectsDir = Path.Combine(Root, "projects");
        Directory.CreateDirectory(ProjectsDir);
        _indexFile = Path.Combine(Root, "projects.json");
        _templatePath = Path.IsPathRooted(o.TemplatePath)
            ? o.TemplatePath
            : Path.Combine(AppContext.BaseDirectory, o.TemplatePath);
        _projects = Load();
    }

    public string Root { get; }
    public string TemplateDir => _templatePath;
    public string ProjectsDir { get; }

    public IReadOnlyList<SpriteProject> All()
    {
        // Projects whose folder was deleted outside the app are hidden (kept in projects.json).
        lock (_lock) return _projects.Where(p => Directory.Exists(p.Path)).OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public SpriteProject? Get(string id)
    {
        lock (_lock) return _projects.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Creates a conversation. Without a name it starts as "Nova conversa" and is titled automatically.</summary>
    public SpriteProject Create(string? name = null, string? gameId = null, AssetKind kind = AssetKind.Sprite,
        Action<SpriteProject>? configure = null)
    {
        var auto = string.IsNullOrWhiteSpace(name);
        name = auto ? Claude.ConversationTitler.DefaultName : name!.Trim();
        var slug = Slugify(name);
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        var id = $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";
        var path = Path.Combine(ProjectsDir, id);

        if (!Directory.Exists(_templatePath))
            throw new DirectoryNotFoundException($"Template não encontrado: {_templatePath}");

        CopyDirectory(_templatePath, path);
        Directory.CreateDirectory(Path.Combine(path, "out"));
        Directory.CreateDirectory(Path.Combine(path, "assets"));

        var project = new SpriteProject { Id = id, Name = name, Path = path, AutoName = auto, GameId = gameId, Kind = kind };
        configure?.Invoke(project);
        lock (_lock)
        {
            _projects.Add(project);
            SaveLocked();
        }
        return project;
    }

    /// <summary>
    /// Starts an animation conversation from a sprite conversation's latest version: its source becomes
    /// sprites/&lt;nome&gt;.sprite.json (the idle frame is the base) and the image goes to assets/base.png.
    /// </summary>
    public SpriteProject CreateAnimation(SpriteProject baseSprite, string sheetFull)
    {
        var title = SpriteTitle(sheetFull);
        return Create($"{title} · animações", baseSprite.GameId, AssetKind.Animation, p =>
        {
            p.BaseConversationId = baseSprite.Id;
            p.BaseSpriteName = Path.GetFileName(sheetFull);
            File.Copy(sheetFull, Path.Combine(p.AssetsDir, "base.png"), overwrite: true);
            var source = Path.Combine(Path.GetDirectoryName(sheetFull)!, ".sources", Path.GetFileNameWithoutExtension(sheetFull) + ".sprite.json");
            if (File.Exists(source))
            {
                Directory.CreateDirectory(p.SpritesDir);
                File.Copy(source, Path.Combine(p.SpritesDir, title + ".sprite.json"), overwrite: true);
            }
        });
    }

    /// <summary>"heroi-v3.png" → "heroi".</summary>
    public static string SpriteTitle(string file) =>
        VersionSuffix().Replace(Path.GetFileNameWithoutExtension(file), "");

    /// <summary>
    /// Deletes the project and every file it produced (workspace + Claude Code session files).
    /// Callers must stop the project's processes first (see SessionManager.DeleteProjectAsync).
    /// The entry is removed from the list even if some file could not be deleted; the report says which.
    /// </summary>
    public async Task<ProjectCleanup.Report> DeleteAsync(SpriteProject project)
    {
        var report = await ProjectCleanup.DeleteAllAsync(project, ProjectsDir);
        lock (_lock)
        {
            _projects.RemoveAll(p => p.Id == project.Id);
            SaveLocked();
        }
        return report;
    }

    /// <param name="byUser">A manual rename turns automatic titling off for good.</param>
    public void Rename(SpriteProject project, string name, bool byUser = true)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_lock)
        {
            project.Name = name.Trim();
            if (byUser) project.AutoName = false;
            project.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    public void Save(SpriteProject project)
    {
        lock (_lock)
        {
            project.UpdatedAt = DateTimeOffset.Now;
            SaveLocked();
        }
    }

    private List<SpriteProject> Load()
    {
        if (!File.Exists(_indexFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SpriteProject>>(File.ReadAllText(_indexFile)) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(_indexFile, _indexFile + ".corrupt", overwrite: true);
            return [];
        }
    }

    private void SaveLocked()
    {
        var tmp = _indexFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_projects, JsonOpts));
        File.Move(tmp, _indexFile, overwrite: true);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    public static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'))
            : path;

    private static string Slugify(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        var slug = NonSlugChars().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > 40) slug = slug[..40].Trim('-');
        return slug.Length == 0 ? "sprite" : slug;
    }

    [GeneratedRegex(@"-v\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();
}
