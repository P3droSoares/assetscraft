using System.Text.RegularExpressions;

namespace AssetsCraft.Core.Projects;

/// <summary>
/// Removes everything a project left on disk: its internal workspace (sprite sources, node_modules,
/// renders, assets, chat) and the files Claude Code keeps per working directory / session
/// under ~/.claude (or $CLAUDE_CONFIG_DIR).
///
/// Safety: only paths derived from this project are touched — the workspace must live inside
/// the app's projects folder, and Claude files are matched by the exact project path or by
/// session ids (GUIDs) recorded for this project.
/// </summary>
public static partial class ProjectCleanup
{
    public sealed record Report(List<string> Deleted, List<string> Failed)
    {
        public bool Success => Failed.Count == 0;
    }

    public static async Task<Report> DeleteAllAsync(SpriteProject project, string projectsRoot)
    {
        var report = new Report([], []);

        var workspace = Path.GetFullPath(project.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(projectsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!workspace.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || workspace.Length <= root.Length)
            throw new InvalidOperationException($"Recusado: {workspace} está fora da pasta interna de projetos.");

        // 1) links first (node_modules → shared runtime): remove the link only, never what it points to
        RemoveLinks(workspace, report);

        // 2) workspace (retry: on Windows, just-killed processes may still hold handles for a moment)
        await DeleteDirectoryAsync(workspace, report);

        // 3) Claude Code artifacts
        foreach (var path in ClaudeArtifacts(project))
        {
            if (Directory.Exists(path)) await DeleteDirectoryAsync(path, report);
            else DeleteFile(path, report);
        }

        return report;
    }

    public static string ClaudeConfigDir =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } cfg
            ? cfg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// Existing files/folders Claude Code keeps for this project: transcripts folder named after the
    /// sanitized working directory, plus per-session files (session-env, file-history, todos, debug).
    /// </summary>
    public static IEnumerable<string> ClaudeArtifacts(SpriteProject project)
    {
        var claudeDir = ClaudeConfigDir;
        if (!Directory.Exists(claudeDir)) yield break;

        var workspace = Path.GetFullPath(project.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectsDir = Path.Combine(claudeDir, "projects");
        var sanitizedDir = Path.Combine(projectsDir, NonAlnum().Replace(workspace, "-"));
        if (Directory.Exists(sanitizedDir)) yield return sanitizedDir;

        var sessionIds = project.SessionIds
            .Append(project.SessionId)
            .OfType<string>()
            .Where(id => GuidRe().IsMatch(id))
            .Distinct()
            .ToList();

        foreach (var id in sessionIds)
        {
            // transcripts that may live in a differently-named folder (path truncation, etc.)
            if (Directory.Exists(projectsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(projectsDir))
                {
                    if (dir == sanitizedDir) continue;
                    var jsonl = Path.Combine(dir, id + ".jsonl");
                    if (File.Exists(jsonl)) yield return jsonl;
                    var sub = Path.Combine(dir, id);
                    if (Directory.Exists(sub)) yield return sub;
                }
            }

            foreach (var d in new[] { Path.Combine(claudeDir, "session-env", id), Path.Combine(claudeDir, "file-history", id) })
                if (Directory.Exists(d)) yield return d;

            var debug = Path.Combine(claudeDir, "debug", id + ".txt");
            if (File.Exists(debug)) yield return debug;

            var todos = Path.Combine(claudeDir, "todos");
            if (Directory.Exists(todos))
                foreach (var f in Directory.EnumerateFiles(todos, id + "*"))
                    yield return f;
        }
    }

    /// <summary>
    /// Total bytes this conversation occupies on disk: its workspace (renders, assets, references, chat,
    /// sources, any private node_modules) plus Claude Code's files for it. Links (the shared runtime)
    /// are not followed, so shared dependencies are not counted.
    /// </summary>
    public static long MeasureBytes(SpriteProject project)
    {
        long total = DirectorySize(project.Path);
        foreach (var path in ClaudeArtifacts(project))
            total += Directory.Exists(path) ? DirectorySize(path) : SafeLength(path);
        return total;
    }

    private static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir) || SharedRuntime.IsLink(dir)) return 0;
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(dir);
        var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos("*", opts); }
            catch { continue; }
            foreach (var e in entries)
            {
                if (e.LinkTarget is not null || e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // don't follow links
                if (e is DirectoryInfo) stack.Push(e.FullName);
                else if (e is FileInfo f) total += f.Length;
            }
        }
        return total;
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    /// <summary>Walks the workspace without following links and unlinks every symlink/junction found.</summary>
    private static void RemoveLinks(string dir, Report report)
    {
        if (!Directory.Exists(dir) || SharedRuntime.IsLink(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (SharedRuntime.IsLink(entry))
            {
                try
                {
                    SharedRuntime.RemoveLink(entry);
                    report.Deleted.Add(entry + " (link)");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Failed.Add($"{entry}: {ex.Message}");
                }
            }
            else if (Directory.Exists(entry))
            {
                RemoveLinks(entry, report);
            }
        }
    }

    private static async Task DeleteDirectoryAsync(string path, Report report)
    {
        if (!Directory.Exists(path)) return;
        if (SharedRuntime.IsLink(path)) return; // never recurse through a link
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
                report.Deleted.Add(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 8)
                {
                    report.Failed.Add($"{path}: {ex.Message}");
                    return;
                }
                await Task.Delay(250 * attempt);
            }
        }
    }

    private static void DeleteFile(string path, Report report)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            report.Deleted.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Failed.Add($"{path}: {ex.Message}");
        }
    }

    /// <summary>Windows refuses to delete read-only files (common in node_modules / git objects).</summary>
    private static void ClearReadOnly(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                var a = File.GetAttributes(f);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
            }
            catch { /* best effort */ }
        }
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidRe();
}
