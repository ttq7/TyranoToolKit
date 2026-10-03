using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using TyranoToolKit.Models;

namespace TyranoToolKit.Services;

/// <summary>
/// APK 构建日志条目，供 IProgress&lt;BuildLog&gt; 回调使用。
/// </summary>
public class BuildLog
{
    public string Stream { get; set; } = "";  // stdout/stderr
    public string Line { get; set; } = "";
    public string Stage { get; set; } = "";  // scaffold/copy/platform/build/done/error
}

/// <summary>
/// Cordova APK 构建服务，移植自旧 CordovaBuilder.js。
/// </summary>
public class ApkBuilderService
{
    private static readonly string WorkspaceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "cordova-workspace");
    public static readonly string ExportRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TyranoToolKit", "export");
    // 引擎项目路径：可由用户设置，默认自动检测 Steam 路径
    public string EngineProjectRoot { get; set; } = ProjectScanner.GetDefaultEnginePath();

    private Process? _activeProc;
    private CancellationTokenSource? _internalCts;

    private static readonly string ConfigXmlTemplate = """
<?xml version='1.0' encoding='utf-8'?>
<widget id="{APPID}" version="{APPVERSION}" xmlns="http://www.w3.org/ns/widgets" xmlns:cdv="http://cordova.apache.org/ns/1.0">
    <name>{APPNAME}</name>
    <description> Tyrano game build</description>
    <author email="dev@cordova.apache.org" href="https://cordova.apache.org">
        Apache Cordova Team
    </author>
    <content src="index.html" />
    <allow-intent href="http://*/*" />
    <allow-intent href="https://*/*" />
    <preference name="Orientation" value="landscape" />
    <preference name="DisallowOverscroll" value="true" />
    <preference name="BackgroundColor" value="#000000" />
    <preference name="android-minSdkVersion" value="21" />
    {ICON_BLOCK}
</widget>
""".Replace("'", "\"");

    /// <summary>
    /// 全局安装 cordova 命令行，返回 (是否成功, 版本号, 错误信息)。
    /// 安装后用完整路径调用 cordova，避免 npm 全局 bin 不在 PATH 中导致失败。
    /// </summary>
    public async Task<(bool ok, string version, string error)> InstallCordovaAsync(
        IProgress<BuildLog> progress, CancellationToken ct)
    {
        void OnLine(string stream, string line) =>
            progress.Report(new BuildLog { Stream = stream, Line = line, Stage = "install" });

        // 优先用工具安装的 npm
        var nodePath = AndroidSdkService.GetNodePath();
        var npmCmd = nodePath != null ? Path.Combine(nodePath, "npm.cmd") : "npm";

        var (code, npmErr) = await ProcessRunner.RunAsync(
            npmCmd, "install -g cordova --registry=https://registry.npmmirror.com",
            null, OnLine, ct);
        if (code != 0)
            return (false, "", $"npm 退出码 {code}：{npmErr}");

        // 取 npm 全局 bin 路径，拼接 cordova.cmd 完整路径
        var prefix = "";
        try
        {
            var (pCode, pOut, _) = await ProcessRunner.RunSync(npmCmd, "prefix -g", 8000);
            if (pCode == 0) prefix = pOut.Trim();
        }
        catch { }
        var cordovaBin = Path.Combine(prefix, "cordova.cmd");
        if (string.IsNullOrEmpty(prefix) || !File.Exists(cordovaBin))
            cordovaBin = "cordova";  // 回退到 PATH

        // 用完整路径调用 cordova -v（首次调用可能因 PATH 未刷新而失败，重试 2 次）
        int retries = 2;
        for (int i = 0; i <= retries; i++)
        {
            var (vCode, vOut, vErr) = await ProcessRunner.RunSync(cordovaBin, "-v", 15000);
            if (vCode == 0)
                return (true, vOut.Trim(), "");
            if (i < retries)
            {
                await Task.Delay(1500, ct);  // 等 1.5 秒让 PATH 刷新
                OnLine("stdout", $"cordova -v 第 {i + 1} 次失败，重试中...");
            }
            else
            {
                // npm install 成功但 cordova -v 失败，可能 PATH 未刷新
                // 仍返回成功，提示用户重启工具后重新检测
                return (true, "", "Cordova 已安装，但首次调用失败，请点击\"重新检测\"验证");
            }
        }
        return (true, "", "");
    }

    /// <summary>
    /// 执行 Cordova APK 完整构建流程。返回 (是否成功, 产物路径)。
    /// </summary>
    public async Task<(bool ok, string outPath)> BuildAsync(
        BuildOptions opts, IProgress<BuildLog> progress, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _internalCts = linkedCts;
        var internalToken = linkedCts.Token;
        try
        {
            return await BuildInternal(opts, progress, internalToken);
        }
        finally
        {
            _internalCts = null;
        }
    }

    private async Task<(bool ok, string outPath)> BuildInternal(
        BuildOptions opts, IProgress<BuildLog> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(ExportRoot);

        // 解析 cordova 完整路径，避免 npm 全局 bin 不在 PATH 中
        var cordovaBin = await GetCordovaBinPathAsync();

        // 依赖预检：缺 JDK/Gradle/build-tools 时快速失败，避免构建到一半报晦涩错误
        if (AndroidSdkService.GetJavaHome() == null)
        {
            progress.Report(new BuildLog { Stream = "stderr", Line = "未检测到可用 JDK，请先在依赖检测页点击\"安装 JDK\"", Stage = "error" });
            return (false, "");
        }
        if (AndroidSdkService.GetGradleBinPath() == null)
        {
            progress.Report(new BuildLog { Stream = "stderr", Line = "未检测到 Gradle，请先在依赖检测页点击\"安装 Gradle\"", Stage = "error" });
            return (false, "");
        }
        if (!AndroidSdkService.IsBuildToolsInstalled())
        {
            progress.Report(new BuildLog { Stream = "stderr", Line = $"SDK 缺少 build-tools {AndroidSdkService.RequiredBuildToolsVersion}，请先点击\"安装 SDK\"补装", Stage = "error" });
            return (false, "");
        }

        var projDir = Path.Combine(WorkspaceRoot, opts.ProjectName);
        var configXml = Path.Combine(projDir, "config.xml");

        // 1. scaffold
        if (!File.Exists(configXml))
        {
            progress.Report(new BuildLog { Stream = "stdout", Line = $"cordova create {projDir} {opts.AppId} {opts.AppName}", Stage = "scaffold" });
            var (createCode, createErr) = await ProcessRunner.RunAsync(
                cordovaBin, $"create \"{projDir}\" {opts.AppId} {opts.AppName}",
                null, (s, l) => progress.Report(new BuildLog { Stream = s, Line = l, Stage = "scaffold" }), ct);
            if (createCode != 0)
            {
                progress.Report(new BuildLog { Stream = "stderr", Line = createErr, Stage = "error" });
                return (false, "");
            }
        }
        // 写 config.xml（有图标才写 <icon> 标签，避免 res/icon.png 不存在报错）
        var iconBlock = !string.IsNullOrEmpty(opts.IconPath) && File.Exists(opts.IconPath)
            ? "    <platform name=\"android\">\n        <icon src=\"res/icon.png\" />\n    </platform>"
            : "";
        var configXmlContent = ConfigXmlTemplate
            .Replace("{APPID}", opts.AppId)
            .Replace("{APPVERSION}", opts.AppVersion)
            .Replace("{APPNAME}", opts.AppName)
            .Replace("{ICON_BLOCK}", iconBlock);
        await File.WriteAllTextAsync(configXml, configXmlContent, Encoding.UTF8, ct);

        // 2. 拷贝 www
        progress.Report(new BuildLog { Stream = "stdout", Line = "拷贝游戏文件到 www", Stage = "copy" });
        var srcProject = Path.Combine(EngineProjectRoot, opts.ProjectName);
        var wwwDir = Path.Combine(projDir, "www");
        if (Directory.Exists(wwwDir)) Directory.Delete(wwwDir, recursive: true);
        Directory.CreateDirectory(wwwDir);

        if (Directory.Exists(srcProject))
        {
            foreach (var name in new[] { "index.html", "data", "tyrano", "builder_config.json", "package.json" })
            {
                var src = Path.Combine(srcProject, name);
                if (File.Exists(src))
                {
                    File.Copy(src, Path.Combine(wwwDir, name), overwrite: true);
                }
                else if (Directory.Exists(src))
                {
                    CopyDirectory(src, Path.Combine(wwwDir, name));
                }
            }
        }

        // 3. icon
        if (!string.IsNullOrEmpty(opts.IconPath) && File.Exists(opts.IconPath))
        {
            var resDir = Path.Combine(projDir, "res");
            Directory.CreateDirectory(resDir);
            var iconDest = Path.Combine(resDir, "icon.png");
            File.Copy(opts.IconPath, iconDest, overwrite: true);
            progress.Report(new BuildLog { Stream = "stdout", Line = $"icon -> {iconDest}", Stage = "copy" });
        }

        // 4. platform add android（注入 ANDROID_HOME + Gradle + JDK 环境变量）
        var buildEnv = new Dictionary<string, string?>
        {
            ["ANDROID_HOME"] = AndroidSdkService.SdkRoot,
            ["ANDROID_SDK_ROOT"] = AndroidSdkService.SdkRoot,
        };
        // 强制用工具安装的 JDK 17（避免系统 Java 25 的 class file version 69 问题）
        var toolJavaHome = AndroidSdkService.GetJavaHome();
        if (toolJavaHome != null)
        {
            buildEnv["JAVA_HOME"] = toolJavaHome;
            var currentPath = Environment.GetEnvironmentVariable("Path") ?? "";
            var javaBinDir = Path.Combine(toolJavaHome, "bin");
            // 把 JDK bin 放到 PATH 最前面，优先于系统的 java
            buildEnv["Path"] = javaBinDir + ";" + currentPath;
        }
        // 注入 Gradle 路径
        var gradleBin = AndroidSdkService.GetGradleBinPath();
        if (gradleBin != null)
        {
            var gradleDir = Path.GetDirectoryName(Path.GetDirectoryName(gradleBin))!;
            buildEnv["GRADLE_HOME"] = gradleDir;
            var gradleBinDir = Path.Combine(gradleDir, "bin");
            var currentPath = buildEnv.TryGetValue("Path", out var p) && p != null ? p : (Environment.GetEnvironmentVariable("Path") ?? "");
            if (!currentPath.Contains(gradleBinDir, StringComparison.OrdinalIgnoreCase))
                buildEnv["Path"] = currentPath.TrimEnd(';') + ";" + gradleBinDir;
        }
        // 强制 cordova 使用 Gradle 8.11.1
        // （cordova-android 15.x 的 prepare 每次构建都会重新生成 wrapper，
        //   环境变量是唯一可靠入口；AGP 8.10.1 要求 Gradle >= 8.11.1）
        // 优先指向工具已下载的本地发行包（file:/// 协议），构建时不再联网下载 130MB；
        // 其次复用 Gradle wrapper 缓存目录里的发行包（之前构建下载过的话）；
        // 都没有时回退到腾讯云镜像
        var localGradleZip = Path.Combine(AndroidSdkService.GradleRoot, $"gradle-{AndroidSdkService.RequiredGradleVersion}-bin.zip");
        if (!File.Exists(localGradleZip))
        {
            try
            {
                var distsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gradle", "wrapper", "dists", $"gradle-{AndroidSdkService.RequiredGradleVersion}-bin");
                if (Directory.Exists(distsDir))
                {
                    var cached = Directory.EnumerateFiles(distsDir, $"gradle-{AndroidSdkService.RequiredGradleVersion}-bin.zip", SearchOption.AllDirectories).FirstOrDefault();
                    if (cached != null) localGradleZip = cached;
                }
            }
            catch { }
        }
        buildEnv["CORDOVA_ANDROID_GRADLE_DISTRIBUTION_URL"] = File.Exists(localGradleZip)
            ? new Uri(localGradleZip).AbsoluteUri
            : $"https://mirrors.cloud.tencent.com/gradle/gradle-{AndroidSdkService.RequiredGradleVersion}-bin.zip";
        var platformsAndroid = Path.Combine(projDir, "platforms", "android");
        if (!Directory.Exists(platformsAndroid))
        {
            progress.Report(new BuildLog { Stream = "stdout", Line = "cordova platform add android", Stage = "platform" });
            var (pCode, _, pErr) = await ProcessRunner.RunAsync(
                cordovaBin, "platform add android", projDir, buildEnv,
                (s, l) => progress.Report(new BuildLog { Stream = s, Line = l, Stage = "platform" }), ct);
            if (pCode != 0)
            {
                progress.Report(new BuildLog { Stream = "stderr", Line = pErr, Stage = "error" });
                return (false, "");
            }
        }

        // 5. gradle 镜像注入 + 修改 gradle-wrapper 指向国内镜像
        InjectGradleMirrors(Path.Combine(projDir, "platforms", "android", "build.gradle"));
        InjectGradleMirrors(Path.Combine(projDir, "platforms", "android", "app", "build.gradle"));
        PatchGradleWrapperProperties(Path.Combine(projDir, "platforms", "android", "gradle", "wrapper", "gradle-wrapper.properties"));
        // tools/build.gradle 里 wrapper 任务硬编码了 gradleVersion，需改成 8.11.1
        PatchToolsBuildGradle(Path.Combine(projDir, "platforms", "android", "tools", "build.gradle"));
        // cdv-gradle-config.json 里 GRADLE_VERSION 硬编码了 8.14.2（不存在），改为 8.11.1
        PatchCdvGradleConfig(Path.Combine(projDir, "platforms", "android", "cdv-gradle-config.json"));
        // 在 gradle.properties 中禁用 daemon，避免 transforms 缓存损坏
        DisableGradleDaemon(Path.Combine(projDir, "platforms", "android", "gradle.properties"));

        // 6. build（Gradle 8.11.1 = AGP 8.10.1 要求的最低版本，wrapper 由环境变量指向腾讯镜像）
        var bt = opts.BuildType == "release" ? "release" : "debug";
        progress.Report(new BuildLog { Stream = "stdout", Line = $"cordova build android --{bt} --gradle-version=8.11.1", Stage = "build" });
        var (bCode, _, bErr) = await ProcessRunner.RunAsync(
            cordovaBin, $"build android --{bt} --gradle-version=8.11.1", projDir, buildEnv,
            (s, l) => progress.Report(new BuildLog { Stream = s, Line = l, Stage = "build" }), ct);
        if (bCode != 0)
        {
            progress.Report(new BuildLog { Stream = "stderr", Line = bErr, Stage = "error" });
            return (false, "");
        }

        // 7. 取产物
        var apkSrc = Path.Combine(projDir, "platforms", "android", "app", "build", "outputs", "apk", bt, $"app-{bt}.apk");
        var exportProj = Path.Combine(ExportRoot, opts.ProjectName);
        Directory.CreateDirectory(exportProj);
        var apkDest = Path.Combine(exportProj, $"{opts.ProjectName}.apk");
        if (!File.Exists(apkSrc))
        {
            progress.Report(new BuildLog { Stream = "stderr", Line = $"未找到产物 APK：{apkSrc}", Stage = "error" });
            return (false, "");
        }
        File.Copy(apkSrc, apkDest, overwrite: true);
        progress.Report(new BuildLog { Stream = "stdout", Line = $"产物：{apkDest}", Stage = "done" });

        return (true, apkDest);
    }

    /// <summary>
    /// 解析 cordova 命令的完整路径。优先用 npm prefix -g 拼接 cordova.cmd，
    /// 回退到直接用 "cordova"（依赖 PATH）。
    /// 必须用异步调用，避免在 UI 线程上 .GetAwaiter().GetResult() 死锁。
    /// </summary>
    public static async Task<string> GetCordovaBinPathAsync()
    {
        // 1. 尝试用 npm prefix -g 获取全局路径
        try
        {
            var (pCode, pOut, _) = await ProcessRunner.RunSync("npm", "prefix -g", 8000);
            if (pCode == 0)
            {
                var prefix = pOut.Trim();
                var found = FindCordovaInDir(prefix);
                if (found != null) return found;
            }
        }
        catch
        {
            // 解析失败继续尝试其他路径
        }

        // 2. 回退到常见 npm 全局路径
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonDirs = new[]
        {
            Path.Combine(appData, "npm"),  // %APPDATA%\npm
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"),
            @"C:\Program Files\nodejs",
        };
        foreach (var dir in commonDirs)
        {
            var found = FindCordovaInDir(dir);
            if (found != null) return found;
        }

        // 3. 最终回退到 PATH
        return "cordova";
    }

    private static string? FindCordovaInDir(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        foreach (var name in new[] { "cordova.cmd", "cordova.bat", "cordova" })
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// 取消正在运行的构建进程。
    /// </summary>
    public void CancelBuild()
    {
        try
        {
            _internalCts?.Cancel();
        }
        catch { }
        try
        {
            if (_activeProc != null && !_activeProc.HasExited)
                _activeProc.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void InjectGradleMirrors(string gradleFile)
    {
        if (!File.Exists(gradleFile)) return;
        var content = File.ReadAllText(gradleFile);
        const string marker = "maven.aliyun.com/repository/google";
        if (content.Contains(marker, StringComparison.Ordinal)) return;

        // 在第一个 repositories { 之后插入
        const string injectBlock =
            "        maven { url 'https://maven.aliyun.com/repository/google' }\n" +
            "        maven { url 'https://maven.aliyun.com/repository/public' }\n";

        var pattern = new Regex(@"repositories\s*\{\s*\n", RegexOptions.Multiline);
        var match = pattern.Match(content);
        if (match.Success)
        {
            var insertAt = match.Index + match.Length;
            content = content.Substring(0, insertAt) + injectBlock + content.Substring(insertAt);
        }
        else
        {
            // 没找到就追加在文件末
            content += "\nrepositories {\n" + injectBlock + "}\n";
        }
        File.WriteAllText(gradleFile, content);
    }

    /// <summary>
    /// 修改 gradle-wrapper.properties 的 distributionUrl 指向国内镜像，避免下载失败。
    /// 腾讯云镜像：https://mirrors.cloud.tencent.com/gradle/gradle-X.X.X-bin.zip
    /// </summary>
    private static void PatchGradleWrapperProperties(string propsPath)
    {
        if (!File.Exists(propsPath)) return;
        try
        {
            var content = File.ReadAllText(propsPath);
            // 用正则替换 distributionUrl 的值
            var pattern = new Regex(@"(distributionUrl\s*=\s*https?://)[^\s]+");
            var match = pattern.Match(content);
            if (!match.Success) return;

            // 提取版本号（gradle-X.X.X-bin.zip）
            var verMatch = Regex.Match(match.Value, @"gradle-([\d.]+)-bin\.zip");
            if (!verMatch.Success) return;
            var version = verMatch.Groups[1].Value;

            // 替换为腾讯云镜像
            var newUrl = $"https://mirrors.cloud.tencent.com/gradle/gradle-{version}-bin.zip";
            var newContent = pattern.Replace(content, $"$1{newUrl}");
            if (newContent != content)
                File.WriteAllText(propsPath, newContent);
        }
        catch { }
    }

    /// <summary>
    /// 修改 platforms/android/tools/build.gradle 中 wrapper 任务的 gradleVersion。
    /// cordova-android 15.1.0 在此文件硬编码了 8.14.2（不存在），改为 8.11.1。
    /// 注意：prepare 阶段可能会重新生成该文件，此补丁仅作兜底。
    /// </summary>
    private static void PatchToolsBuildGradle(string buildGradlePath)
    {
        if (!File.Exists(buildGradlePath)) return;
        try
        {
            var content = File.ReadAllText(buildGradlePath);
            // 匹配 gradleVersion = 'X.X.X' 或 gradleVersion = "X.X.X"
            var pattern = new Regex(@"gradleVersion\s*=\s*['""][\d.]+['""]");
            var newContent = pattern.Replace(content, "gradleVersion = '8.11.1'");
            if (newContent != content)
                File.WriteAllText(buildGradlePath, newContent);
        }
        catch { }
    }

    /// <summary>
    /// 修改 platforms/android/cdv-gradle-config.json：
    /// - GRADLE_VERSION 改为 8.11.1（原 8.14.2 不存在）
    /// - AGP_VERSION 保留模板默认值（8.10.1 与 Gradle 8.11.1 兼容）
    /// 注意：cordova build 的 prepare 阶段会重新生成此文件，此补丁仅作兜底。
    /// </summary>
    private static void PatchCdvGradleConfig(string configPath)
    {
        if (!File.Exists(configPath)) return;
        try
        {
            var content = File.ReadAllText(configPath);
            // GRADLE_VERSION → 8.11.1
            content = Regex.Replace(content,
                @"(""GRADLE_VERSION""\s*:\s*"")[^""]+("")", "${1}8.11.1${2}");
            File.WriteAllText(configPath, content);
        }
        catch { }
    }

    /// <summary>
    /// 在 gradle.properties 中添加 org.gradle.daemon=false，避免 Gradle 守护进程导致 transforms 缓存损坏。
    /// </summary>
    private static void DisableGradleDaemon(string propsPath)
    {
        if (!File.Exists(propsPath)) return;
        try
        {
            var content = File.ReadAllText(propsPath);
            if (!content.Contains("org.gradle.daemon"))
            {
                content += "\norg.gradle.daemon=false\n";
                File.WriteAllText(propsPath, content);
            }
            else if (content.Contains("org.gradle.daemon=true"))
            {
                content = content.Replace("org.gradle.daemon=true", "org.gradle.daemon=false");
                File.WriteAllText(propsPath, content);
            }
        }
        catch { }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
        }
    }
}
