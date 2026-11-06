using System.Text;
using AssetsCraft.Core.Sprites;

namespace AssetsCraft.Core.Projects;

/// <summary>
/// Copies a small sample of the linked game's existing PNGs into the conversation (references/game-style/),
/// so Claude can match the game's art style. Claude only ever sees the copy: it gets no access to the game
/// folder, so it cannot write there.
/// </summary>
public static class GameStyleSample
{
    public const string Folder = "references/game-style";
    private const int MaxFiles = 16;
    private const int MaxScanned = 4000;
    private const int MinSide = 8, MaxSide = 4096; // MonoGame atlases are often 2048px
    private const long MaxBytes = 2_000_000;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", ".vs", ".idea", "node_modules", ".config", "fonts", "font" };

    /// <summary>Paths that most likely hold game art come first; fonts and UI noise are skipped.</summary>
    private static readonly string[] ArtWords = ["sprite", "atlas", "character", "player", "enemy", "tile", "anim", "item", "npc"];

    /// <summary>Appended to the system prompt of sprite and tile conversations.</summary>
    public const string Instructions =
        "ESTILO DO JOGO: references/game-style/ tem uma amostra (cópia, só leitura) dos PNGs que já existem no projeto " +
        "do jogo vinculado; references/game-style/INDEX.md diz de onde veio cada arquivo. Antes de desenhar o primeiro " +
        "asset de uma conversa, analise essa amostra: leia o INDEX.md, rode `node tools/sprite.mjs inspect " +
        "references/game-style/<arquivo>` em 2 a 4 arquivos representativos (prefira personagens/objetos parecidos com o " +
        "pedido) e abra-os com Read. Atlas grandes (mais de 512 px): não rode inspect na imagem inteira; abra com Read e use " +
        "o .xml/.json de mesmo nome (copiado ao lado) para achar as regiões dos sprites individuais. Identifique: tamanho típico de quadro/tile, paleta (reaproveite as cores exatas), " +
        "contorno (tem? cor e espessura), sombreamento (chapado, dithering, quantos tons por cor), direção da luz, " +
        "proporções e nível de detalhe. Diga ao usuário, em 1 ou 2 linhas, o estilo que identificou e desenhe o novo " +
        "asset coerente com ele, a menos que o usuário peça outro estilo. Se a pasta não existir ou estiver vazia, siga o " +
        "pedido normalmente. Nunca tente ler ou escrever na pasta original do jogo.";

    /// <summary>
    /// Refreshes the sample (most recently changed sprite-sized PNGs of the game first). Returns how many files
    /// it holds. Never throws: a game folder that is gone or unreadable just yields an empty sample.
    /// </summary>
    public static int Sync(SpriteProject project, GameProject? game)
    {
        var dest = Path.Combine(project.Path, Folder);
        try
        {
            var picked = game is { Exists: true } ? Pick(game) : [];
            if (picked.Count == 0)
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                return 0;
            }

            Directory.CreateDirectory(dest);
            var keep = new HashSet<string>(StringComparer.Ordinal) { "INDEX.md" };
            var index = new StringBuilder()
                .AppendLine($"# Estilo do jogo: {game!.Name}")
                .AppendLine()
                .AppendLine("Cópia de PNGs que já existem no projeto do jogo (só para referência de estilo).")
                .AppendLine()
                .AppendLine("| arquivo | origem no jogo | tamanho |")
                .AppendLine("|---|---|---|");
            foreach (var (file, w, h) in picked)
            {
                var rel = Path.GetRelativePath(game.Path, file.FullName).Replace('\\', '/');
                var name = rel.Replace('/', '_');
                CopyIfChanged(file, Path.Combine(dest, name), keep);
                // atlas metadata (frame regions) next to the image: game-atlas.xml / .json
                var extra = "";
                foreach (var ext in new[] { ".xml", ".json", ".atlas" })
                {
                    var meta = new FileInfo(Path.ChangeExtension(file.FullName, ext));
                    if (!meta.Exists || meta.Length > MaxBytes) continue;
                    var metaName = Path.ChangeExtension(name, ext);
                    CopyIfChanged(meta, Path.Combine(dest, metaName), keep);
                    extra = $" (regiões em {metaName})";
                }
                index.AppendLine($"| {name} | {rel} | {w}×{h}{extra} |");
            }
            foreach (var old in Directory.EnumerateFiles(dest).ToList().Where(f => !keep.Contains(Path.GetFileName(f))))
                File.Delete(old);
            File.WriteAllText(Path.Combine(dest, "INDEX.md"), index.ToString());
            return picked.Count;
        }
        catch
        {
            return Directory.Exists(dest) ? Directory.EnumerateFiles(dest, "*.png").Count() : 0;
        }
    }

    private static void CopyIfChanged(FileInfo source, string target, HashSet<string> keep)
    {
        keep.Add(Path.GetFileName(target));
        var existing = new FileInfo(target);
        if (!existing.Exists || existing.Length != source.Length || existing.LastWriteTimeUtc < source.LastWriteTimeUtc)
            source.CopyTo(target, overwrite: true);
    }

    private static bool LooksLikeArt(string path) =>
        ArtWords.Any(w => path.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static List<(FileInfo File, int W, int H)> Pick(GameProject game)
    {
        var found = new List<(FileInfo, int, int)>();
        var scanned = 0;
        var stack = new Stack<string>([game.ContentRoot]);
        while (stack.Count > 0 && scanned < MaxScanned)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir, "*.png").ToList();
            }
            catch
            {
                continue;
            }
            foreach (var d in subdirs)
                if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
            foreach (var f in files)
            {
                if (++scanned > MaxScanned) break;
                var info = new FileInfo(f);
                if (info.Length > MaxBytes || info.Name.Contains("font", StringComparison.OrdinalIgnoreCase)) continue;
                var (w, h) = SpriteSheet.ReadPngSize(f);
                if (w < MinSide || h < MinSide || w > MaxSide || h > MaxSide) continue;
                found.Add((info, w, h));
            }
        }
        return found
            .OrderByDescending(x => LooksLikeArt(x.Item1.FullName))
            .ThenByDescending(x => x.Item1.LastWriteTimeUtc)
            .Take(MaxFiles).ToList();
    }
}
