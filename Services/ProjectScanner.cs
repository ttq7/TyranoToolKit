using System.IO;
using Microsoft.Win32;
using TyranoToolKit.Models;

namespace TyranoToolKit.Services;

/// <summary>
/// 扫描本地与 Steam 候选路径下的 Tyrano 游戏项目。
/// </summary>
public static class ProjectScanner
{
    /// <summary>
    /// 从注册表读取 Steam 安装路径。
    /// </summary>
    public static string? GetSteamPath()
    {
        try
        {
            var val = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            if (val is string s && !string.IsNullOrEmpty(s) && Directory.Exists(s))
                return s;
        }
        catch { }
        // 回退到默认路径
        var defaultPath = @"C:\Program Files (x86)\Steam";
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }

    /// <summary>
    /// 获取 Steam library 列表（含默认路径和自定义库）。
    /// </summary>
    private static List<string> EnumerateSteamLibraries()
    {
        var result = new List<string>();
        var steamPath = GetSteamPath();
        if (steamPath == null) return result;

        // 默认库
        var defaultLib = Path.Combine(steamPath, "steamapps");
        if (Directory.Exists(defaultLib))
            result.Add(steamPath);

        // 自定义库
        var libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libFile)) return result;

        try
        {
            var lines = File.ReadAllLines(libFile);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                var idx = trimmed.IndexOf('"', 5);
                if (idx < 0 || idx + 1 >= trimmed.Length) continue;
                var start = idx + 1;
                var end = trimmed.IndexOf('"', start);
                if (end < 0) continue;
                var path = trimmed.Substring(start, end - start).Replace(@"\\", @"\");
                if (Directory.Exists(path))
                    result.Add(path);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 获取 TyranoBuilder 引擎的 myproject 路径（默认 Steam）。
    /// </summary>
    public static string GetDefaultEnginePath()
    {
        // 优先：本地引擎路径（exe 附近的 myproject）
        var local = FindLocalEnginePath();
        if (!string.IsNullOrEmpty(local))
            return local;

        // 回退：Steam 路径
        foreach (var lib in EnumerateSteamLibraries())
        {
            var tyranoProject = Path.Combine(lib, "steamapps", "common", "TyranoBuilder", "myproject");
            if (Directory.Exists(tyranoProject))
                return tyranoProject;
        }
        return "";
    }

    /// <summary>
    /// 查找本地引擎的 myproject 路径。
    /// 从 exe 向上查找 + 扫描常见安装目录（含非标准路径名如 tyranobuilder_v204b_win_std）。
    /// </summary>
    public static string? FindLocalEnginePath()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 从 exe 所在目录向上逐级查找 myproject
        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                if (seen.Add(dir.FullName))
                {
                    var candidate = Path.Combine(dir.FullName, "myproject");
                    if (Directory.Exists(candidate))
                        return candidate;
                    // 也检查子目录中包含 tyranobuilder 或暴龙 的文件夹
                    var sub = FindMyProjectInChildren(dir.FullName, seen);
                    if (sub != null) return sub;
                }
                dir = dir.Parent;
            }
        }
        catch { }

        // 2. 扫描常见安装目录
        var searchRoots = new List<string>();
        // Program Files
        searchRoots.Add(@"C:\Program Files");
        searchRoots.Add(@"C:\Program Files (x86)");
        // 用户目录
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        searchRoots.Add(userProfile);
        // 桌面
        searchRoots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        // 下载（常见路径）
        searchRoots.Add(Path.Combine(userProfile, "Downloads"));
        // D:\QQ文件 等常见下载位置
        searchRoots.Add(@"D:\");
        searchRoots.Add(@"E:\");

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var found = FindMyProjectInChildren(root, seen, maxDepth: 3);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// 在指定目录的子目录中查找 myproject（支持非标准路径名）。
    /// </summary>
    private static string? FindMyProjectInChildren(string root, HashSet<string> seen, int maxDepth = 2)
    {
        if (maxDepth <= 0) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!seen.Add(dir)) continue;
                var name = Path.GetFileName(dir);
                // 直接检查 myproject 子目录
                var candidate = Path.Combine(dir, "myproject");
                if (Directory.Exists(candidate))
                    return candidate;
                // 递归检查包含 tyranobuilder/暴龙/tyrano 的目录名
                if (name.Contains("tyranobuilder", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("暴龙", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("tyrano", StringComparison.OrdinalIgnoreCase))
                {
                    var sub = FindMyProjectInChildren(dir, seen, maxDepth - 1);
                    if (sub != null) return sub;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 扫描指定引擎路径下的项目，返回去重后的列表。
    /// </summary>
    public static List<GameProject> ScanProjects(string? enginePath = null)
    {
        var all = new List<GameProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 用户自定义路径
        if (!string.IsNullOrEmpty(enginePath) && Directory.Exists(enginePath))
            ScanOneRoot(enginePath, "自定义", all, seen);

        // 本地引擎路径（exe 附近的 myproject）
        var localPath = FindLocalEnginePath();
        if (!string.IsNullOrEmpty(localPath) && localPath != enginePath)
            ScanOneRoot(localPath, "本地", all, seen);

        // Steam 路径
        foreach (var lib in EnumerateSteamLibraries())
        {
            var root = Path.Combine(lib, "steamapps", "common", "TyranoBuilder", "myproject");
            ScanOneRoot(root, "Steam", all, seen);
        }

        return all;
    }

    // TyranoBuilder 系统目录名，扫描时排除
    private static readonly HashSet<string> SystemDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "resources", "lang", "lang_rider", "locales",
        "dlc", "export", "tmp", "tb_dialog", "TyranoToolKit",
        ".git", ".vs", "bin", "obj", "publish",
    };

    private static void ScanOneRoot(string root, string source,
        List<GameProject> all, HashSet<string> seen)
    {
        if (!Directory.Exists(root)) return;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(root); }
        catch { return; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
            if (SystemDirNames.Contains(name)) continue;

            var index = Path.Combine(dir, "index.html");
            var hasIndex = File.Exists(index);
            // 只列出有 index.html 的真实项目
            if (!hasIndex) continue;

            if (!seen.Add(dir)) continue;

            all.Add(new GameProject
            {
                Name = name,
                Path = dir,
                HasIndex = hasIndex,
                Source = source,
            });
        }
    }
}
