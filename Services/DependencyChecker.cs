using System.IO;
using System.Text.RegularExpressions;
using TyranoToolKit.Models;

namespace TyranoToolKit.Services;

/// <summary>
/// 检测 java/javac/adb/cordova/gradle 等构建依赖是否可用。
/// </summary>
public static class DependencyChecker
{
    /// <summary>
    /// 检测全部依赖项。每完成一个通过 progress 回调报告进度。
    /// </summary>
    public static async Task<List<DependencyStatus>> CheckAll(
        IProgress<(int done, int total, DependencyStatus dep)>? progress = null)
    {
        var result = new List<DependencyStatus>();
        var checks = new (string name, Func<Task<DependencyStatus>> check)[]
        {
            ("node", CheckNode),
            ("npm", CheckNpm),
            ("java", CheckJava),
            ("javac", CheckJavac),
            ("cordova", CheckCordova),
            ("android-sdk", CheckAndroidSdk),
            ("gradle", CheckGradle),
        };
        int total = checks.Length;
        int done = 0;
        foreach (var (name, check) in checks)
        {
            var dep = await check();
            result.Add(dep);
            done++;
            progress?.Report((done, total, dep));
        }
        return result;
    }

    private static async Task<DependencyStatus> CheckJava()
    {
        // 只用工具安装的 JDK（不回退到系统 Java，避免高版本误导）
        var javaHome = AndroidSdkService.GetToolInstalledJavaHome();
        if (javaHome == null)
            return new DependencyStatus { Name = "java", Ok = false, Error = "未安装，点击\"安装 JDK\"自动下载", Description = "Java 运行时（JRE/JDK，仅支持工具安装的 JDK 17）" };

        var cmd = Path.Combine(javaHome, "bin", "java.exe");
        var env = new Dictionary<string, string?> { ["JAVA_HOME"] = javaHome,
            ["Path"] = (Environment.GetEnvironmentVariable("Path") ?? "") + ";" + Path.Combine(javaHome, "bin") };
        var (code, stdout, stderr) = await ProcessRunner.RunSync(cmd, "-version", env, 8000);
        var text = (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
        var ok = code == 0;
        if (!ok)
            return new DependencyStatus { Name = "java", Ok = false, Error = "JDK 安装异常，请重新安装", Description = "Java 运行时（JRE/JDK）" };

        var verStr = ExtractJavaVersion(text);
        return new DependencyStatus { Name = "java", Ok = true, Version = verStr, Description = "Java 运行时（JRE/JDK）" };
    }

    private static async Task<DependencyStatus> CheckJavac()
    {
        var javaHome = AndroidSdkService.GetToolInstalledJavaHome();
        if (javaHome == null)
            return new DependencyStatus { Name = "javac", Ok = false, Error = "未安装，点击\"安装 JDK\"自动下载", Description = "Java 编译器（JDK，仅支持工具安装的 JDK 17）" };

        var cmd = Path.Combine(javaHome, "bin", "javac.exe");
        var env = new Dictionary<string, string?> { ["JAVA_HOME"] = javaHome,
            ["Path"] = (Environment.GetEnvironmentVariable("Path") ?? "") + ";" + Path.Combine(javaHome, "bin") };
        var (code, _, stderr) = await ProcessRunner.RunSync(cmd, "-version", env, 8000);
        var text = stderr.Trim();
        var ok = code == 0;
        if (!ok)
            return new DependencyStatus { Name = "javac", Ok = false, Error = "JDK 安装异常，请重新安装", Description = "Java 编译器（JDK）" };

        var verStr = ExtractJavaVersion(text);
        return new DependencyStatus { Name = "javac", Ok = true, Version = verStr, Description = "Java 编译器（JDK）" };
    }

    private static async Task<DependencyStatus> CheckAndroidSdk()
    {
        // 检测 AndroidSdkService 安装的 SDK 或系统已有 SDK
        var installed = AndroidSdkService.IsInstalled();
        var sdkPath = AndroidSdkService.SdkRoot;

        // 也检测环境变量指向的 SDK
        var envAndroidHome = Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "";
        if (!installed && !string.IsNullOrEmpty(envAndroidHome) && Directory.Exists(envAndroidHome))
        {
            var envAdb = Path.Combine(envAndroidHome, "platform-tools", "adb.exe");
            var envPlatforms = Path.Combine(envAndroidHome, "platforms");
            installed = File.Exists(envAdb) && Directory.Exists(envPlatforms) &&
                Directory.GetDirectories(envPlatforms, "android-*").Length > 0 &&
                // build-tools 也必须完整，否则构建会报 "No installed build tools found"
                File.Exists(Path.Combine(envAndroidHome, "build-tools",
                    AndroidSdkService.RequiredBuildToolsVersion, "aapt2.exe"));
            if (installed) sdkPath = envAndroidHome;
        }

        return new DependencyStatus
        {
            Name = "android-sdk",
            Ok = installed,
            Version = installed ? sdkPath : "",
            Error = installed ? "" : "未安装，点击\"安装 SDK\"自动下载",
            Description = "Android SDK（platform-tools + platforms + build-tools）"
        };
    }

    private static async Task<DependencyStatus> CheckCordova()
    {
        // 用完整路径调用 cordova，避免 npm 全局 bin 不在 PATH 中
        var cordovaBin = await ApkBuilderService.GetCordovaBinPathAsync();
        var (code, stdout, _) = await ProcessRunner.RunSync(cordovaBin, "-v", 15000);
        var text = stdout.Trim();
        var ok = code == 0;
        return new DependencyStatus
        {
            Name = "cordova",
            Ok = ok,
            Version = ok ? text : "",
            Error = ok ? "" : text,
            Description = "Cordova 命令行"
        };
    }

    private static async Task<DependencyStatus> CheckGradle()
    {
        // 优先用工具安装的 Gradle
        var gradleBin = AndroidSdkService.GetGradleBinPath();
        // Gradle 需要 JAVA_HOME 才能运行
        var javaHome = AndroidSdkService.GetJavaHome();

        // Gradle.bat 不存在 = 没下载
        if (gradleBin == null)
        {
            return new DependencyStatus
            {
                Name = "gradle",
                Ok = false,
                Error = $"未安装，点击\"安装 Gradle\"自动下载（要求 {AndroidSdkService.RequiredGradleVersion}+）",
                Description = $"Gradle 构建工具（最低版本 {AndroidSdkService.RequiredGradleVersion}，用于生成构建 wrapper；已下载的发行包会供构建离线复用，需先安装 JDK）"
            };
        }

        // Gradle 已下载但 JDK 缺失
        if (javaHome == null)
        {
            return new DependencyStatus
            {
                Name = "gradle",
                Ok = false,
                Version = "",
                Error = "Gradle 已下载，但缺少 JDK，请先安装 JDK",
                Description = $"Gradle 构建工具（最低版本 {AndroidSdkService.RequiredGradleVersion}，用于生成构建 wrapper；已下载的发行包会供构建离线复用，需先安装 JDK）"
            };
        }

        var env = new Dictionary<string, string?>
        {
            ["JAVA_HOME"] = javaHome,
            ["Path"] = (Environment.GetEnvironmentVariable("Path") ?? "") + ";" + Path.Combine(javaHome, "bin")
        };
        var (code, stdout, stderr) = await ProcessRunner.RunSync(gradleBin, "-v", env, 15000);
        var text = (string.IsNullOrEmpty(stdout) ? stderr : stdout).Trim();
        var ok = code == 0;
        var version = ok ? ExtractMatch(text, @"Gradle\s+([\d.]+)") : "";

        // 版本低于构建要求（如还残留着旧版 Gradle 8.9）→ 标记为需升级
        if (ok && !string.IsNullOrEmpty(version) &&
            Version.TryParse(version, out var gradleVer) &&
            Version.TryParse(AndroidSdkService.RequiredGradleVersion, out var requiredVer) &&
            gradleVer < requiredVer)
        {
            return new DependencyStatus
            {
                Name = "gradle",
                Ok = false,
                Version = version,
                Error = $"版本过低（当前 {version}），点击\"安装 Gradle\"升级到 {AndroidSdkService.RequiredGradleVersion}",
                Description = $"Gradle 构建工具（最低版本 {AndroidSdkService.RequiredGradleVersion}，用于生成构建 wrapper；已下载的发行包会供构建离线复用，需先安装 JDK）"
            };
        }

        return new DependencyStatus
        {
            Name = "gradle",
            Ok = ok,
            Version = version,
            Error = ok ? "" : $"gradle -v 失败：{(string.IsNullOrEmpty(text) ? "未知错误" : text)}",
            Description = $"Gradle 构建工具（最低版本 {AndroidSdkService.RequiredGradleVersion}，用于生成构建 wrapper；已下载的发行包会供构建离线复用，需先安装 JDK）"
        };
    }

    private static string ExtractJavaVersion(string text)
    {
        var m = Regex.Match(text, @"version\s+""(\d+(?:\.\d+)?(?:\.\d+)?(?:_\d+)?)-?[^""]*""");
        if (!m.Success)
            m = Regex.Match(text, @"(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : text.Split('\n')[0];
    }

    private static string ExtractMatch(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>
    /// 从 Java 版本字符串提取主版本号（如 "17.0.13" → 17, "1.8.0" → 8）
    /// </summary>
    private static int ExtractJavaMajor(string versionStr)
    {
        if (string.IsNullOrEmpty(versionStr)) return 0;
        // Java 9+ 版本格式：17.0.13
        // Java 8 及以下格式：1.8.0
        if (versionStr.StartsWith("1."))
        {
            var parts = versionStr.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var v8)) return v8;
        }
        else
        {
            var parts = versionStr.Split('.');
            if (parts.Length >= 1 && int.TryParse(parts[0], out var v)) return v;
        }
        return 0;
    }

    private static async Task<DependencyStatus> CheckNode()
    {
        var nodePath = AndroidSdkService.GetNodePath();
        var (cmd, args) = nodePath != null
            ? (Path.Combine(nodePath, "node.exe"), "-v")
            : ("node", "-v");
        var (code, stdout, stderr) = await ProcessRunner.RunSync(cmd, args, null, 8000);
        var ok = code == 0;
        var output = (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
        if (ok)
        {
            var ver = ExtractMatch(output, @"v(\d+\.\d+\.\d+)");
            return new DependencyStatus { Name = "node", Ok = true, Version = $"v{ver}", Description = "Node.js 运行时" };
        }
        return new DependencyStatus { Name = "node", Ok = false, Version = "", Description = "点击\"安装 Node.js\"自动下载" };
    }

    private static async Task<DependencyStatus> CheckNpm()
    {
        var nodePath = AndroidSdkService.GetNodePath();
        var (cmd, args) = nodePath != null
            ? (Path.Combine(nodePath, "npm.cmd"), "-v")
            : ("npm", "-v");
        var (code, stdout, stderr) = await ProcessRunner.RunSync(cmd, args, null, 8000);
        var ok = code == 0;
        var output = (string.IsNullOrEmpty(stderr) ? stdout : stderr).Trim();
        if (ok)
        {
            var ver = ExtractMatch(output, @"(\d+\.\d+\.\d+)");
            return new DependencyStatus { Name = "npm", Ok = true, Version = ver, Description = "包管理器" };
        }
        return new DependencyStatus { Name = "npm", Ok = false, Version = "", Description = "随 Node.js 一起安装" };
    }
}
