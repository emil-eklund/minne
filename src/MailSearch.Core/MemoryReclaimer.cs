using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MailSearch;

/// <summary>Returns as much process memory as possible to the OS after expensive resources are unloaded.</summary>
public static class MemoryReclaimer
{
    /// <summary>
    /// Full compacting GC, then (on Windows) a working-set trim. The trim matters because unloading a
    /// model returns its pages to the process, not to the OS; trimming drops them from the working set
    /// (the number Task Manager shows) and the next search only faults back what it reads.
    /// </summary>
    public static void Reclaim()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
