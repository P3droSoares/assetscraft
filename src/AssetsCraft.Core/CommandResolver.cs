using System.Diagnostics;
using System.Text;

namespace AssetsCraft.Core;

/// <summary>
/// Builds a <see cref="ProcessStartInfo"/> that works on Windows and Linux.
/// On Windows, tools like npm/npx (and claude when installed via npm) are .cmd shims that
/// CreateProcess cannot run directly, so they are resolved through PATH/PATHEXT and wrapped in cmd.exe.
/// </summary>
public static class CommandResolver
{
    public static ProcessStartInfo Create(string command, IEnumerable<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Child processes (Claude Code, npm, node) see the bundled ffmpeg/ffprobe first on PATH.
        if (BundledTools.FfmpegBinDir is { } ffmpegDir)
            psi.Environment["PATH"] = ffmpegDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        var resolved = Resolve(command);
        var ext = Path.GetExtension(resolved).ToLowerInvariant();
        if (OperatingSystem.IsWindows() && ext is ".cmd" or ".bat")
        {
            // cmd /s strips the first and last quote of the command line, so the whole command needs an
            // extra pair of quotes; otherwise a shim under a path with spaces (C:\Program Files\nodejs) breaks.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"{string.Join(' ', args.Prepend(resolved).Select(QuoteForCmd))}\"";
            return psi;
        }

        psi.FileName = resolved;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string QuoteForCmd(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || "\"&|<>^()".Contains(c))
            ? arg
            : "\"" + arg.Replace("\"", "\"\"") + "\"";

    /// <summary>Resolves a command name to a full path when possible; otherwise returns it unchanged.</summary>
    public static string Resolve(string command)
    {
        if (Path.IsPathRooted(command)) return command;

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Default install location of the native Claude Code installer (both OSes).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));

        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            candidates = Path.HasExtension(command)
                ? [command]
                : [command + ".exe", command + ".cmd", command + ".bat", command];
        }
        else
        {
            candidates = [command];
        }

        foreach (var dir in dirs)
        foreach (var name in candidates)
        {
            var full = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(full)) return full;
        }

        return command;
    }
}
