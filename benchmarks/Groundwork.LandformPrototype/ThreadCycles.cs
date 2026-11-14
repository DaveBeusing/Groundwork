using System.Runtime.InteropServices;

internal static partial class ThreadCycles
{
    internal static ulong Read() =>
        OperatingSystem.IsWindows() && QueryThreadCycleTime(new IntPtr(-2), out var cycles) != 0 ? cycles : 0;

    internal static void Pin(int processor)
    {
        if (!OperatingSystem.IsWindows() || processor < 0 || processor >= IntPtr.Size * 8 ||
            SetThreadAffinityMask(new IntPtr(-2), (nuint)1 << processor) == 0)
            throw new InvalidOperationException($"Could not pin this thread to logical processor {processor}.");
        if (GetCurrentProcessorNumber() != processor)
            throw new InvalidOperationException("The benchmark thread is not on the requested logical processor.");
        Console.Error.WriteLine($"Pinned sweep thread {NativeThreadId()} to logical processor {processor}.");
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint SetThreadAffinityMask(IntPtr thread, nuint mask);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessorNumber();

    internal static uint NativeThreadId() => OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0;

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial int QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
