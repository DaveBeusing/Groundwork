using System.Runtime.InteropServices;

internal static partial class ThreadCycles
{
    internal static ulong Read() =>
        OperatingSystem.IsWindows() && QueryThreadCycleTime(new IntPtr(-2), out var cycles) != 0 ? cycles : 0;

    [LibraryImport("kernel32.dll")]
    private static partial int QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
