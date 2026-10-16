using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Barep.Services;

/// <summary>Resultado de liberar memoria: cuánto había en uso antes y después.</summary>
public sealed record MemoryCleanResult(long UsedBefore, long UsedAfter, long CacheBefore, long CacheAfter,
    int Trimmed, bool CachePurged)
{
    public long Freed => Math.Max(0, UsedBefore - UsedAfter);
    public long CacheFreed => Math.Max(0, CacheBefore - CacheAfter);
}

/// <summary>Cómo está la memoria: total, en uso y la caché de archivos de Windows.</summary>
public readonly record struct MemoryStatus(long Total, long Available, long Cache)
{
    public long Used => Math.Max(0, Total - Available);
    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
}

/// <summary>
/// «Liberar RAM». Hace lo mismo que herramientas como RAMMap: pide a cada
/// programa que suelte la memoria que tiene reservada pero no está usando
/// (vuelve a cargarla si la necesita) y, con permisos de administrador, vacía
/// la caché de archivos de Windows. No cierra nada ni borra ningún dato.
/// </summary>
public static class MemoryCleaner
{
    /// <summary>Procesos que no se tocan: recortarlos provoca tirones de imagen o de sonido.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "audiodg", "csrss", "System", "Idle", "Registry", "Memory Compression", "Secure System"
    };

    public static MemoryStatus Status()
    {
        long total = 0, available = 0, cache = 0;
        try
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status))
            {
                total = (long)status.TotalPhys;
                available = (long)status.AvailPhys;
            }
        }
        catch { }

        try
        {
            var info = new PerformanceInformation { Size = Marshal.SizeOf<PerformanceInformation>() };
            if (GetPerformanceInfo(out info, info.Size))
                cache = (long)info.SystemCache * (long)info.PageSize;
        }
        catch { }

        return new MemoryStatus(total, available, cache);
    }

    public static MemoryCleanResult Clean(bool purgeCache)
    {
        var before = Status();
        var self = Environment.ProcessId;
        var trimmed = 0;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self || process.Id <= 4 || Skip.Contains(process.ProcessName)) continue;
                var handle = OpenProcess(ProcessSetQuota | ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                try { if (EmptyWorkingSet(handle)) trimmed++; }
                finally { CloseHandle(handle); }
            }
            catch { }
            finally { process.Dispose(); }
        }

        var purged = false;
        if (purgeCache && TweakCatalog.IsAdmin && EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            // Primero se escribe a disco lo pendiente y después se vacía la caché en reposo.
            var flush = MemoryFlushModifiedList;
            NtSetSystemInformation(SystemMemoryListInformation, ref flush, sizeof(int));
            var purge = MemoryPurgeStandbyList;
            purged = NtSetSystemInformation(SystemMemoryListInformation, ref purge, sizeof(int)) == 0;
        }

        // Windows tarda un instante en reflejar la memoria devuelta.
        Thread.Sleep(1200);
        var after = Status();
        return new MemoryCleanResult(before.Used, after.Used, before.Cache, after.Cache, trimmed, purged);
    }

    // ────────────────────────── Interop ──────────────────────────

    private const int ProcessSetQuota = 0x0100;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int Size;
        public IntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation info, int size);

    // Activar un privilegio del propio proceso (hace falta para vaciar la caché).

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int Count;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length,
        IntPtr previous, IntPtr returnLength);

    private static bool EnablePrivilege(string name)
    {
        const int adjust = 0x0020, query = 0x0008, enabled = 0x2;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, adjust | query, out var token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) return false;
                var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = enabled };
                return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) &&
                       Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch { return false; }
    }
}
