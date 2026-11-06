namespace AssetsCraft.Core.Projects;

/// <summary>Deleting and exporting individual files of a conversation (a sprite version) safely.</summary>
public static class ConversationFiles
{
    /// <summary>
    /// Everything one render produced for out/&lt;name&gt;-vN.png: the sheet, its metadata, the GIFs of each
    /// animation, the zoomed preview and the source snapshot. Only existing files are returned.
    /// </summary>
    public static List<string> SpriteVersionFiles(SpriteProject project, string sheetFull)
    {
        var dir = Path.GetDirectoryName(sheetFull)!;
        var stem = Path.GetFileNameWithoutExtension(sheetFull);
        var files = new List<string>
        {
            sheetFull,
            Path.Combine(dir, stem + ".json"),
            Path.Combine(dir, ".preview", stem + ".png"),
            Path.Combine(dir, ".sources", stem + ".sprite.json"),
        };
        var gifs = Path.Combine(dir, "gif");
        if (Directory.Exists(gifs)) files.AddRange(Directory.EnumerateFiles(gifs, stem + "-*.gif"));
        return files.Where(File.Exists).ToList();
    }

    /// <summary>GIFs rendered for this version (one per animation with 2+ frames).</summary>
    public static List<string> Gifs(string sheetFull)
    {
        var gifs = Path.Combine(Path.GetDirectoryName(sheetFull)!, "gif");
        var stem = Path.GetFileNameWithoutExtension(sheetFull);
        return Directory.Exists(gifs) ? Directory.EnumerateFiles(gifs, stem + "-*.gif").OrderBy(f => f).ToList() : [];
    }

    /// <summary>Deletes a sprite version from out/ (sheet + metadata + GIFs + preview + source snapshot).</summary>
    public static List<string> DeleteSprite(SpriteProject project, string sheetRel)
    {
        var full = Inside(project, sheetRel, ["out"]);
        var deleted = new List<string>();
        foreach (var f in SpriteVersionFiles(project, full)) DeleteFile(f, deleted);
        return deleted;
    }

    /// <summary>Deletes a file the user added to assets/.</summary>
    public static List<string> DeleteAsset(SpriteProject project, string assetRel)
    {
        var deleted = new List<string>();
        DeleteFile(Inside(project, assetRel, ["assets"]), deleted);
        return deleted;
    }

    /// <summary>
    /// Copies a version into <paramref name="folder"/>: the sheet PNG, its JSON (with "image" kept pointing at the
    /// copied PNG) and, when <paramref name="withGifs"/>, one GIF per animation. Returns the written paths.
    /// </summary>
    public static async Task<List<string>> ExportSpriteAsync(string sheetFull, string folder, string baseName, bool withGifs)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        var stem = Path.GetFileNameWithoutExtension(sheetFull);

        var png = Path.Combine(folder, baseName + ".png");
        File.Copy(sheetFull, png, overwrite: true);
        written.Add(png);

        var meta = Path.ChangeExtension(sheetFull, ".json");
        if (File.Exists(meta))
        {
            var json = await File.ReadAllTextAsync(meta);
            json = json.Replace($"\"image\": \"{stem}.png\"", $"\"image\": \"{baseName}.png\"");
            var dest = Path.Combine(folder, baseName + ".json");
            await File.WriteAllTextAsync(dest, json);
            written.Add(dest);
        }

