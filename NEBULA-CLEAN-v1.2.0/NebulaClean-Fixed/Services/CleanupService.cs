using NebulaClean.Models;
using System.IO;

namespace NebulaClean.Services;

public sealed record LargeFileItem(string Name, string Path, long SizeBytes)
{
    public string SizeText => CleanupCategory.FormatBytes(SizeBytes);
}

public sealed class CleanupService
{
    public IReadOnlyList<CleanupCategory> CreateCategories()
    {
        string userTemp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string? userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] edgeCache = Directory.Exists(Path.Combine(local, "Microsoft", "Edge", "User Data"))
            ? Directory.GetDirectories(Path.Combine(local, "Microsoft", "Edge", "User Data"), "*", SearchOption.TopDirectoryOnly)
                .SelectMany(profile => new[] { Path.Combine(profile, "Cache"), Path.Combine(profile, "Code Cache"), Path.Combine(profile, "GPUCache") }).ToArray()
            : [];
        string[] chromeCache = Directory.Exists(Path.Combine(local, "Google", "Chrome", "User Data"))
            ? Directory.GetDirectories(Path.Combine(local, "Google", "Chrome", "User Data"), "*", SearchOption.TopDirectoryOnly)
                .SelectMany(profile => new[] { Path.Combine(profile, "Cache"), Path.Combine(profile, "Code Cache"), Path.Combine(profile, "GPUCache") }).ToArray()
            : [];

        var categories = new List<CleanupCategory>
        {
            Cat("Временные файлы пользователя", "Временные данные приложений текущего пользователя", userTemp, true),
            Cat("Системные временные файлы", "Временные файлы Windows (часть файлов может требовать запуск от администратора)", Path.Combine(windows, "Temp"), true),
            Cat("Кэш миниатюр Windows", "Кэш эскизов, который Windows может создать заново", Path.Combine(local, "Microsoft", "Windows", "Explorer"), true, "thumbcache_"),
            Cat("Отчёты об ошибках Windows", "Локальные дампы и отчёты Windows Error Reporting", Path.Combine(programData, "Microsoft", "Windows", "WER"), true),
            Cat("Кэш шейдеров DirectX", "Пересоздаваемый кэш графических шейдеров", Path.Combine(local, "D3DSCache"), true),
            Cat("Кэш доставки обновлений", "Кэш оптимизации доставки обновлений Windows", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), false),
            Cat("Загрузки Windows Update", "Загруженные пакеты обновлений; Windows при необходимости скачает их снова", Path.Combine(windows, "SoftwareDistribution", "Download"), false),
            Cat("Кэш Microsoft Edge", "Кэш браузера — страницы могут загружаться медленнее после очистки", edgeCache, false),
            Cat("Кэш Google Chrome", "Кэш браузера — страницы могут загружаться медленнее после очистки", chromeCache, false)
        };
        return categories.Where(c => c.Paths.Length > 0).ToList();
    }

    private static CleanupCategory Cat(string name, string description, string path, bool selected, string? filenamePrefix = null) =>
        Cat(name, description, new[] { path }, selected, filenamePrefix);

    private static CleanupCategory Cat(string name, string description, string[] paths, bool selected, string? filenamePrefix = null) => new()
    {
        Name = name, Description = description, LocationLabel = paths.Length == 1 ? paths[0] : $"{paths.Length} папок кэша",
        Paths = paths, IsSelected = selected, FilenamePrefix = filenamePrefix
    };

    public async Task ScanAsync(IEnumerable<CleanupCategory> categories, CancellationToken token = default)
    {
        await Task.Run(() =>
        {
            foreach (var category in categories)
            {
                token.ThrowIfCancellationRequested();
                long total = 0; int count = 0;
                foreach (string path in category.Paths)
                {
                    foreach (string file in EnumerateFilesSafe(path, SearchOption.AllDirectories, token))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(file);
                            if (!info.Exists || !MatchesCategory(category, info.Name)) continue;
                            total = checked(total + info.Length); count++;
                        }
                        catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { } catch (OverflowException) { }
                    }
                }
                category.SizeBytes = total; category.FileCount = count;
            }
        }, token);
    }

    public async Task<(long FreedBytes, int DeletedFiles, int SkippedFiles)> CleanAsync(IEnumerable<CleanupCategory> categories, CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            long freed = 0; int deleted = 0; int skipped = 0;
            foreach (var category in categories.Where(c => c.IsSelected))
            {
                foreach (string path in category.Paths)
                {
                    foreach (string file in EnumerateFilesSafe(path, SearchOption.AllDirectories, token))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(file);
                            if (!info.Exists || !MatchesCategory(category, info.Name)) continue;
                            long size = info.Length; info.Delete(); freed += size; deleted++;
                        }
                        catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; } catch (System.Security.SecurityException) { skipped++; }
                    }
                }
            }
            return (freed, deleted, skipped);
        }, token);
    }

    public async Task<List<LargeFileItem>> FindLargeFilesAsync(string root, long minimumBytes, CancellationToken token = default, IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var found = new List<LargeFileItem>();
            var pending = new Stack<string>(); pending.Push(root);
            int visited = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                if (IsExcludedFromLargeScan(dir)) continue;
                try
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        token.ThrowIfCancellationRequested();
                        try { var fi = new FileInfo(file); if (fi.Exists && fi.Length >= minimumBytes) found.Add(new LargeFileItem(fi.Name, fi.FullName, fi.Length)); }
                        catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
                    }
                    foreach (string sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        try { var di = new DirectoryInfo(sub); if ((di.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub); }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
                if (++visited % 250 == 0) progress?.Report($"Проверено папок: {visited:N0} · найдено крупных файлов: {found.Count:N0}");
            }
            return found.OrderByDescending(f => f.SizeBytes).Take(1000).ToList();
        }, token);
    }

    private static bool IsExcludedFromLargeScan(string path)
    {
        string normalized = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string[] excluded = { @"C:\Windows\WinSxS\", @"C:\Windows\Installer\", @"C:\Windows\System32\", @"C:\Windows\SysWOW64\", @"C:\$Recycle.Bin\", @"C:\System Volume Information\" };
        return excluded.Any(x => normalized.StartsWith(x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesCategory(CleanupCategory category, string filename) =>
        string.IsNullOrWhiteSpace(category.FilenamePrefix) || filename.StartsWith(category.FilenamePrefix, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateFilesSafe(string path, SearchOption option, CancellationToken token)
    {
        if (!Directory.Exists(path)) yield break;
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            string[] files = []; string[] dirs = [];
            try { files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
            foreach (string file in files) yield return file;
            try { dirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (System.Security.SecurityException) { }
            foreach (string sub in dirs)
            {
                try { var di = new DirectoryInfo(sub); if ((di.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(sub); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
