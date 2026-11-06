using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetsCraft.Core.Projects;

/// <summary>
/// Color palette of a game project. When set, every sprite, tileset and animation of that game may only use
/// these colors: Claude is told so, and tools/sprite.mjs snaps any other color to the nearest one on render.
/// </summary>
public sealed class GamePalette
{
    public const int MaxColors = 256;

    /// <summary>Workspace file read by tools/sprite.mjs (and by Claude).</summary>
    public const string WorkspaceFile = ".assetscraft/palette.json";

    public required string Name { get; set; }

    /// <summary>Opaque colors as "rrggbb" (lowercase, no '#'), in the file's order, no duplicates.</summary>
    public required List<string> Colors { get; set; }

    /// <summary>Appended to every conversation's system prompt (no-op while the file doesn't exist).</summary>
    public const string Instructions =
        "PALETA DO JOGO: se o arquivo .assetscraft/palette.json existir, o projeto do jogo tem uma paleta fixa e TODO " +
        "asset (sprite, tile, animação) deve usar SOMENTE as cores dele, mais transparência. Use \"palette\": \"game\" na " +
        "fonte do sprite (as chaves 0-9, a-v… seguem a ordem do arquivo; veja `node tools/sprite.mjs palettes`) ou " +
        "{ \"$base\": \"game\", \"x\": \"#hex-da-paleta\" }. Não use paletas prontas, cores inventadas, semitransparência, " +
        "nem SVG com degradê/antialiasing fora da paleta: o render troca qualquer cor de fora pela mais próxima e avisa; " +
        "se avisar, revise o desenho. Se o usuário pedir uma cor que não existe na paleta, use a mais próxima e diga isso.";

    /// <summary>Reads a palette from a text file: .hex (one color per line), .gpl (GIMP), .pal (JASC), .txt (Paint.NET) or .json.</summary>
    public static GamePalette FromTextFile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        List<string> colors = ext switch
        {
            ".gpl" => ParseGpl(text, ref name),
            ".pal" => ParseJasc(text),
            ".json" => ParseJson(text),
            _ => ParseHexList(text),
        };
        return Create(name, colors);
    }

    /// <summary>Validates and de-duplicates. Throws with a user-facing message when the palette is unusable.</summary>
    public static GamePalette Create(string name, IEnumerable<string> colors)
    {
        var list = colors.Select(Normalize).Where(c => c is not null).Cast<string>().Distinct().ToList();
        if (list.Count == 0) throw new InvalidDataException("Nenhuma cor encontrada no arquivo.");
        if (list.Count > MaxColors)
            throw new InvalidDataException($"O arquivo tem {list.Count} cores; uma paleta pode ter no máximo {MaxColors}. " +
                                           "Use um arquivo de paleta (como os do lospec.com), não uma imagem/screenshot.");
        return new GamePalette { Name = string.IsNullOrWhiteSpace(name) ? "paleta" : name.Trim(), Colors = list };
    }

    private static string? Normalize(string c)
    {
        var s = c.Trim().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 8 && Regex.IsMatch(s, "^[0-9a-fA-F]{8}$")) s = s[2..]; // AARRGGBB (Paint.NET)
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        return Regex.IsMatch(s, "^[0-9a-fA-F]{6}$") ? s.ToLowerInvariant() : null;
    }

    private static List<string> ParseHexList(string text) =>
        text.Split('\n')
            .Select(l => l.Split(';')[0].Trim())       // Paint.NET comments
            .Where(l => l.Length > 0)
            .SelectMany(l => Regex.Matches(l, "#?(?:0x)?[0-9a-fA-F]{6,8}\\b|#[0-9a-fA-F]{3}\\b").Select(m => m.Value))
            .ToList();

    private static List<string> ParseGpl(string text, ref string name)
    {
        var colors = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase)) { name = line[5..].Trim(); continue; }
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("GIMP") || line.StartsWith("Columns")) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                colors.Add($"{r:x2}{g:x2}{b:x2}");
        }
        return colors;
    }

    private static List<string> ParseJasc(string text)
    {
        var colors = new List<string>();
        foreach (var raw in text.Split('\n').Skip(3)) // JASC-PAL, 0100, count
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
                colors.Add($"{r:x2}{g:x2}{b:x2}");
        }
        return colors;
    }

    private static List<string> ParseJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("colors", out var c)) root = c;
        return root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
    }

    /// <summary>Writes (or removes) .assetscraft/palette.json in a conversation workspace.</summary>
    public static void SyncInto(string workspace, GamePalette? palette)
    {
        var file = Path.Combine(workspace, WorkspaceFile);
        if (palette is null)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }
        var json = JsonSerializer.Serialize(new { name = palette.Name, colors = palette.Colors.Select(c => "#" + c) },
            new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file) || File.ReadAllText(file) != json) File.WriteAllText(file, json);
    }

    public static string Hex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"{r:x2}{g:x2}{b:x2}");
}
