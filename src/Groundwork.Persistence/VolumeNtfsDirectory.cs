using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Groundwork.Persistence;

internal sealed partial class VolumeNtfsDirectory : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint WriteThrough = 0x80000000;
    private const uint BackupSemantics = 0x02000000;
    private readonly List<SafeFileHandle> _pins = [];

    internal VolumeNtfsDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows() || new DriveInfo(Path.GetPathRoot(directory)!).DriveFormat != "NTFS")
            throw new IOException("Volume checkpoints require local NTFS.");
        try
        {
            var chain = new Stack<DirectoryInfo>();
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
                chain.Push(current);
            foreach (var part in chain)
            {
                var pin = Open(part.FullName, ReadAttributes | 0x20, 3, 3, OpenReparsePoint | BackupSemantics);
                _pins.Add(pin);
                Verify(pin, directory: true);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static void Reserve(string path)
    {
        if (!CreateDirectory(path, IntPtr.Zero))
            throw Win32Error();
    }

    internal static FileStream OpenFile(string path, bool candidate = false, bool lease = false)
    {
        var access = candidate ? 0xC0010000u : lease ? 0xC0000000u : 0x80000000u;
        var handle = Open(path, access, candidate || lease ? 0u : 1u, candidate ? 1u : lease ? 4u : 3u,
            OpenReparsePoint | BackupSemantics | (candidate ? WriteThrough : 0));
        try
        {
            Verify(handle, directory: false);
            return new FileStream(handle, candidate || lease ? FileAccess.ReadWrite : FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FileIdentity(ulong VolumeSerial, ulong Low, ulong High);

    internal static FileIdentity Identify(FileStream file)
    {
        if (!GetFileIdentity(file.SafeFileHandle, 18, out var identity, 24))
            throw Win32Error();
        return identity;
    }
    internal unsafe void Publish(FileStream candidate, string leafName, bool replace)
    {
        // FILE_RENAME_INFORMATION. RootDirectory pins the relative destination in the validated slot.
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var data = new byte[nameOffset + sizeof(char) * leafName.Length];
        data[0] = replace ? (byte)1 : (byte)0;
        MemoryMarshal.Write(data.AsSpan(rootOffset), _pins[^1].DangerousGetHandle());
        MemoryMarshal.Write(data.AsSpan(lengthOffset), sizeof(char) * leafName.Length);
        MemoryMarshal.AsBytes(leafName.AsSpan()).CopyTo(data.AsSpan(nameOffset));
        fixed (byte* pointer = data)
        {
            var status = SetInformation(candidate.SafeFileHandle, out _, pointer, (uint)data.Length, 10);
            if (status != 0)
                throw new IOException("NTFS checkpoint rename failed.", Marshal.GetExceptionForHR(unchecked((int)(status | 0x10000000))));
        }
        GC.KeepAlive(_pins);
    }

    public void Dispose()
    {
        for (var i = _pins.Count - 1; i >= 0; i--)
            _pins[i].Dispose();
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        var error = Win32Error();
        handle.Dispose();
        throw error;
    }

    private static void Verify(SafeFileHandle handle, bool directory)
    {
        if (!GetInformation(handle, 9, out var info, 8))
            throw Win32Error();
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory)
            throw new InvalidDataException("Unexpected reparse point or file type.");
        if (!directory)
        {
            if (!GetStandardInformation(handle, 1, out var standard, 24))
                throw Win32Error();
            if (standard.Links != 1)
                throw new InvalidDataException("Checkpoint files must have a single directory entry.");
        }
    }

    private static IOException Win32Error()
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException(new Win32Exception(error).Message, unchecked((int)(0x80070000u | (uint)error)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeInformation { public uint Attributes, ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInformation
    {
        public long AllocationSize, EndOfFile;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectory(string path, IntPtr security);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetInformation(SafeFileHandle handle, int kind, out AttributeInformation info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetStandardInformation(SafeFileHandle handle, int kind, out StandardInformation info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileIdentity(SafeFileHandle handle, int kind, out FileIdentity info, uint size);
    [LibraryImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static unsafe partial int SetInformation(SafeFileHandle handle, out IoStatus status, byte* info, uint size, int kind);
}
