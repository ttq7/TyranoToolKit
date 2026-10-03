using System.IO;
using System.IO.Compression;
using System.Net.Http;
using TyranoToolKit.Models;

namespace TyranoToolKit.Services;

/// <summary>
/// Android SDK 下载进度。
/// </summary>
public class SdkProgress
{
    public string Stage { get; set; } = "";  // downloading/extracting/installing/done/error
    public double Percent { get; set; }
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public double SpeedKBs { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Android SDK 自动下载与安装服务。
/// 流程：下载 commandline-tools zip → 解压 → sdkmanager 安装 platform-tools/platforms/build-tools。
/// </summary>
public class AndroidSdkService : IDisposable
{
    // Google 官方源 + 国内镜像回退
    private static readonly string[] DownloadUrls =
    {
        "https://mirrors.tuna.tsinghua.edu.cn/android/repository/commandlinetools-win-11076708_latest.zip",
        "https://dl.google.com/android/repository/commandlinetools-win-11076708_latest.zip",
    };

    public static readonly string SdkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "android-sdk");

    private readonly HttpClient _client;
    private CancellationTokenSource? _cts;

    public AndroidSdkService()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 获取 sdkmanager.bat 完整路径。
    /// </summary>
    public static string? GetSdkManagerPath()
    {
        var p = Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", "sdkmanager.bat");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// 预写 SDK 许可证哈希文件（%SDK%\licenses\）。
    /// sdkmanager 启动时检测这些文件，命中则跳过所有交互式许可确认。
    /// 这是 Android 官方 CI 部署文档推荐的做法；管道传 y 无效，
    /// 因为 sdkmanager 通过控制台（而非 stdin）读取确认输入。
    /// </summary>
    private static void WriteSdkLicenses()
    {
        try
        {
            var licDir = Path.Combine(SdkRoot, "licenses");
            Directory.CreateDirectory(licDir);

            // android-sdk-license 的全部历史哈希（sdkmanager 按文件内容是否包含任一哈希判断）
            var sdkLicense = Path.Combine(licDir, "android-sdk-license");
            if (!File.Exists(sdkLicense))
            {
                File.WriteAllLines(sdkLicense, new[]
                {
                    "8933bad161af4178b1185d1a37fbf41ea5269c55",
                    "d56f5187479451eabf01fb78af6dfcb131a6481e",
                    "24333f8a63b6825ea9c5514f83c2829b004d1fee",
                });
            }

            // android-sdk-preview-license（部分组件依赖）
            var previewLicense = Path.Combine(licDir, "android-sdk-preview-license");
            if (!File.Exists(previewLicense))
            {
                File.WriteAllText(previewLicense, "84831b9409646a918e30573bab4c9c91346d8abd" + Environment.NewLine);
            }
        }
        catch
        {
            // 写失败不阻断流程，后续 --licenses 步骤仍会尝试
        }
    }

    /// <summary>
    /// 下载并安装 Android SDK。
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(
        IProgress<SdkProgress> progress, IProgress<BuildLog>? log, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // 如果已安装，直接返回
        if (IsInstalled())
        {
            progress.Report(new SdkProgress { Stage = "done", Percent = 100, Message = "SDK 已安装" });
            return true;
        }

        Directory.CreateDirectory(SdkRoot);
        var zipPath = Path.Combine(SdkRoot, "commandlinetools.zip");

        // 1-2. 下载并解压 commandline-tools（已存在 sdkmanager 时跳过，避免补装组件时重复下载 150MB）
        if (GetSdkManagerPath() == null)
        {
            log?.Report(new BuildLog { Stream = "stdout", Line = "开始下载 Android commandline tools", Stage = "sdk-download" });
            bool downloaded = false;
            foreach (var url in DownloadUrls)
            {
                try
                {
                    log?.Report(new BuildLog { Stream = "stdout", Line = $"尝试下载源：{url}", Stage = "sdk-download" });
                    if (await DownloadZipAsync(url, zipPath, progress, token))
                    {
                        downloaded = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Report(new BuildLog { Stream = "stderr", Line = $"下载失败：{ex.Message}", Stage = "sdk-download" });
                }
            }
            if (!downloaded)
            {
                progress.Report(new SdkProgress { Stage = "error", Message = "所有下载源均失败，请检查网络" });
                return false;
            }

            // 解压
            progress.Report(new SdkProgress { Stage = "extracting", Percent = 0, Message = "解压中..." });
            log?.Report(new BuildLog { Stream = "stdout", Line = "解压 commandline tools", Stage = "sdk-extract" });
            var cmdlineDir = Path.Combine(SdkRoot, "cmdline-tools");
            Directory.CreateDirectory(cmdlineDir);
            // 解压到临时目录再重组为 latest 结构
            var tempDir = Path.Combine(SdkRoot, "_tmp_extract");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, tempDir);
            }
            catch (Exception ex)
            {
                // zip 损坏（下载不完整等）：删除后报错，避免留下坏文件
                try { if (File.Exists(zipPath)) File.Delete(zipPath); if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                progress.Report(new SdkProgress { Stage = "error", Message = $"commandline tools 解压失败（zip 可能已损坏）：{ex.Message}" });
                return false;
            }
            // zip 内顶层是 cmdline-tools 目录，移动到 cmdline-tools/latest
            var latestDir = Path.Combine(cmdlineDir, "latest");
            if (Directory.Exists(latestDir)) Directory.Delete(latestDir, true);
            var srcDir = Path.Combine(tempDir, "cmdline-tools");
            if (Directory.Exists(srcDir))
                Directory.Move(srcDir, latestDir);
            else
                // 有时 zip 顶层直接是 bin 等，直接移动
                Directory.Move(tempDir, latestDir);
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            try { File.Delete(zipPath); } catch { }
        }

        // 3. sdkmanager 安装必需包
        var sdkmanager = GetSdkManagerPath();
        if (sdkmanager == null)
        {
            progress.Report(new SdkProgress { Stage = "error", Message = "sdkmanager 未找到" });
            return false;
        }

        log?.Report(new BuildLog { Stream = "stdout", Line = "用 sdkmanager 安装 platform-tools, platforms;android-36, build-tools;36.0.0", Stage = "sdk-install" });
        progress.Report(new SdkProgress { Stage = "installing", Percent = 50, Message = "安装 SDK 组件（可能需要几分钟）..." });

        // 设置 ANDROID_HOME 环境变量供 sdkmanager 使用
        var env = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = SdkRoot,
            ["ANDROID_SDK_ROOT"] = SdkRoot,
        };
        // sdkmanager 需要 JAVA_HOME
        var javaHome = GetJavaHome();
        if (javaHome != null)
        {
            env["JAVA_HOME"] = javaHome;
            env["Path"] = (Environment.GetEnvironmentVariable("Path") ?? "") + ";" + Path.Combine(javaHome, "bin");
        }

        // 预写许可证文件（CI 标准做法）：sdkmanager 检测到 licenses/ 目录里的
        // 哈希文件后跳过所有交互式许可确认。管道传 y 的方式对 sdkmanager 无效
        // （它读的是控制台而非 stdin），虚拟机里因此卡在 "Accept? (y/N):" 导致 build-tools 被跳过
        WriteSdkLicenses();

        // 先接受所有许可协议（避免后续安装包时卡在许可提示）
        log?.Report(new BuildLog { Stream = "stdout", Line = "接受 SDK 许可协议...", Stage = "sdk-install" });
        try
        {
            // sdkmanager --licenses 需要交互式输入多个 y，用 cmd 的 for 循环生成 100 个 y 管道传入
            // ProcessRunner 会用 cmd.exe /c 包装，所以这里传 cmd 作为命令，args 是完整的管线
            var licCmd = "(for /l %i in (1,1,100) do @echo y) | " + sdkmanager + " --licenses";
            var (licCode, _, licErr) = await ProcessRunner.RunAsync(
                "cmd", $"/c \"{licCmd}\"", null, env,
                (s, l) => log?.Report(new BuildLog { Stream = s, Line = l, Stage = "sdk-install" }),
                token, 120000);
            if (licCode != 0)
                log?.Report(new BuildLog { Stream = "stderr", Line = $"接受许可协议返回非零：{licErr}", Stage = "sdk-install" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Report(new BuildLog { Stream = "stderr", Line = $"接受许可协议异常：{ex.Message}", Stage = "sdk-install" });
        }

        // 方案1：直接下载 SDK 组件 zip 并解压（绕过 sdkmanager 网络问题）
        progress.Report(new SdkProgress { Stage = "installing", Percent = 50, Message = "下载 platform-tools..." });
        log?.Report(new BuildLog { Stream = "stdout", Line = "直接下载 SDK 组件（清华镜像）", Stage = "sdk-install" });

        var ptUrls = new[]
        {
            "https://mirrors.tuna.tsinghua.edu.cn/android/repository/platform-tools-latest-windows.zip",
            "https://dl.google.com/android/repository/platform-tools-latest-windows.zip",
        };
        var ptZip = Path.Combine(SdkRoot, "platform-tools.zip");
        var ptDir = Path.Combine(SdkRoot, "platform-tools");
        if (!Directory.Exists(ptDir))
        {
            bool ptOk = false;
            foreach (var u in ptUrls)
            {
                try
                {
                    log?.Report(new BuildLog { Stream = "stdout", Line = $"下载 platform-tools：{u}", Stage = "sdk-install" });
                    if (await DownloadZipAsync(u, ptZip, progress, token))
                    {
                        ZipFile.ExtractToDirectory(ptZip, SdkRoot, overwriteFiles: true);
                        File.Delete(ptZip);
                        ptOk = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Report(new BuildLog { Stream = "stderr", Line = $"platform-tools 下载失败：{ex.Message}", Stage = "sdk-install" });
                }
            }
            if (!ptOk)
            {
                // 用 sdkmanager 回退
                log?.Report(new BuildLog { Stream = "stdout", Line = "直接下载失败，尝试 sdkmanager...", Stage = "sdk-install" });
                var (code, _, stderr) = await ProcessRunner.RunAsync(
                    sdkmanager, "platform-tools", null, env,
                    (s, l) => log?.Report(new BuildLog { Stream = s, Line = l, Stage = "sdk-install" }),
                    token, 600000);
                if (code != 0)
                {
                    log?.Report(new BuildLog { Stream = "stderr", Line = $"sdkmanager 安装 platform-tools 失败：{stderr}", Stage = "sdk-error" });
                    progress.Report(new SdkProgress { Stage = "error", Message = "platform-tools 安装失败" });
                    return false;
                }
            }
        }

        // 下载 platforms;android-36
        progress.Report(new SdkProgress { Stage = "installing", Percent = 65, Message = "下载 platforms;android-36..." });
        var platUrls = new[]
        {
            "https://mirrors.tuna.tsinghua.edu.cn/android/repository/platform-36_r02.zip",
            "https://dl.google.com/android/repository/platform-36_r02.zip",
        };
        var platZip = Path.Combine(SdkRoot, "platform-36.zip");
        var platDir = Path.Combine(SdkRoot, "platforms", "android-36");
        if (!Directory.Exists(platDir))
        {
            bool platOk = false;
            foreach (var u in platUrls)
            {
                try
                {
                    log?.Report(new BuildLog { Stream = "stdout", Line = $"下载 platforms;android-36：{u}", Stage = "sdk-install" });
                    if (await DownloadZipAsync(u, platZip, progress, token))
                    {
                        Directory.CreateDirectory(Path.Combine(SdkRoot, "platforms"));
                        ZipFile.ExtractToDirectory(platZip, Path.Combine(SdkRoot, "platforms"), overwriteFiles: true);
                        File.Delete(platZip);
                        platOk = true;
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Report(new BuildLog { Stream = "stderr", Line = $"platforms 下载失败：{ex.Message}", Stage = "sdk-install" });
                }
            }
            if (!platOk)
            {
                // 用 sdkmanager 回退
                log?.Report(new BuildLog { Stream = "stdout", Line = "直接下载失败，尝试 sdkmanager...", Stage = "sdk-install" });
                var platCmd = "(for /l %i in (1,1,100) do @echo y) | " + sdkmanager + " platforms;android-36";
                var (code, _, stderr) = await ProcessRunner.RunAsync(
                    "cmd", $"/c \"{platCmd}\"", null, env,
                    (s, l) => log?.Report(new BuildLog { Stream = s, Line = l, Stage = "sdk-install" }),
                    token, 600000);
                if (code != 0)
                {
                    log?.Report(new BuildLog { Stream = "stderr", Line = $"sdkmanager 安装 platforms 失败：{stderr}", Stage = "sdk-error" });
                }
            }
            // 最终校验：android-36/android.jar 必须存在（构建必需），否则终止并报错
            if (!File.Exists(Path.Combine(SdkRoot, "platforms", "android-36", "android.jar")))
            {
                log?.Report(new BuildLog { Stream = "stderr", Line = "platforms;android-36 安装失败（android.jar 缺失）", Stage = "sdk-error" });
                progress.Report(new SdkProgress { Stage = "error", Message = "platforms;android-36 安装失败，请检查网络后重试" });
                return false;
            }
        }

        // 安装 build-tools;36.0.0（直接下载 zip 的 URL 已失效，必须用 sdkmanager；
        // 目录存在但缺 aapt2.exe 时也会重装，避免半截安装导致构建失败）
        progress.Report(new SdkProgress { Stage = "installing", Percent = 80, Message = $"安装 build-tools;{RequiredBuildToolsVersion}..." });
        if (!IsBuildToolsInstalled())
        {
            log?.Report(new BuildLog { Stream = "stdout", Line = $"用 sdkmanager 安装 build-tools;{RequiredBuildToolsVersion}", Stage = "sdk-install" });
            // sdkmanager 安装包也可能提示许可，用 for 循环传 y
            var btCmd = "(for /l %i in (1,1,100) do @echo y) | " + sdkmanager + $" build-tools;{RequiredBuildToolsVersion}";
            var (btCode, _, btErr) = await ProcessRunner.RunAsync(
                "cmd", $"/c \"{btCmd}\"", null, env,
                (s, l) => log?.Report(new BuildLog { Stream = s, Line = l, Stage = "sdk-install" }),
                token, 600000);
            if (btCode != 0 || !IsBuildToolsInstalled())
            {
                log?.Report(new BuildLog { Stream = "stderr", Line = $"sdkmanager 安装 build-tools 失败：{btErr}", Stage = "sdk-error" });
                progress.Report(new SdkProgress { Stage = "error", Message = $"build-tools;{RequiredBuildToolsVersion} 安装失败，请检查网络后重试" });
                return false;
            }
        }

        // 4. 写入 ANDROID_HOME 用户环境变量
        try
        {
            EnvironmentHelper.SetUserEnvironment("ANDROID_HOME", SdkRoot);
            EnvironmentHelper.SetUserEnvironment("ANDROID_SDK_ROOT", SdkRoot);
            log?.Report(new BuildLog { Stream = "stdout", Line = $"ANDROID_HOME={SdkRoot}", Stage = "sdk-done" });
        }
        catch { }

        // 5. 下载安装 Gradle（Cordova 构建 Android 必需，版本不满足要求时也会重新下载）
        if (!IsGradleUpToDate())
        {
            log?.Report(new BuildLog { Stream = "stdout", Line = "开始下载 Gradle", Stage = "gradle-download" });
            var gradleOk = await DownloadAndInstallGradleAsync(progress, log, token);
            if (!gradleOk)
            {
                progress.Report(new SdkProgress { Stage = "error", Message = "Gradle 下载失败，请手动安装" });
                return false;
            }
        }

        progress.Report(new SdkProgress { Stage = "done", Percent = 100, Message = "SDK + Gradle 安装完成" });
        return true;
    }

    public static readonly string GradleRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "gradle");

    /// <summary>
    /// Cordova 构建要求的最低 Gradle 版本（cordova-android 15.x 的 AGP 8.10.1 要求 >= 8.11.1）
    /// </summary>
    public const string RequiredGradleVersion = "8.11.1";

    /// <summary>
    /// Cordova 构建要求的 build-tools 版本（cordova-android 15.x 模板）
    /// </summary>
    public const string RequiredBuildToolsVersion = "36.0.0";

    /// <summary>
    /// build-tools 是否已正确安装（目录存在且包含 aapt2.exe，防止半截安装被误判）。
    /// </summary>
    public static bool IsBuildToolsInstalled()
        => File.Exists(Path.Combine(SdkRoot, "build-tools", RequiredBuildToolsVersion, "aapt2.exe"));

    /// <summary>
    /// SDK 是否已安装（cmdline-tools + platform-tools + platforms + build-tools 全部存在）。
    /// </summary>
    public static bool IsInstalled()
    {
        if (!Directory.Exists(SdkRoot)) return false;
        var cmdlineLatest = Path.Combine(SdkRoot, "cmdline-tools", "latest", "bin", "sdkmanager.bat");
        if (!File.Exists(cmdlineLatest)) return false;
        var platformTools = Path.Combine(SdkRoot, "platform-tools", "adb.exe");
        if (!File.Exists(platformTools)) return false;
        // 检查是否有任意 platforms/android-XX 目录
        var platformsDir = Path.Combine(SdkRoot, "platforms");
        if (!Directory.Exists(platformsDir)) return false;
        if (Directory.GetDirectories(platformsDir, "android-*").Length == 0) return false;
        // build-tools 必须完整（缺它会导致构建报 "No installed build tools found"）
        return IsBuildToolsInstalled();
    }

    public static string? GetGradleBinPath()
    {
        if (!Directory.Exists(GradleRoot)) return null;
        // 查找 gradle-X.Y/bin/gradle.bat；存在多个版本时取版本号最新的
        string? best = null;
        Version? bestVer = null;
        foreach (var d in Directory.EnumerateDirectories(GradleRoot, "gradle-*"))
        {
            var bat = Path.Combine(d, "bin", "gradle.bat");
            if (!File.Exists(bat)) continue;
            var ver = ParseGradleVersion(Path.GetFileName(d));
            if (best == null || (ver != null && (bestVer == null || ver > bestVer)))
            {
                best = bat;
                bestVer = ver;
            }
        }
        return best;
    }

    public static bool IsGradleInstalled() => GetGradleBinPath() != null;

    /// <summary>
    /// 已安装的 Gradle 版本号（从目录名 gradle-X.Y.Z 解析），无法解析时返回 null。
    /// 直接返回目录名原文（避免 Version.ToString() 把 8.10 截断显示成 8.1）。
    /// </summary>
    public static string? GetInstalledGradleVersion()
    {
        var bin = GetGradleBinPath();
        if (bin == null) return null;
        // ...gradle-8.11.1\bin\gradle.bat → "gradle-8.11.1"
        var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(bin)));
        if (dirName == null || !dirName.StartsWith("gradle-", StringComparison.OrdinalIgnoreCase))
            return null;
        var ver = dirName.Substring("gradle-".Length).Trim();
        return Version.TryParse(ver, out _) ? ver : null;
    }

    /// <summary>
    /// 本地 Gradle 是否已安装且满足 RequiredGradleVersion 最低版本要求。
    /// 版本号无法识别时视为已安装（保持旧行为）。
    /// </summary>
    public static bool IsGradleUpToDate()
    {
        var bin = GetGradleBinPath();
        if (bin == null) return false;
        var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(bin)));
        var ver = ParseGradleVersion(dirName);
        var required = ParseGradleVersion("gradle-" + RequiredGradleVersion);
        if (ver == null || required == null) return true;
        return ver >= required;
    }

