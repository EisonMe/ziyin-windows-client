namespace ZiYin.Service;

public sealed class FileStore(ServiceSettings settings)
{
    public const int MaxBytes = 10 * 1024 * 1024;
    private readonly string root = Path.GetFullPath(settings.FileRoot).TrimEnd(Path.DirectorySeparatorChar);
    public string Resolve(string? relative, bool allowRoot = false)
    {
        relative = (relative ?? "").Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/').Any(s => s is ".." or "." || s.EndsWith('.') || s.EndsWith(' ')))
            throw new ArgumentException("只允许文件根目录内的相对路径。");
        if (relative.Length == 0 && !allowRoot) throw new ArgumentException("缺少文件路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new ArgumentException("路径超出文件根目录。");
        var current = root;
        CheckLink(current);
        foreach (var component in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        { if (component == ".") continue; current = Path.Combine(current, component); CheckLink(current); }
        return full;
    }
    private static void CheckLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("文件 API 不允许符号链接或目录联接。");
    }
    public object[] List(string? path) => new DirectoryInfo(Resolve(path, true)).EnumerateFileSystemInfos().Take(1000)
        .Select(f => (object)new { name = f.Name, directory = f is DirectoryInfo, link = (f.Attributes & FileAttributes.ReparsePoint) != 0,
            size = f is FileInfo file ? file.Length : 0, modifiedAt = f.LastWriteTimeUtc }).ToArray();
    public async Task Write(string path, Stream input, CancellationToken cancellation)
    {
        var full = Resolve(path);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException();
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int total = 0, count;
                while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
                { total += count; if (total > MaxBytes) throw new ArgumentException("上传上限为 10 MiB。"); await output.WriteAsync(buffer.AsMemory(0, count), cancellation); }
            }
            Resolve(path); File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
