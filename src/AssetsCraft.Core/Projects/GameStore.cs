using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetsCraft.Core.Projects;

/// <summary>A game project (MonoGame or any folder) the assets are made for. Lives outside the app.</summary>
public sealed class GameProject
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Path { get; init; }
    public bool IsMonoGame { get; set; }
    public DateTimeOffset LinkedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Where "Enviar ao jogo" writes: &lt;game&gt;/Content when it exists; otherwise the folder of the MonoGame
    /// Content.mgcb (often one level down: &lt;repo&gt;/&lt;Projeto&gt;/Content); otherwise the folder itself.
    /// </summary>
    public string ContentRoot
    {
        get
        {
            var content = System.IO.Path.Combine(Path, "Content");
            if (Directory.Exists(content)) return content;
            try
            {
                var mgcb = Directory.EnumerateFiles(Path, "*.mgcb", new EnumerationOptions
                    {
                        RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true,
                    })
                    .Where(f => !System.IO.Path.GetRelativePath(Path, f).Split(System.IO.Path.DirectorySeparatorChar)
                        .Any(s => s is "bin" or "obj"))
                    .OrderBy(f => f.Length)
                    .FirstOrDefault();
                if (mgcb is not null) return System.IO.Path.GetDirectoryName(mgcb)!;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return Path;
        }
    }

    public bool Exists => Directory.Exists(Path);

    /// <summary>Fixed color palette for everything made for this game (null = free colors).</summary>
    public GamePalette? Palette { get; set; }
}

/// <summary>Linked game projects and the active one, in &lt;data&gt;/games.json.</summary>
public sealed partial class GameStore
{
    private sealed class State
    {
        public List<GameProject> Games { get; set; } = [];
        public string? ActiveId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _file;
    private State _state;

    public GameStore(string dataRoot)
    {
        _file = System.IO.Path.Combine(dataRoot, "games.json");
        _state = Load();
    }

    public IReadOnlyList<GameProject> All()
    {
        lock (_lock) return _state.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public GameProject? Active
    {
        get { lock (_lock) return _state.Games.FirstOrDefault(g => g.Id == _state.ActiveId) ?? _state.Games.FirstOrDefault(); }
    }

    public GameProject? Get(string? id)
    {
        lock (_lock) return _state.Games.FirstOrDefault(g => g.Id == id);
    }

    public void SetActive(GameProject game)
    {
        lock (_lock)
        {
            _state.ActiveId = game.Id;
            SaveLocked();
        }
    }

    /// <summary>Sets (or clears, with null) the game's palette.</summary>
    public void SetPalette(GameProject game, GamePalette? palette)
    {
        lock (_lock)
        {
            var stored = _state.Games.FirstOrDefault(g => g.Id == game.Id) ?? throw new InvalidOperationException("Projeto não vinculado.");
            stored.Palette = palette;
            game.Palette = palette;
            SaveLocked();
        }
    }

    /// <summary>Links a folder (re-linking the same folder returns the existing entry) and makes it active.</summary>
    public GameProject Link(string folder)
    {
        folder = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Pasta não encontrada: {folder}");
        lock (_lock)
        {
            var existing = _state.Games.FirstOrDefault(g => string.Equals(g.Path, folder,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (existing is null)
            {
                var (name, mono) = Inspect(folder);
                existing = new GameProject { Id = Guid.NewGuid().ToString("N")[..12], Name = name, Path = folder, IsMonoGame = mono };
                _state.Games.Add(existing);
            }
            _state.ActiveId = existing.Id;
            SaveLocked();
            return existing;
        }
    }

    /// <summary>Removes the link only (the game folder and the conversations are not touched).</summary>
    public void Unlink(GameProject game)
    {
        lock (_lock)
        {
            _state.Games.RemoveAll(g => g.Id == game.Id);
            if (_state.ActiveId == game.Id) _state.ActiveId = _state.Games.FirstOrDefault()?.Id;
            SaveLocked();
        }
    }

    /// <summary>Name from the .csproj (or the folder); MonoGame when a csproj references it or Content.mgcb exists.</summary>
    public static (string Name, bool IsMonoGame) Inspect(string folder)
    {
        var csproj = Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault()
                     ?? SafeEnumerate(folder, "*.csproj").FirstOrDefault();
        var name = csproj is not null ? System.IO.Path.GetFileNameWithoutExtension(csproj) : new DirectoryInfo(folder).Name;
        var mono = SafeEnumerate(folder, "*.mgcb").Any()
                   || (csproj is not null && MonoGameRef().IsMatch(File.ReadAllText(csproj)));
        return (name, mono);
    }

    private static IEnumerable<string> SafeEnumerate(string folder, string pattern)
    {
        try
        {
            // shallow: the project itself and one level below (Content/, src/<proj>/)
            return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true });
        }
        catch (IOException)
        {
            return [];
        }
    }

    private State Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_file)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
        File.Move(tmp, _file, overwrite: true);
    }

    [GeneratedRegex(@"MonoGame\.", RegexOptions.IgnoreCase)]
    private static partial Regex MonoGameRef();
}
