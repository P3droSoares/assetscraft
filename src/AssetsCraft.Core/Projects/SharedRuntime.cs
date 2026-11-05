using System.Diagnostics;
using System.Security.Cryptography;

namespace AssetsCraft.Core.Projects;

/// <summary>
/// One node_modules shared by every project. Normally it ships with the app (&lt;app&gt;/runtime, installed at
/// build time — see InstallNodeRuntime in the Desktop .csproj); only when that bundle is missing or stale
/// (dev build without npm, edited template) is it installed once in &lt;data&gt;/runtime.
/// Each project gets only a link to it (symlink on Linux, junction on Windows — no admin needed).
/// The runtime is reinstalled automatically when the template's package.json changes.
/// </summary>
public sealed class SharedRuntime
{
    private const string StampFile = ".template-hash";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _templatePackageJson;

    public SharedRuntime(string dataRoot, string templatePath)
    {
        _templatePackageJson = Path.Combine(templatePath, "package.json");
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime");
        IsBundled = IsReadyAt(bundled);
        Dir = IsBundled ? bundled : Path.Combine(dataRoot, "runtime");
    }

    /// <summary>Using the node_modules shipped next to the executable (nothing to install).</summary>
    public bool IsBundled { get; }

    public string Dir { get; }
    public string NodeModules => Path.Combine(Dir, "node_modules");

    /// <summary>True when node_modules exists and matches the current template package.json.</summary>
    public bool IsReady => IsReadyAt(Dir);

    private bool IsReadyAt(string dir) =>
        Directory.Exists(Path.Combine(dir, "node_modules"))
        && File.Exists(Path.Combine(dir, StampFile))
        && string.Equals(File.ReadAllText(Path.Combine(dir, StampFile)).Trim(), TemplateHash(), StringComparison.OrdinalIgnoreCase);

    /// <summary>&lt;project&gt;/node_modules is a link to this runtime (not to an older/other one).</summary>
    public bool IsLinkedInto(string projectPath)
    {
        var link = Path.Combine(projectPath, "node_modules");
        return IsLink(link) && SameTarget(link);
    }

    /// <summary>Installs the shared dependencies if needed. Concurrent callers wait for the same install.</summary>
    public async Task<(bool Ok, string Output)> EnsureAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        if (IsReady) return (true, "");
        await _gate.WaitAsync(ct);
        try
        {
            if (IsReady) return (true, "");
            onStatus?.Invoke("Instalando dependências compartilhadas (só na primeira vez)…");

            Directory.CreateDirectory(Dir);
            File.Copy(_templatePackageJson, Path.Combine(Dir, "package.json"), overwrite: true);
            File.Delete(Path.Combine(Dir, StampFile));

            var psi = CommandResolver.Create("npm", ["install", "--no-fund", "--no-audit"], Dir);
            using var p = ChildProcesses.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }

            var output = await stdout + await stderr;
            if (p.ExitCode != 0) return (false, output);

            await File.WriteAllTextAsync(Path.Combine(Dir, StampFile), TemplateHash(), ct);
            return (true, output);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Points &lt;project&gt;/node_modules at the shared runtime (replacing a stale link).</summary>
    public void LinkInto(string projectPath)
    {
        var link = Path.Combine(projectPath, "node_modules");
        if (IsLink(link))
        {
            if (SameTarget(link)) return;
            RemoveLink(link);
        }
        else if (Directory.Exists(link))
        {
            // Conversation from an older version with its own node_modules (stale: lacks newer shared
            // packages such as the music engine). It is only a dependency cache inside the app's data,
            // so replace it with the shared link — also frees hundreds of MB.
            Directory.Delete(link, recursive: true); // .NET deletes links inside without following them
        }

        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, NodeModules }) psi.ArgumentList.Add(a);
            using var p = ChildProcesses.Start(psi);
            p.WaitForExit();
            if (p.ExitCode != 0) throw new IOException("mklink /J falhou: " + p.StandardError.ReadToEnd());
        }
        else
        {
            Directory.CreateSymbolicLink(link, NodeModules);
        }
    }

    public static bool IsLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists && info.LinkTarget is null) return File.Exists(path) && new FileInfo(path).LinkTarget is not null;
            return info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes only the link itself, never the directory it points to.</summary>
    public static void RemoveLink(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.Delete(path, recursive: false); // junction/symlink: removes the reparse point
        else File.Delete(path); // unlink(2)
    }

    private bool SameTarget(string link)
    {
        var target = new DirectoryInfo(link).LinkTarget;
        return target is not null
               && Path.GetFullPath(target, Path.GetDirectoryName(link)!).TrimEnd(Path.DirectorySeparatorChar)
               == Path.GetFullPath(NodeModules).TrimEnd(Path.DirectorySeparatorChar);
    }

    private string TemplateHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_templatePackageJson)));
}
