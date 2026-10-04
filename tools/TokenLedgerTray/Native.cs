using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DevMind.TokenLedgerTray;

/// <summary>
/// Win32 P/Invokes the tray app needs: DestroyIcon so the GDI handle returned by
/// Icon.FromHandle does not leak on every icon refresh, and AttachConsole so a
/// WinExe can write --selftest output to the calling console.
/// </summary>
internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    /// <summary>AttachedConsole sentinel: attach to the parent process's console.</summary>
    public const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeConsole();

    /// <summary>
    /// Job object with KILL_ON_JOB_CLOSE. Docs: that flag requires
    /// JOBOBJECT_EXTENDED_LIMIT_INFORMATION (JobObjectExtendedLimitInformation,
    /// class 9) - the basic structure (class 2) silently rejects it with
    /// ERROR_INVALID_PARAMETER (87).
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(
        IntPtr job,
        int jobObjectInformationClass,
        IntPtr jobObjectInformation,
        int jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Puts a process in a job that dies with the job handle. Returns the job
    /// handle, which the caller must keep alive for the life of the child and
    /// then CloseHandle to terminate the tree. Null when unavailable - the
    /// caller then falls back to killing the direct child only.
    /// </summary>
    public static IntPtr CreateKillOnCloseJob()
    {
        const int JobObjectExtendedLimitInformation = 9;
        const int JobObjectLimitKillOnJobClose = 0x2000;

        var job = CreateJobObject(IntPtr.Zero, string.Empty);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        // Marshal.SizeOf, not sizeof: the latter needs an unsafe context, and the
        // marshaler's size is what SetInformationJobObject expects anyway.
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, size))
            {
                CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.DestroyStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    public static bool Assign(IntPtr job, Process process)
    {
        if (job == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
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
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// Settings, read once from an optional TokenLedgerTray.json next to the exe.
/// Missing file or missing/blank property = built-in default. Never written.
/// </summary>
internal sealed record TraySettings(string LedgerDir, string ScriptsDir, string? SettingsError)
{
    public const string DefaultLedgerDir = @"G:\DevMind_Tracing\ledger";
    public const string DefaultScriptsDir = @"C:\Users\pkailas\source\repos\DevMind\tools\TokenLedger";

    public static TraySettings Load()
    {
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, "TokenLedgerTray.json");

        if (!File.Exists(path))
        {
            return new TraySettings(DefaultLedgerDir, DefaultScriptsDir, null);
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw);

            var ledger = Pick(parsed, "LedgerDir", DefaultLedgerDir);
            var scripts = Pick(parsed, "ScriptsDir", DefaultScriptsDir);
            return new TraySettings(ledger, scripts, null);
        }
        catch (Exception ex)
        {
            // A malformed settings file must not stop the tray: fall back to the
            // defaults and keep the reason for the tooltip.
            return new TraySettings(DefaultLedgerDir, DefaultScriptsDir, ex.Message);
        }
    }

    private static string Pick(Dictionary<string, string>? parsed, string key, string fallback)
    {
        if (parsed is null)
        {
            return fallback;
        }

        // Ordinal-ignore-case so the file can use either casing.
        foreach (var pair in parsed)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value!;
            }
        }

        return fallback;
    }
}
