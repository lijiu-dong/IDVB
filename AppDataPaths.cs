namespace IDVBuff;

/// <summary>Separates manual test builds from the user's production data.</summary>
public static class AppDataPaths
{
#if IDVB_EMBEDDED
    public const bool IsTestBuild = false;
    public const string ProductDirectoryName = "IDVB-Embedded";
    public const string DisplayName = "Identity Vision Bridge";
#elif IDVBUFF_TEST_BUILD
    public const bool IsTestBuild = true;
    public const string ProductDirectoryName = "IDVB-Test";
    private const string LegacyProductDirectoryName = "IDVBuff-Test";
    public const string DisplayName = "Identity Vision Bridge（测试版）";
#else
    public const bool IsTestBuild = false;
    public const string ProductDirectoryName = "IDVB";
    private const string LegacyProductDirectoryName = "IDVBuff";
    public const string DisplayName = "Identity Vision Bridge";
#endif

    private static readonly Lazy<string> ResolvedRoot = new(ResolveRootDirectory);
    private static string? _memoryTestRoot = null;
    public static string RootDirectory => ResolvedRoot.Value;
    internal static bool IsMemoryTest => _memoryTestRoot is not null;

    // Startup logging must not trigger migration, but must honor test isolation.
    internal static string RootBeforeLegacyMigration
    {
        get
        {
#if IDVB_UNIT_TEST || IDVB_EMBEDDED
            return RootDirectory;
#else
            return _memoryTestRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductDirectoryName);
#endif
        }
    }

#if IDVB_DEVELOPMENT_BUILD || IDVB_UNIT_TEST
    internal static void ConfigureMemoryTestRoot(string[] arguments)
    {
        const string option = "--idvb-memory-test-root";
        var indexes = arguments.Select((value, index) => (value, index))
            .Where(item => item.value.Equals(option, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index).ToArray();
        if (indexes.Length == 0) return;
        if (indexes.Length != 1 || indexes[0] + 1 >= arguments.Length)
            throw new InvalidOperationException("内存测试必须指定唯一的隔离数据目录。");
        if (ResolvedRoot.IsValueCreated || _memoryTestRoot is not null)
            throw new InvalidOperationException("必须在首次访问应用数据之前配置隔离目录。");
        _memoryTestRoot = ValidateMemoryTestRoot(arguments[indexes[0] + 1]);
    }

    internal static string ValidateMemoryTestRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("内存测试目录必须是绝对路径。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "IDVB-MemoryReplay"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
            throw new InvalidOperationException("内存测试目录必须是临时 IDVB-MemoryReplay 下独立的 GUID 目录。");
        if (!Directory.Exists(full))
            throw new InvalidOperationException("请先创建并准备独立的内存测试数据目录。");
        for (var entry = new DirectoryInfo(full); entry is not null; entry = entry.Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("内存测试目录及其父目录不能使用目录链接。");
        return full;
    }
#endif

    private static string ResolveRootDirectory()
    {
        if (_memoryTestRoot is not null) return _memoryTestRoot;
#if IDVB_EMBEDDED
        // Embedded processing must never migrate or share the desktop application's data.
        return Path.Combine(Path.GetTempPath(), "IDVB-Embedded", Environment.ProcessId.ToString());
#elif IDVB_UNIT_TEST
        // Unit tests must never migrate or write the user's installed data.
        return Path.Combine(Path.GetTempPath(), $"IDVB-UnitTests-{Environment.ProcessId}");
#else
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var targetDirectory = Path.Combine(localAppData, ProductDirectoryName);
        var legacyDirectory = Path.Combine(localAppData, LegacyProductDirectoryName);

        try
        {
            if (!Directory.Exists(legacyDirectory))
                return targetDirectory;

            if (!Directory.Exists(targetDirectory))
            {
                Directory.Move(legacyDirectory, targetDirectory);
                return targetDirectory;
            }

            MoveMissingLegacyEntries(legacyDirectory, targetDirectory);
        }
        catch
        {
            // Keeping the old directory intact is safer than risking user data.
        }

        return targetDirectory;
#endif
    }

    internal static readonly object CacheIoGate = new();
    private static readonly Dictionary<string, int> ActiveCachePaths = new(StringComparer.OrdinalIgnoreCase);

    internal static IDisposable ProtectCachePath(string path)
    {
        path = Path.GetFullPath(path);
        lock (CacheIoGate)
            ActiveCachePaths[path] = ActiveCachePaths.GetValueOrDefault(path) + 1;
        return new CachePathLease(path);
    }

    internal static string[] GetActiveCachePaths()
    {
        lock (CacheIoGate) return ActiveCachePaths.Keys.ToArray();
    }

    private sealed class CachePathLease(string path) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (CacheIoGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (--ActiveCachePaths[path] == 0) ActiveCachePaths.Remove(path);
            }
        }
    }

    private static void MoveMissingLegacyEntries(
        string legacyDirectory,
        string targetDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(legacyDirectory))
        {
            var destinationPath = Path.Combine(
                targetDirectory,
                Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                continue;

            if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, destinationPath);
            else
                File.Move(sourcePath, destinationPath);
        }
    }
}