    /// <summary>从 "gradle-8.11.1" 目录名解析版本号。</summary>
    private static Version? ParseGradleVersion(string? dirName)
    {
        if (string.IsNullOrEmpty(dirName) || !dirName.StartsWith("gradle-", StringComparison.OrdinalIgnoreCase))
            return null;
        return Version.TryParse(dirName.Substring("gradle-".Length).Trim(), out var v) ? v : null;
    }

    private async Task<bool> DownloadAndInstallGradleAsync(
        IProgress<SdkProgress> progress, IProgress<BuildLog>? log, CancellationToken ct)
    {
        // Gradle 8.11.1（cordova-android 15.x 的 AGP 8.10.1 要求 >= 8.11.1；
        // 本地 Gradle 仅用于执行 wrapper 任务生成构建用 wrapper）
        Directory.CreateDirectory(GradleRoot);
        // 发行包保留在本地（gradle-<版本>-bin.zip），构建时通过 file:/// 协议直接使用，避免重复联网下载
        var zipPath = Path.Combine(GradleRoot, $"gradle-{RequiredGradleVersion}-bin.zip");

        // 本地已有发行包时直接复用（可能上次解压后目录被误删，zip 还在）
        bool downloaded = false;
        if (File.Exists(zipPath) && new FileInfo(zipPath).Length > 1024 * 1024)
        {
            downloaded = true;
            log?.Report(new BuildLog { Stream = "stdout", Line = "检测到本地已有 Gradle 发行包，跳过下载", Stage = "gradle-download" });
        }
        if (!downloaded)
        {
            var urls = new[]
        {
            "https://mirrors.tuna.tsinghua.edu.cn/gradle/8.11.1/gradle-8.11.1-bin.zip",
            "https://mirrors.cloud.tencent.com/gradle/gradle-8.11.1-bin.zip",
            "https://services.gradle.org/distributions/gradle-8.11.1-bin.zip",
        };
            foreach (var url in urls)
            {
                for (int attempt = 1; attempt <= 3 && !downloaded; attempt++)
                {
                    try
                    {
                        log?.Report(new BuildLog { Stream = "stdout", Line = $"下载 Gradle（第 {attempt} 次）：{url}", Stage = "gradle-download" });
                        if (await DownloadZipAsync(url, zipPath, progress, ct))
                        {
                            downloaded = true;
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log?.Report(new BuildLog { Stream = "stderr", Line = $"Gradle 下载失败（第 {attempt} 次）：{ex.Message}", Stage = "gradle-download" });
                        // 删除不完整的文件，下次重试
                        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                    }
                }
                if (downloaded) break;
            }
        }
        if (!downloaded) return false;

        // 解压（保留 zip 供构建时本地引用）
        progress.Report(new SdkProgress { Stage = "extracting", Percent = 0, Message = "解压 Gradle" });
        log?.Report(new BuildLog { Stream = "stdout", Line = "解压 Gradle", Stage = "gradle-extract" });
        try
        {
            ZipFile.ExtractToDirectory(zipPath, GradleRoot, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            // zip 损坏：删除坏文件并报错，避免留下坏状态
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            log?.Report(new BuildLog { Stream = "stderr", Line = $"Gradle 解压失败（zip 可能已损坏）：{ex.Message}", Stage = "gradle-extract" });
            progress.Report(new SdkProgress { Stage = "error", Message = "Gradle 解压失败，请重试（将重新下载）" });
            return false;
        }

        // 清理旧版本目录，只保留刚安装的 gradle-<RequiredGradleVersion>
        try
        {
            foreach (var d in Directory.EnumerateDirectories(GradleRoot, "gradle-*"))
            {
                if (!string.Equals(Path.GetFileName(d), $"gradle-{RequiredGradleVersion}", StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(d, recursive: true);
            }
        }
        catch { }

        // 设置 GRADLE_HOME 环境变量
        var gradleHome = GetGradleBinPath();
        if (gradleHome != null)
        {
            var gradleDir = Path.GetDirectoryName(Path.GetDirectoryName(gradleHome))!;
            try
            {
                EnvironmentHelper.SetUserEnvironment("GRADLE_HOME", gradleDir);
                // 把 gradle/bin 加入用户 PATH
                var currentPath = EnvironmentHelper.GetUserEnvironment("Path") ?? "";
                var gradleBinDir = Path.Combine(gradleDir, "bin");
                if (!currentPath.Contains(gradleBinDir, StringComparison.OrdinalIgnoreCase))
                {
                    var newPath = string.IsNullOrEmpty(currentPath)
                        ? gradleBinDir
                        : currentPath.TrimEnd(';') + ";" + gradleBinDir;
                    EnvironmentHelper.SetUserEnvironment("Path", newPath);
                }
                log?.Report(new BuildLog { Stream = "stdout", Line = $"GRADLE_HOME={gradleDir}", Stage = "gradle-done" });
            }
            catch { }
        }

        return IsGradleInstalled();
    }

    // ---- JDK 下载安装 ----

    public static readonly string JdkRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "jdk");

    public static string? GetJavaHome()
    {
        // 优先用工具安装的 JDK
        var toolHome = GetToolInstalledJavaHome();
        if (toolHome != null) return toolHome;
        // 回退到系统 JAVA_HOME
        var envHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
        if (!string.IsNullOrEmpty(envHome) && File.Exists(Path.Combine(envHome, "bin", "java.exe")))
            return envHome;
        return null;
    }

    /// <summary>
    /// 仅检查工具自身安装的 JDK（不回退到系统 JAVA_HOME）。
    /// 用于 JDK 下载安装流程，避免系统已装的高版本 Java 误判为"已安装"。
    /// </summary>
    public static string? GetToolInstalledJavaHome()
    {
        if (Directory.Exists(JdkRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(JdkRoot, "jdk-*"))
            {
                var java = Path.Combine(d, "bin", "java.exe");
                if (File.Exists(java)) return d;
            }
        }
        return null;
    }

    public static bool IsJdkInstalled() => GetToolInstalledJavaHome() != null;

    public async Task<bool> DownloadAndInstallJdkAsync(
        IProgress<SdkProgress> progress, IProgress<BuildLog>? log, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        if (IsJdkInstalled())
        {
            progress.Report(new SdkProgress { Stage = "done", Percent = 100, Message = "JDK 已安装" });
            return true;
        }

        // Eclipse Temurin JDK 17 LTS（Gradle 8.11.1 完全兼容，避免 Java 25 的 class file version 69 问题）
        // 清华镜像优先 + Adoptium API 回退
        var urls = new[]
        {
            "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/17/jdk/x64/windows/OpenJDK17U-jdk_x64_windows_hotspot_17.0.13_11.zip",
            "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jdk/hotspot/normal/eclipse",
        };
        Directory.CreateDirectory(JdkRoot);
        var zipPath = Path.Combine(JdkRoot, "jdk.zip");

        bool downloaded = false;
        foreach (var url in urls)
        {
            try
            {
                log?.Report(new BuildLog { Stream = "stdout", Line = $"下载 JDK：{url}", Stage = "jdk-download" });
                if (await DownloadZipAsync(url, zipPath, progress, token))
                {
                    downloaded = true;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Report(new BuildLog { Stream = "stderr", Line = $"JDK 下载失败：{ex.Message}", Stage = "jdk-download" });
            }
        }
        if (!downloaded)
        {
            progress.Report(new SdkProgress { Stage = "error", Message = "JDK 下载失败，请检查网络" });
            return false;
        }

        // 解压
        progress.Report(new SdkProgress { Stage = "extracting", Percent = 0, Message = "解压 JDK" });
        log?.Report(new BuildLog { Stream = "stdout", Line = "解压 JDK", Stage = "jdk-extract" });
        try
        {
            ZipFile.ExtractToDirectory(zipPath, JdkRoot, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            log?.Report(new BuildLog { Stream = "stderr", Line = $"JDK 解压失败（zip 可能已损坏）：{ex.Message}", Stage = "jdk-extract" });
            progress.Report(new SdkProgress { Stage = "error", Message = "JDK 解压失败，请重试（将重新下载）" });
            return false;
        }
        try { File.Delete(zipPath); } catch { }

        // 设置 JAVA_HOME 和 PATH（用工具刚解压的 JDK，不回退到系统的高版本 Java）
        var javaHome = GetToolInstalledJavaHome();
        if (javaHome != null)
        {
            try
            {
                EnvironmentHelper.SetUserEnvironment("JAVA_HOME", javaHome);
                var currentPath = EnvironmentHelper.GetUserEnvironment("Path") ?? "";
                var jdkBin = Path.Combine(javaHome, "bin");
                if (!currentPath.Contains(jdkBin, StringComparison.OrdinalIgnoreCase))
                {
                    var newPath = string.IsNullOrEmpty(currentPath)
                        ? jdkBin
                        : currentPath.TrimEnd(';') + ";" + jdkBin;
                    EnvironmentHelper.SetUserEnvironment("Path", newPath);
                }
                log?.Report(new BuildLog { Stream = "stdout", Line = $"JAVA_HOME={javaHome}", Stage = "jdk-done" });
            }
            catch { }
        }

        return IsJdkInstalled();
    }

    // ---- Gradle 单独安装（public） ----
    public async Task<bool> DownloadAndInstallGradlePublicAsync(
        IProgress<SdkProgress> progress, IProgress<BuildLog>? log, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        if (IsGradleUpToDate())
        {
            progress.Report(new SdkProgress { Stage = "done", Percent = 100, Message = "Gradle 已安装" });
            return true;
        }
        // 已安装但版本过低时提示升级
        var oldVer = GetInstalledGradleVersion();
        if (oldVer != null)
            log?.Report(new BuildLog { Stream = "stdout", Line = $"检测到 Gradle {oldVer} 低于要求的 {RequiredGradleVersion}，开始升级", Stage = "gradle-download" });
        return await DownloadAndInstallGradleAsync(progress, log, token);
    }

    private async Task<bool> DownloadZipAsync(string url, string destPath,
        IProgress<SdkProgress> progress, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return false;

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long downloaded = 0;
        long lastTickBytes = 0;
        var lastReport = DateTime.UtcNow;
        var lastSpeedUpdate = DateTime.UtcNow;
        int read;

        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 250)
            {
                double speedKBs = 0;
                if (now > lastSpeedUpdate)
                {
                    var dt = (now - lastSpeedUpdate).TotalSeconds;
                    if (dt > 0)
                        speedKBs = (downloaded - lastTickBytes) / 1024.0 / dt;
                }
                lastSpeedUpdate = now;
                lastTickBytes = downloaded;

                double percent = total > 0 ? downloaded * 100.0 / total : 0;
                progress.Report(new SdkProgress
                {
                    Stage = "downloading",
                    Percent = percent,
                    Downloaded = downloaded,
                    Total = total,
                    SpeedKBs = speedKBs,
                });
                lastReport = now;
            }
        }

        await fs.FlushAsync(ct);

        // 完整性校验：Content-Length 已知时，下载字节数必须一致（防止流被截断产生坏 zip）
        if (total > 0 && downloaded != total)
        {
            progress.Report(new SdkProgress
            {
                Stage = "error",
                Message = $"下载不完整（{downloaded}/{total} 字节），请重试",
            });
            return false;
        }
        return true;
    }

    public void Cancel() => _cts?.Cancel();

    public void Dispose()
    {
        _cts?.Cancel();
        _client.Dispose();
    }

    // ---- Node.js 下载安装 ----

    public static readonly string NodeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "node");

    public static string? GetNodePath()
    {
        // 优先用工具安装的 Node.js
        if (Directory.Exists(NodeRoot))
        {
            if (File.Exists(Path.Combine(NodeRoot, "node.exe")))
                return NodeRoot;
            foreach (var d in Directory.EnumerateDirectories(NodeRoot))
            {
                if (File.Exists(Path.Combine(d, "node.exe")))
                    return d;
            }
        }
        // 回退到系统 PATH 中的 node
        var node = ProcessRunner.FindInPath("node.exe");
        if (!string.IsNullOrEmpty(node))
            return Path.GetDirectoryName(node);
        return null;
    }

    public static bool IsNodeInstalled() => GetNodePath() != null;

    public async Task<bool> DownloadAndInstallNodeAsync(
        IProgress<SdkProgress> progress, IProgress<BuildLog>? log, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        if (IsNodeInstalled())
        {
            progress.Report(new SdkProgress { Stage = "done", Percent = 100, Message = "Node.js 已安装" });
            return true;
        }

        // Node.js 22 LTS - 官方源 + npmmirror 回退
        var urls = new[]
        {
            "https://npmmirror.com/mirrors/node/v22.11.0/node-v22.11.0-win-x64.zip",
            "https://nodejs.org/dist/v22.11.0/node-v22.11.0-win-x64.zip",
        };
        Directory.CreateDirectory(NodeRoot);
        var zipPath = Path.Combine(NodeRoot, "node.zip");

        bool downloaded = false;
        foreach (var url in urls)
        {
            try
            {
                log?.Report(new BuildLog { Stream = "stdout", Line = $"下载 Node.js：{url}", Stage = "node-download" });
                if (await DownloadZipAsync(url, zipPath, progress, token))
                {
                    downloaded = true;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Report(new BuildLog { Stream = "stderr", Line = $"Node.js 下载失败：{ex.Message}", Stage = "node-download" });
            }
        }
        if (!downloaded)
        {
            progress.Report(new SdkProgress { Stage = "error", Message = "Node.js 下载失败，请检查网络" });
            return false;
        }

        // 解压
        progress.Report(new SdkProgress { Stage = "extracting", Percent = 0, Message = "解压 Node.js" });
        log?.Report(new BuildLog { Stream = "stdout", Line = "解压 Node.js", Stage = "node-extract" });
        ZipFile.ExtractToDirectory(zipPath, NodeRoot, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { }

        // 把 node 目录加入 PATH
        var nodePath = GetNodePath();
        if (nodePath != null)
        {
            try
            {
                var currentPath = EnvironmentHelper.GetUserEnvironment("Path") ?? "";
                if (!currentPath.Contains(nodePath, StringComparison.OrdinalIgnoreCase))
                {
                    var newPath = string.IsNullOrEmpty(currentPath)
                        ? nodePath
                        : currentPath.TrimEnd(';') + ";" + nodePath;
                    EnvironmentHelper.SetUserEnvironment("Path", newPath);
                }
                log?.Report(new BuildLog { Stream = "stdout", Line = $"Node.js 路径：{nodePath}", Stage = "node-done" });
            }
            catch { }
        }

        return IsNodeInstalled();
    }
}
