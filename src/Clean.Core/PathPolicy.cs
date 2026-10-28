namespace Clean.Core;

public sealed class PathPolicy(IEnumerable<string>? protectedRoots = null)
{
    private readonly string[] _protected = (protectedRoots ?? []).Select(Normalize).ToArray();
    public static bool IsValid(string path)
    {
        try { Normalize(path); return true; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空。");
        path = path.Replace('/', '\\');
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).Contains(':') || path.IndexOfAny(['*', '?', '"', '<', '>', '|', '\0']) >= 0)
            throw new ArgumentException("只支持盘符开头的本地绝对路径，不支持网络、设备路径或数据流。");
        foreach (var segment in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (segment.EndsWith(' ') || segment.EndsWith('.') || segment.Any(char.IsControl) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new ArgumentException("不支持有歧义的 Windows 路径。");
        }
        var full = Path.GetFullPath(path);
        return full.Length == 3 ? full : full.TrimEnd('\\');
    }
    public static bool Within(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }
    public bool IsProtected(string path) => _protected.Any(root => Within(path, root));
    public bool Allows(string path, string root, IEnumerable<string> exclusions) =>
        Within(path, root) && !IsProtected(path) && !exclusions.Any(x => Within(path, x));
}
