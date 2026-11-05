namespace AssetsCraft.Core;

/// <summary>
/// Locates the FFmpeg build shipped next to the executable (&lt;app&gt;/ffmpeg/bin).
/// Falls back to ffmpeg/ffprobe on PATH when the bundle is missing (e.g. dev builds without vendor/).
/// </summary>
public static class BundledTools
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>Directory holding the bundled ffmpeg/ffprobe, or null when not bundled.</summary>
    public static string? FfmpegBinDir { get; } = FindBinDir();

    public static string Ffmpeg => FfmpegBinDir is { } d ? Path.Combine(d, "ffmpeg" + Exe) : "ffmpeg";
    public static string Ffprobe => FfmpegBinDir is { } d ? Path.Combine(d, "ffprobe" + Exe) : "ffprobe";
    public static bool IsBundled => FfmpegBinDir is not null;

    private static string? FindBinDir()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin");
        var ffmpeg = Path.Combine(dir, "ffmpeg" + Exe);
        if (!File.Exists(ffmpeg)) return null;

        // MSBuild copies may drop the executable bit on Unix.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var f in new[] { ffmpeg, Path.Combine(dir, "ffprobe" + Exe) })
            {
                if (!File.Exists(f)) continue;
                var mode = File.GetUnixFileMode(f);
                const UnixFileMode exec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((mode & UnixFileMode.UserExecute) == 0)
                {
                    try { File.SetUnixFileMode(f, mode | exec); } catch { /* read-only install */ }
                }
            }
        }
        return dir;
    }
}
