using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Clean.Core;

namespace Clean.Windows;

public sealed class SystemMetrics : IDiskInfoProvider, IMemoryInfoProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public ImmutableArray<DiskSnapshot> GetDisks()
    {
        var result = ImmutableArray.CreateBuilder<DiskSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Fixed) result.Add(new(drive.Name, drive.TotalSize, drive.AvailableFreeSpace, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { result.Add(new(drive.Name, 0, 0, Errors.Describe(e))); }
        }
        return result.ToImmutable();
    }
    public MemorySnapshot Sample(bool includeProcesses)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw NativeFiles.Error(Marshal.GetLastWin32Error());
        var processes = ImmutableArray.CreateBuilder<ProcessSnapshot>();
        int inaccessible = 0;
        if (includeProcesses)
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try { processes.Add(new(process.Id, process.ProcessName, process.WorkingSet64)); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { inaccessible++; }
                }
            }
        }
        return new(DateTimeOffset.UtcNow, status.TotalPhysical, status.AvailablePhysical,
            processes.OrderByDescending(x => x.WorkingSetBytes).ToImmutableArray(), inaccessible);
    }
}
