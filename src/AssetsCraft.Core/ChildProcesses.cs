using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AssetsCraft.Core;

/// <summary>
/// Single entry point for every process the app starts (Claude Code, ffmpeg/ffprobe, npm, node…),
/// guaranteeing nothing is left running after the app goes away:
///
/// 1. All children are tracked; on any exit path (window close, SIGTERM/SIGINT/SIGHUP, Ctrl+C,
///    unhandled exception, ProcessExit) their whole process trees are killed.
/// 2. Hard kill of the app (SIGKILL / crash / Task Manager):
///    - Windows: children live in a Job Object with KILL_ON_JOB_CLOSE — the OS kills them all.
///    - Linux: children are started through <c>setpriv --pdeathsig KILL</c>, so they die with the app.
/// 3. Every child (and grandchild) carries ASSETSCRAFT_OWNER=&lt;app pid&gt; in its environment; on startup
///    <see cref="SweepOrphans"/> kills leftovers whose owner is no longer running (Linux).
/// </summary>
public static class ChildProcesses
{
    public const string OwnerVariable = "ASSETSCRAFT_OWNER";

    private static readonly ConcurrentDictionary<int, Process> Running = new();
    private static readonly string? SetPriv = OperatingSystem.IsLinux() && File.Exists("/usr/bin/setpriv") ? "/usr/bin/setpriv" : null;
    private static readonly List<PosixSignalRegistration> Signals = [];
    private static int _installed;
    private static IntPtr _job;

    /// <summary>Installs exit/signal handlers and creates the Windows job. Call once at startup.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => KillAll();
        Console.CancelKeyPress += (_, _) => KillAll();

        if (!OperatingSystem.IsWindows())
        {
            foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
            {
                try
                {
                    Signals.Add(PosixSignalRegistration.Create(sig, _ => KillAll())); // default action (exit) still runs
                }
                catch { /* unsupported signal on this platform */ }
            }
        }
        else
        {
            _job = CreateKillOnCloseJob();
        }

        SweepOrphans();
    }

    /// <summary>Starts a tracked child process. Use instead of Process.Start everywhere.</summary>
    public static Process Start(ProcessStartInfo psi)
    {
        if (psi.UseShellExecute) throw new InvalidOperationException("Use ChildProcesses só para processos sem shell.");
        psi.Environment[OwnerVariable] = Environment.ProcessId.ToString();

        if (SetPriv is not null && psi.FileName != SetPriv)
        {
            // setpriv execs the target in the same pid with "kill me when my parent dies"
            var original = psi.FileName;
            var args = psi.ArgumentList.ToList();
            psi.FileName = SetPriv;
            psi.ArgumentList.Clear();
            foreach (var a in new[] { "--pdeathsig", "KILL", "--", original }.Concat(args)) psi.ArgumentList.Add(a);
        }

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Exited += (_, _) => Running.TryRemove(SafeId(p), out _);
        // Linux pdeathsig fires when the *thread* that forked the child exits, and pool threads retire
        // when idle — so every child is forked from one dedicated thread that lives as long as the app.
        var started = SetPriv is null ? p.Start() : Spawner.Run(p.Start);
        if (!started) throw new InvalidOperationException($"Não foi possível iniciar {psi.FileName}");
        Running[p.Id] = p;
        if (_job != IntPtr.Zero)
        {
            try { AssignProcessToJobObject(_job, p.Handle); } catch { /* already in a job that forbids nesting */ }
        }
        if (p.HasExited) Running.TryRemove(p.Id, out _);
        return p;
    }

    /// <summary>Kills a process and all of its descendants (best effort, never throws).</summary>
    public static void KillTree(Process? p)
    {
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
    }

    public static int Count => Running.Count(kv => !SafeExited(kv.Value));

    /// <summary>Kills every tracked child tree. Safe to call many times / from signal handlers.</summary>
    public static void KillAll()
    {
        foreach (var p in Running.Values) KillTree(p);
        Running.Clear();
    }

    /// <summary>
    /// Linux: kills processes left behind by a previous run (ASSETSCRAFT_OWNER points to a dead or different
    /// process). Only the current user's processes are readable, so nothing else is touched.
    /// </summary>
    public static int SweepOrphans()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var killed = 0;
        var me = Environment.ProcessId;
        foreach (var dir in SafeEnumerate("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == me) continue;
            string environ;
            try { environ = File.ReadAllText(Path.Combine(dir, "environ")); }
            catch { continue; }
            var owner = environ.Split('\0').FirstOrDefault(e => e.StartsWith(OwnerVariable + "=", StringComparison.Ordinal));
            if (owner is null || !int.TryParse(owner[(OwnerVariable.Length + 1)..], out var ownerPid)) continue;
            if (ownerPid == me || IsAlive(ownerPid)) continue; // belongs to a running AssetsCraft
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                killed++;
            }
            catch { /* exited meanwhile */ }
        }
        return killed;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            // pid reuse: only count it if it is actually a AssetsCraft instance
            var name = p.ProcessName;
            return name.Contains("AssetsCraft", StringComparison.OrdinalIgnoreCase) || name is "dotnet";
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return []; }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return -1; }
    }

    private static bool SafeExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    /// <summary>Long-lived thread used to fork children (keeps pdeathsig bound to the app's lifetime).</summary>
    private static class Spawner
    {
        private static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();

        static Spawner()
        {
            var t = new Thread(() =>
            {
                foreach (var work in Queue.GetConsumingEnumerable()) work();
            })
            {
                IsBackground = true,
                Name = "AssetsCraft child spawner",
            };
            t.Start();
        }

        public static T Run<T>(Func<T> func)
        {
            T result = default!;
            Exception? error = null;
            using var done = new ManualResetEventSlim();
            Queue.Add(() =>
            {
                try { result = func(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result;
        }
    }

    // ------------------------------------------------------------------ Windows Job Object

    private static IntPtr CreateKillOnCloseJob()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size))
                    throw new Win32Exception();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job; // intentionally never closed: the OS closes it when the app dies → children killed
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