        if (withGifs)
        {
            foreach (var gif in Gifs(sheetFull))
            {
                var anim = Path.GetFileNameWithoutExtension(gif)[(stem.Length + 1)..];
                var dest = Path.Combine(folder, $"{baseName}-{anim}.gif");
                File.Copy(gif, dest, overwrite: true);
                written.Add(dest);
            }
        }
        return written;
    }

    /// <summary>
    /// Copies the version's PNG into the game: &lt;Content&gt;/&lt;Sprites|Tiles|Animations&gt;/&lt;nome&gt;.png, overwriting
    /// the previous one (the game always has the approved version under a stable name). Only the PNG goes:
    /// wiring it into the game is the game's job. Returns the destination.
    /// </summary>
    public static string SendToGame(SpriteProject project, string sheetFull, GameProject game)
    {
        if (!game.Exists) throw new DirectoryNotFoundException($"A pasta do projeto não existe mais: {game.Path}");
        var name = project.Kind == AssetKind.Animation && project.BaseSpriteName is { } baseName
            ? ProjectStore.SpriteTitle(baseName)
            : ProjectStore.SpriteTitle(sheetFull);
        var dest = SendDestination(game, project.Kind, name);
        var ownsDest = project.SentPath is { } prev && PathEquals(prev, dest);
        if (!ownsDest)
        {
            // first send to this destination: remember what was there so "Remover do jogo" can put it back
            if (project.SentPath is not null) RemoveFromGame(project, game);
            project.SentBackup = null;
            project.SentCreatedDir = null;
            if (File.Exists(dest))
            {
                var backupDir = Path.Combine(project.Path, ".assetscraft", "replaced");
                Directory.CreateDirectory(backupDir);
                var backup = Path.Combine(backupDir, Path.GetFileName(dest));
                File.Copy(dest, backup, overwrite: true);
                project.SentBackup = backup;
            }
            project.SentCreatedDir = TopmostMissing(Path.GetDirectoryName(dest)!);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(sheetFull, dest, overwrite: true);
        return dest;
    }

    /// <summary>The PNG in the game is no longer the version that was sent (edited in the game since).</summary>
    public static bool SentFileChanged(SpriteProject project)
    {
        if (project.SentPath is not { } dest || !File.Exists(dest) || project.SentVersion is not { } v) return false;
        var sent = Path.Combine(project.OutputDir, v);
        if (!File.Exists(sent)) return false;
        return !File.ReadAllBytes(sent).AsSpan().SequenceEqual(File.ReadAllBytes(dest));
    }

    /// <summary>
    /// Undoes "Enviar ao jogo": deletes the PNG that was sent, puts back the file it had replaced (if any) and
    /// removes the folders the send created, when empty. Only touches paths inside the game folder.
    /// </summary>
    public static void RemoveFromGame(SpriteProject project, GameProject game)
    {
        if (project.SentPath is not { } dest) return;
        var root = Path.GetFullPath(game.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(dest);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"Recusado: {dest} está fora do projeto do jogo.");

        if (File.Exists(full)) File.Delete(full);
        if (project.SentBackup is { } backup && File.Exists(backup))
        {
            File.Copy(backup, full, overwrite: true);
            File.Delete(backup);
        }
        else
        {
            // folders created by the send (or, for sends from before this was tracked, any empty parent)
            var stop = project.SentCreatedDir is { } created ? Path.GetDirectoryName(Path.GetFullPath(created)) : root.TrimEnd(Path.DirectorySeparatorChar);
            for (var dir = Path.GetDirectoryName(full); dir is not null && dir.StartsWith(root, StringComparison.Ordinal)
                     && !PathEquals(dir, stop!) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
                 dir = Path.GetDirectoryName(dir))
                Directory.Delete(dir);
        }
        project.SentVersion = null;
        project.SentPath = null;
        project.SentBackup = null;
        project.SentCreatedDir = null;
    }

    private static string? TopmostMissing(string dir)
    {
        string? missing = null;
        for (var d = Path.GetFullPath(dir); d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing = d;
        return missing;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string SendDestination(GameProject game, AssetKind kind, string name) =>
        Path.Combine(game.ContentRoot, AssetKinds.Get(kind).Folder, name + ".png");

    private static string Inside(SpriteProject project, string rel, string[] allowedTopFolders)
    {
        var root = Path.GetFullPath(project.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var top = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (!full.StartsWith(root, StringComparison.Ordinal) || !allowedTopFolders.Contains(top))
            throw new InvalidOperationException($"Recusado: {rel} não é um arquivo excluível desta conversa.");
        return full;
    }

    private static void DeleteFile(string path, List<string> deleted)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        deleted.Add(path);
    }
}
