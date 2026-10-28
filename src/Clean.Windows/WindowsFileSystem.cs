using System.Security.Cryptography;
using System.Text;
using Clean.Core;
using Microsoft.Win32.SafeHandles;

namespace Clean.Windows;

public sealed class WindowsFileSystem : IFileSystem
{
    [ThreadStatic] private static EnumerationContext? _enumeration;
    private sealed record EnumerationContext(WindowsFileSystem Owner, string Directory, string Fingerprint);
    public static PathPolicy CreatePolicy(string stateDirectory) => new(new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        stateDirectory, AppContext.BaseDirectory
    }.Where(x => !string.IsNullOrWhiteSpace(x)).Concat(DriveInfo.GetDrives().SelectMany(x =>
        new[] { Path.Combine(x.Name, "$Recycle.Bin"), Path.Combine(x.Name, "System Volume Information") })));

    public void ValidateRoot(string path)
    {
        path = PathPolicy.Normalize(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            throw new PolicyException("首版仅支持已就绪的本地固定磁盘。");
        using var guards = AncestorGuards.Acquire(path, includeLeaf: true, strict: false);
    }
    public IEnumerable<FileEntry> Enumerate(string directory)
    {
        // Hold ancestors while enumerating: a junction cannot replace a checked directory.
        using var guard = AncestorGuards.Acquire(directory, true, false);
        var previous = _enumeration;
        _enumeration = new(this, PathPolicy.Normalize(directory), guard.Fingerprint);
        try
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
            { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false }))
                yield return new(info.FullName, (info.Attributes & FileAttributes.Directory) != 0,
                    (info.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        finally { _enumeration = previous; }
    }
    public FileSnapshot ReadSnapshot(string path)
    {
        path = PathPolicy.Normalize(path);
        // Reuse only the directory handles that the synchronous streaming enumerator still holds.
        // Execution never uses this context, and always acquires fresh, stricter ancestor guards.
        if (_enumeration is { } context && ReferenceEquals(context.Owner, this) &&
            string.Equals(context.Directory, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
        {
            using var scanned = NativeFiles.Open(path);
            return NativeFiles.Snapshot(scanned, path, context.Fingerprint);
        }
        using var ancestors = AncestorGuards.Acquire(path, false, false);
        using var handle = NativeFiles.Open(path);
        return NativeFiles.Snapshot(handle, path, ancestors.Fingerprint);
    }
    public ActionOutcome ExecuteVerified(FileSnapshot expected, CleanupMode mode, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(ItemStatus.NotExecuted, ErrorKind.Cancelled, "已取消。");
        try
        {
            using var ancestors = AncestorGuards.Acquire(expected.Path, false, true);
            using var handle = NativeFiles.Open(expected.Path, mode == CleanupMode.Permanent ? 0x10080u : 0x80u,
                mode == CleanupMode.Permanent ? 0u : 5u);
            var current = NativeFiles.Snapshot(handle, expected.Path, ancestors.Fingerprint);
            if (current != expected) return new(ItemStatus.Skipped, ErrorKind.Changed, "最终句柄复核发现文件或目录变化。");
            if (current.LinkCount != 1 || (current.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                return new(ItemStatus.Skipped, ErrorKind.Policy, "只读、系统、重解析或多硬链接文件不执行清理。");
            if (cancellationToken.IsCancellationRequested) return new(ItemStatus.NotExecuted, ErrorKind.Cancelled, "已取消。");
            if (mode == CleanupMode.Permanent)
            {
                NativeFiles.Delete(handle);
                return new(ItemStatus.Succeeded, ErrorKind.None, "已永久删除，无法由本应用恢复。");
            }
            return ShellRecycleBin.Recycle(expected, () => ReadSnapshot(expected.Path) == expected, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new(e is PolicyException or FileNotFoundException or DirectoryNotFoundException ? ItemStatus.Skipped : ItemStatus.Failed,
                Errors.Classify(e), Errors.Describe(e));
        }
    }
    private sealed class AncestorGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        public string Fingerprint { get; private set; } = "";
        public static AncestorGuards Acquire(string path, bool includeLeaf, bool strict)
        {
            var result = new AncestorGuards();
            try
            {
                var directory = includeLeaf ? PathPolicy.Normalize(path) : Path.GetDirectoryName(PathPolicy.Normalize(path))!;
                var paths = new Stack<string>();
                while (directory is not null) { paths.Push(directory); directory = Path.GetDirectoryName(directory); }
                var ids = new StringBuilder();
                foreach (var parent in paths)
                {
                    var handle = NativeFiles.Open(parent, 0x80, strict ? 1u : 3u);
                    result._handles.Add(handle);
                    NativeFiles.CheckPath(handle, parent, true);
                    ids.Append(NativeFiles.Identity(handle)).Append('/');
                }
                result.Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ids.ToString())));
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); }
    }
}
