using System.Runtime.InteropServices;
using Clean.Core;

namespace Clean.Windows;

public sealed class ShellRecycleBin : IRecycleBinService
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct BinInfo { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? root, ref BinInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint owner, string? root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint context, ref Guid iid, out IShellItem item);

    public RecycleBinSnapshot Query()
    {
        var info = new BinInfo { Size = (uint)Marshal.SizeOf<BinInfo>() };
        var hr = SHQueryRecycleBinW(null, ref info);
        return hr >= 0 ? new(info.Items, info.Bytes) : new(0, 0, $"无法查询回收站（0x{hr:X8}）。");
    }
    public EmptyBinResult Empty() => InSta(() =>
    {
        var before = Query();
        if (before.Error is not null) return new EmptyBinResult(false, false, before, before, before.Error);
        // Keep the system's confirmation UI. Never used by automated tests.
        var hr = SHEmptyRecycleBinW(0, null, 0);
        var after = Query();
        bool cancelled = hr is unchecked((int)0x800704C7) or unchecked((int)0x80270000);
        return new EmptyBinResult(hr >= 0, cancelled, before, after,
            hr >= 0 ? "系统操作已返回成功；前后数量变化可能受其他程序影响。" :
            $"{(cancelled ? "已取消" : "清空失败")}（0x{hr:X8}），可能已有部分项目被处理，请查看回收站。");
    });
    internal static ActionOutcome Recycle(FileSnapshot expected, Func<bool> verify, CancellationToken token) => InSta(() =>
    {
        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!;
            operation = (IFileOperation)Activator.CreateInstance(type)!;
            // Recycle only, no recursive or connected-file work, no elevation or error dialogs.
            operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x00000400 | 0x00000004 | 0x00001000 | 0x00002000 | 0x00000010);
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(expected.Path, 0, ref iid, out item);
            var sink = new RecycleSink(verify, token);
            operation.DeleteItem(item, sink);
            var hr = operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            GC.KeepAlive(sink);
            if (sink.Recycled) return new ActionOutcome(ItemStatus.Succeeded, ErrorKind.None, "已移入 Windows 回收站；可在回收站中手动还原，未声称释放空间。");
            if (sink.Rejected) return new ActionOutcome(ItemStatus.Skipped, token.IsCancellationRequested ? ErrorKind.Cancelled : ErrorKind.Policy,
                "取消、文件变化或系统未保证回收站操作；未允许永久删除降级。");
            return new ActionOutcome(ItemStatus.Failed, ErrorKind.Unavailable,
                $"系统未确认移入回收站（0x{(sink.Result ?? hr):X8}，取消={aborted}），请检查原路径与回收站。");
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
        }
    });
    internal static T InSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception e) { error = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(Func<bool> verify, CancellationToken token) : IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }
        public bool Rejected { get; private set; }
        public int? Result { get; private set; }
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try { if (!token.IsCancellationRequested && (flags & 0x80) != 0 && verify()) return 0; }
            catch (Exception) { /* Fail closed across the COM callback boundary. */ }
            Rejected = true;
            return unchecked((int)0x80004004);
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem)
        { Result = result; Recycled = result >= 0 && newItem is not null && (flags & 0x80) != 0; return 0; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint f, IShellItem i, string n) => unchecked((int)0x80004004);
        public int PostRenameItem(uint f, IShellItem i, string n, int r, IShellItem? v) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string? n) => unchecked((int)0x80004004);
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string? n, int r, IShellItem? v) => 0;
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string? n) => unchecked((int)0x80004004);
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string? n, int r, IShellItem? v) => 0;
        public int PreNewItem(uint f, IShellItem d, string n) => unchecked((int)0x80004004);
        public int PostNewItem(uint f, IShellItem d, string n, string? t, uint a, int r, IShellItem? v) => 0;
        public int UpdateProgress(uint total, uint completed) => token.IsCancellationRequested ? unchecked((int)0x80004004) : 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
