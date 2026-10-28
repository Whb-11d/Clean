using System.Runtime.InteropServices;
using System.Text;
using Clean.Core;
using Microsoft.Win32.SafeHandles;

namespace Clean.Windows;

internal static class NativeFiles
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInfo information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);

    internal static SafeFileHandle Open(string path, uint access = 0x80, uint share = 7)
    {
        var handle = CreateFileW(path, access, share, 0, 3, 0x02000000 | 0x00200000, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Error(error);
    }
    internal static Exception Error(int error) => error switch
    {
        2 => new FileNotFoundException(),
        3 => new DirectoryNotFoundException(),
        5 => new UnauthorizedAccessException(),
        _ => new IOException($"Windows 文件操作失败（{error}）。", unchecked((int)0x80070000) | error)
    };
    internal static FileInfo Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(Marshal.GetLastWin32Error());
        return info;
    }
    internal static string Identity(SafeFileHandle handle)
    {
        // FileIdInfo is a 128-bit file ID plus 64-bit volume serial, including on ReFS.
        // Unsupported file systems fail closed instead of using a truncated identity.
        if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw Error(Marshal.GetLastWin32Error());
        return $"{id.Volume:X16}:{id.High:X16}{id.Low:X16}";
    }
    internal static void CheckPath(SafeFileHandle handle, string expected, bool directory)
    {
        var info = Info(handle);
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new PolicyException("文件类型变化或包含重解析点，已跳过。");
        var buffer = new StringBuilder(512);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0) throw Error(Marshal.GetLastWin32Error());
        if (count >= buffer.Capacity)
        {
            if (count > 32768) throw new PolicyException("路径长度超出支持范围。");
            buffer = new StringBuilder((int)count + 1);
            count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0) throw Error(Marshal.GetLastWin32Error());
            if (count >= buffer.Capacity) throw new PolicyException("路径长度超出支持范围。");
        }
        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final[4..];
        if (!PathPolicy.Normalize(final).Equals(PathPolicy.Normalize(expected), StringComparison.OrdinalIgnoreCase))
            throw new PolicyException("最终路径不匹配（别名、链接或路径已变化）。");
    }
    internal static DateTimeOffset Time(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        DateTimeOffset.FromFileTime(((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime);
    internal static FileSnapshot Snapshot(SafeFileHandle handle, string path, string ancestors)
    {
        CheckPath(handle, path, false);
        var info = Info(handle);
        return new(path, ((long)info.SizeHigh << 32) | info.SizeLow, Time(info.Creation), Time(info.Write),
            (FileAttributes)info.Attributes, Identity(handle), ancestors, info.Links);
    }
    internal static void Delete(SafeFileHandle handle)
    {
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw Error(Marshal.GetLastWin32Error());
    }
}
