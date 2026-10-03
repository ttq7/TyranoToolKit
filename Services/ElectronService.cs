using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TyranoToolKit.Models;

namespace TyranoToolKit.Services;

/// <summary>
/// 下载进度数据，供 IProgress&lt;DownloadProgress&gt; 回调使用。
/// </summary>
public class DownloadProgress
{
    public string Version { get; set; } = "";
    public string Stage { get; set; } = "";  // downloading/verifying/done/error/cancelled
    public double Percent { get; set; }
    public long Downloaded { get; set; }
    public long Total { get; set; }
    public double SpeedKBs { get; set; }
    public string? Error { get; set; }
    public string? ZipPath { get; set; }
}

/// <summary>
/// Electron 版本下载与缓存管理服务，移植自旧 ElectronDownloader.js。
/// </summary>
public class ElectronService : IDisposable
{
    private const string REG_NPM = "https://registry.npmmirror.com/electron";
    // 下载源选项：索引 0=国内镜像（npmmirror），1=官方源（GitHub）
    public static readonly (string name, string url)[] DownloadSourceOptions =
    {
        ("国内镜像（npmmirror，推荐）", "https://npmmirror.com/mirrors/electron/"),
        ("官方源（GitHub）", "https://github.com/electron/electron/releases/download/"),
    };
    // 当前选择的下载源索引（默认 0=国内镜像）
    public static int ActiveSourceIndex { get; set; } = 0;

    // 按 ActiveSourceIndex 返回下载源 URL 顺序：选国内镜像时 GitHub 作为回退，选官方源时只用 GitHub
    private static IEnumerable<string> GetActiveDownloadSources()
    {
        if (ActiveSourceIndex == 0)
        {
            yield return DownloadSourceOptions[0].url;
            yield return DownloadSourceOptions[1].url;
        }
        else
        {
            yield return DownloadSourceOptions[1].url;
        }
    }

    // 缓存根：与 @electron/get 默认路径一致，便于暴龙引擎打包时复用缓存
    // env-paths('electron', { suffix: '' }).cache 在 Windows 上 = %LOCALAPPDATA%\electron\Cache\
    // (env-paths 的 cache 属性在 Windows 上默认带 'Cache' 后缀)
    private static readonly string CacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "electron", "Cache");

    private static readonly Regex PreReleaseRegex =
        new(@"-(alpha|beta|nightly|rc)", RegexOptions.IgnoreCase);
    // 从 zip 文件名反解版本号：electron-v24.8.8-win32-x64.zip -> 24.8.8
    private static readonly Regex ZipVersionRegex =
        new(@"^electron-v(\d+\.\d+\.\d+(?:[-+\.][\w.]+)?)");
    private static readonly Regex ShaLineRegex =
        new(@"^([0-9a-fA-F]{64})\s+\*?(.+)$");

    private readonly HttpClient _client;
    private readonly Dictionary<string, CancellationTokenSource> _downloads = new();

    public ElectronService()
    {
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// 抓取 npm registry 的 electron 包元数据，返回 latest、稳定版列表、全部版本列表。
    /// </summary>
    public async Task<(string latest, List<string> stable, List<string> all)> FetchVersionList()
    {
        using var resp = await _client.GetAsync(REG_NPM, CancellationToken.None);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(CancellationToken.None);
        using var doc = await JsonDocument.ParseAsync(stream);

        var root = doc.RootElement;
        string latest = "";
        if (root.TryGetProperty("dist-tags", out var dt) &&
            dt.TryGetProperty("latest", out var lt) && lt.ValueKind == JsonValueKind.String)
        {
            latest = lt.GetString() ?? "";
        }

        List<string> all = new();
        List<string> stable = new();
        if (root.TryGetProperty("versions", out var vs) && vs.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in vs.EnumerateObject())
            {
                var v = p.Name;
                if (string.IsNullOrEmpty(v)) continue;
                all.Add(v);
                if (!PreReleaseRegex.IsMatch(v))
                    stable.Add(v);
            }
        }

        all.Sort(VersionComparerDescending);
        stable.Sort(VersionComparerDescending);
        return (latest, stable, all);
    }

    /// <summary>
    /// 按 @electron/get 规则计算下载 URL 对应的缓存子目录哈希：
    /// 去掉文件名后的目录 URL 的 SHA256（小写 hex）。
    /// 例：https://github.com/electron/electron/releases/download/v24.8.8/electron-v24.8.8-win32-x64.zip
    ///     -> stripped = https://github.com/electron/electron/releases/download/v24.8.8
    ///     -> sha256   = 42fe9ea2e7543527d2eddd8b707e7771d236d9ef1fd97027e1c0159c454ad12f
    /// </summary>
    private static string GetCacheHash(string downloadUrl)
    {
        var uri = new Uri(downloadUrl);
        var pathname = uri.AbsolutePath;
        var lastSlash = pathname.LastIndexOf('/');
        var dirname = lastSlash > 0 ? pathname.Substring(0, lastSlash) : "/";
        // Authority 包含端口（若有），与 Node url.format 行为一致
        var strippedUrl = $"{uri.Scheme}://{uri.Authority}{dirname}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(strippedUrl));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// 扫描缓存目录，返回目录与已缓存版本（含大小）。
    /// 新缓存结构：CacheRoot/&lt;sha256哈希&gt;/electron-v&lt;version&gt;-...zip + SHASUMS256.txt
    /// 通过 zip 文件名反解版本号。
    /// </summary>
    public (string dir, List<(string version, long size)> versions) GetCacheInfo()
    {
        Directory.CreateDirectory(CacheRoot);
        var list = new List<(string version, long size)>();
        if (!Directory.Exists(CacheRoot)) return (CacheRoot, list);

        // 同一版本可能因不同下载源对应不同哈希目录，按版本聚合大小
        var versionSizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in Directory.EnumerateDirectories(CacheRoot))
        {
            // 子目录名应为 64 位 hex（SHA256）
            var name = Path.GetFileName(d);
            if (name.Length != 64 || !name.All(c => "0123456789abcdef".Contains(char.ToLower(c))))
                continue;

            // 在子目录下找 electron-v*.zip 反解版本号
            string? version = null;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "electron-v*.zip"))
                {
                    var fn = Path.GetFileName(f);
                    var m = ZipVersionRegex.Match(fn);
                    if (m.Success)
                    {
                        version = m.Groups[1].Value;
                        break;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(version)) continue;

            long size = 0;
            try { size = GetDirSize(d); }
            catch { }

            if (versionSizeMap.ContainsKey(version))
                versionSizeMap[version] += size;
            else
                versionSizeMap[version] = size;
        }

        foreach (var kv in versionSizeMap)
            list.Add((kv.Key, kv.Value));

        list.Sort((a, b) => VersionComparerDescending(a.version, b.version));
        return (CacheRoot, list);
    }

    /// <summary>
    /// 下载指定版本 Electron 压缩包，支持断点续传与取消。返回最终 zip 文件路径。
    /// 缓存路径与 @electron/get 一致：CacheRoot/&lt;sha256&gt;/electron-v*.zip，便于暴龙引擎复用。
    /// </summary>
    public async Task<string> DownloadAsync(string version, string platform, string arch,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var fileName = $"electron-v{version}-{platform}-{arch}.zip";
        Directory.CreateDirectory(CacheRoot);

        // 1. 先扫描所有可能的哈希子目录，看 zip 是否已存在（任何源对应目录均可复用）
        if (Directory.Exists(CacheRoot))
        {
            foreach (var d in Directory.EnumerateDirectories(CacheRoot))
            {
                var existingZip = Path.Combine(d, fileName);
                if (File.Exists(existingZip))
                {
                    progress?.Report(new DownloadProgress
                    {
                        Version = version,
                        Stage = "done",
                        Percent = 100,
                        Total = (new FileInfo(existingZip)).Length,
                        Downloaded = (new FileInfo(existingZip)).Length,
                        ZipPath = existingZip,
                    });
                    return existingZip;
                }
            }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _downloads[version] = cts;
        HttpResponseMessage? resp = null;

        try
        {
            // 2. 多源尝试：每个源对应各自的哈希目录，支持续传
            string? usedUrl = null;
            string? hashDir = null;
            string? zipPath = null;
            string? partPath = null;
            long startOffset = 0;

            foreach (var baseUrl in GetActiveDownloadSources())
            {
                var tryUrl = baseUrl + "v" + version + "/" + fileName;
                var hash = GetCacheHash(tryUrl);
                var hd = Path.Combine(CacheRoot, hash);
                Directory.CreateDirectory(hd);
                var zp = Path.Combine(hd, fileName);
                var pp = zp + ".part";

                // 若该源对应目录已有完整 zip，直接复用
                if (File.Exists(zp))
                {
                    progress?.Report(new DownloadProgress
                    {
                        Version = version,
                        Stage = "done",
                        Percent = 100,
                        Total = (new FileInfo(zp)).Length,
                        Downloaded = (new FileInfo(zp)).Length,
                        ZipPath = zp,
                    });
                    return zp;
                }

                long so = 0;
                if (File.Exists(pp)) so = (new FileInfo(pp)).Length;

                var req = new HttpRequestMessage(HttpMethod.Get, tryUrl);
                if (so > 0) req.Headers.Range = new RangeHeaderValue(so, null);

                try
                {
                    resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (resp.StatusCode == HttpStatusCode.OK ||
                        resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        usedUrl = tryUrl;
                        hashDir = hd;
                        zipPath = zp;
                        partPath = pp;
                        startOffset = so;
                        break;
                    }
                    resp.Dispose();
                    resp = null;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 尝试下一个源 */ }
            }

            if (resp == null || zipPath == null || partPath == null || hashDir == null)
                throw new InvalidOperationException("所有下载源均不可用，请检查网络");

            long total = 0;
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 416：服务端认为已经下完
                File.Move(partPath, zipPath, true);
                progress?.Report(new DownloadProgress
                {
                    Version = version,
                    Stage = "done",
                    Percent = 100,
                    ZipPath = zipPath,
                });
                await SaveShasumsAsync(version, hashDir, ct);
                return zipPath;
            }

            if (resp.StatusCode != HttpStatusCode.OK &&
                resp.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new InvalidOperationException(
                    $"下载失败：HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            }

            if (resp.Content.Headers.ContentRange != null &&
                resp.Content.Headers.ContentRange.HasLength &&
                resp.Content.Headers.ContentRange.Length.HasValue)
                total = resp.Content.Headers.ContentRange.Length.Value;
            else if (resp.Content.Headers.ContentLength.HasValue)
                total = resp.Content.Headers.ContentLength.Value + startOffset;

            await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var fs = new FileStream(partPath,
                startOffset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 81920);
            // FileMode.Append 写入会自动追加到文件末尾，无需手动定位

            var buffer = new byte[81920];
            long downloaded = startOffset;
            long lastTickBytes = downloaded;
            var lastReport = DateTime.UtcNow;
            var lastSpeedUpdate = DateTime.UtcNow;

            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), cts.Token);
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
                    progress?.Report(new DownloadProgress
                    {
                        Version = version,
                        Stage = "downloading",
                        Percent = percent,
                        Downloaded = downloaded,
                        Total = total,
                        SpeedKBs = speedKBs,
                    });
                    lastReport = now;
                }
            }

            await fs.FlushAsync(cts.Token);
            fs.Close();

            // 416 容忍；后续做 SHA256 校验
            try
            {
                await VerifySha256Async(version, fileName, partPath, hashDir);
            }
            catch
            {
                // 校验失败由 VerifySha256Async 自行删除 .part 并抛出
                throw;
            }

            File.Move(partPath, zipPath, true);
            // 保存 SHASUMS256.txt 到哈希目录，让 @electron/get 下次能直接复用缓存
            await SaveShasumsAsync(version, hashDir, ct);
            progress?.Report(new DownloadProgress
            {
                Version = version,
                Stage = "done",
                Percent = 100,
                Downloaded = total > 0 ? total : downloaded,
                Total = total,
                ZipPath = zipPath,
            });
            return zipPath;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new DownloadProgress
            {
                Version = version,
                Stage = "cancelled",
            });
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report(new DownloadProgress
            {
                Version = version,
                Stage = "error",
                Error = ex.Message,
            });
            throw;
        }
        finally
        {
            resp?.Dispose();
            _downloads.Remove(version);
            cts.Dispose();
        }
    }

    /// <summary>
    /// 下载并保存 SHASUMS256.txt 到指定哈希目录，便于 @electron/get 下次复用缓存。
    /// 失败时容忍（不影响主下载流程）。
    /// </summary>
    private async Task SaveShasumsAsync(string version, string hashDir, CancellationToken ct)
    {
        var shasumsPath = Path.Combine(hashDir, "SHASUMS256.txt");
        if (File.Exists(shasumsPath)) return;  // 已存在不重复下载

        foreach (var baseUrl in GetActiveDownloadSources())
        {
            try
            {
                using var sresp = await _client.GetAsync(
                    baseUrl + "v" + version + "/SHASUMS256.txt", ct);
                if (sresp.IsSuccessStatusCode)
                {
                    var text = await sresp.Content.ReadAsByteArrayAsync(ct);
                    await File.WriteAllBytesAsync(shasumsPath, text, ct);
                    return;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 尝试下一个源 */ }
        }
    }

    /// <summary>
    /// 校验已下载压缩包的 SHA256，与官方 SHASUMS256.txt 对比。取不到校验和或对应行时容忍。
    /// 优先使用本地 hashDir 内的 SHASUMS256.txt，避免重复网络请求。
    /// </summary>
    public async Task VerifySha256Async(string version, string fileName, string zipPath, string? hashDir = null)
    {
        string expected = "";
        string? text = null;

        // 1. 优先用本地 hashDir 内的 SHASUMS256.txt
        if (hashDir != null)
        {
            var localPath = Path.Combine(hashDir, "SHASUMS256.txt");
            if (File.Exists(localPath))
            {
                try { text = await File.ReadAllTextAsync(localPath); }
                catch { }
            }
        }

        // 2. 本地没有则多源拉取（同时保存到 hashDir 备用）
        if (string.IsNullOrEmpty(text))
        {
            foreach (var baseUrl in GetActiveDownloadSources())
            {
                try
                {
                    using var sresp = await _client.GetAsync(
                        baseUrl + "v" + version + "/SHASUMS256.txt", CancellationToken.None);
                    if (sresp.IsSuccessStatusCode)
                    {
                        text = await sresp.Content.ReadAsStringAsync(CancellationToken.None);
                        // 顺便保存到 hashDir
                        if (hashDir != null)
                        {
                            try { await File.WriteAllTextAsync(Path.Combine(hashDir, "SHASUMS256.txt"), text, CancellationToken.None); }
                            catch { }
                        }
                        break;
                    }
                }
                catch { /* 尝试下一个源 */ }
            }
        }

        if (string.IsNullOrEmpty(text)) return;

        foreach (var line in text.Split('\n'))
        {
            var m = ShaLineRegex.Match(line.Trim());
            if (!m.Success) continue;
            if ((m.Groups[2].Value.Trim() ?? "").Equals(fileName, StringComparison.Ordinal))
            {
                expected = m.Groups[1].Value;
                break;
            }
        }

        if (string.IsNullOrEmpty(expected)) return;

        string actual;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        await using (var fs = File.OpenRead(zipPath))
        {
            var hash = await sha.ComputeHashAsync(fs);
            actual = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        if (!string.Equals(actual, expected.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(zipPath); } catch { }
            throw new InvalidOperationException(
                $"SHA256 校验失败：期望 {expected}，实际 {actual}");
        }
    }

    /// <summary>
    /// 取消指定版本的下载任务。
    /// </summary>
    public void CancelDownload(string version)
    {
        if (_downloads.TryGetValue(version, out var cts))
        {
            cts.Cancel();
        }
    }

    /// <summary>
    /// 删除缓存中指定版本（所有下载源对应哈希目录）。返回 (是否成功, 释放字节数)。
    /// </summary>
    public Task<(bool ok, long freedBytes)> RemoveVersionAsync(string version)
    {
        if (!Directory.Exists(CacheRoot))
            return Task.FromResult((false, 0L));

        long totalFreed = 0;
        bool anyDeleted = false;
        var fileNamePrefix = $"electron-v{version}-";

        foreach (var d in Directory.EnumerateDirectories(CacheRoot))
        {
            // 检查该哈希目录是否包含该版本的 zip 文件
            try
            {
                bool contains = false;
                foreach (var f in Directory.EnumerateFiles(d, "electron-v*.zip"))
                {
                    if (Path.GetFileName(f).StartsWith(fileNamePrefix, StringComparison.Ordinal))
                    {
                        contains = true;
                        break;
                    }
                }
                if (!contains) continue;
            }
            catch { continue; }

            long size = 0;
            try { size = GetDirSize(d); }
            catch { }

            try
            {
                Directory.Delete(d, recursive: true);
                totalFreed += size;
                anyDeleted = true;
            }
            catch { }
        }

        return Task.FromResult((anyDeleted, totalFreed));
    }

    // 暴龙引擎 electron 包路径（位于引擎根目录的 resources/app/node_modules/electron/package.json）
    private static readonly Regex VersionFieldRegex =
        new(@"""version""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// 自动搜索暴龙引擎 外层 node_modules/electron/package.json 路径。
    /// 注意：electron-packager 用 resolve('electron', { basedir: 游戏项目package.json所在目录 })
    /// 向上查找时会找到引擎根目录的 外层 node_modules/electron（不是 resources/app/node_modules）。
    /// 搜索顺序：ProjectScanner 推断的引擎路径 → exe 附近 → Steam 路径。
    /// </summary>
    public static string? FindEngineElectronPackagePath()
    {
        var candidates = new List<string>();

        // 1. ProjectScanner 推断的引擎路径（返回的是 myproject 子目录）
        var myproject = ProjectScanner.GetDefaultEnginePath();
        if (!string.IsNullOrEmpty(myproject) && Directory.Exists(myproject))
        {
            var engineRoot = Path.GetDirectoryName(myproject);
            if (!string.IsNullOrEmpty(engineRoot))
                candidates.Add(Path.Combine(engineRoot, "node_modules", "electron", "package.json"));
        }

        // 2. exe 附近向上查找（便携版场景）
        try
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                candidates.Add(Path.Combine(dir.FullName, "node_modules", "electron", "package.json"));
                candidates.Add(Path.Combine(dir.FullName, "TyranoBuilder", "node_modules", "electron", "package.json"));
                dir = dir.Parent;
            }
        }
        catch { }

        // 3. Steam 路径（外层 node_modules/electron/package.json）
        var steamPath = ProjectScanner.GetSteamPath();
        if (!string.IsNullOrEmpty(steamPath))
        {
            // 默认库
            candidates.Add(Path.Combine(steamPath, "steamapps", "common", "TyranoBuilder", "node_modules", "electron", "package.json"));
            // 自定义库
            var libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libFile))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(libFile))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var idx = trimmed.IndexOf('"', 5);
                        if (idx < 0 || idx + 1 >= trimmed.Length) continue;
                        var start = idx + 1;
                        var end = trimmed.IndexOf('"', start);
                        if (end < 0) continue;
                        var lib = trimmed.Substring(start, end - start).Replace(@"\\", @"\");
                        if (Directory.Exists(lib))
                            candidates.Add(Path.Combine(lib, "steamapps", "common", "TyranoBuilder", "node_modules", "electron", "package.json"));
                    }
                }
                catch { }
            }
        }

        // 去重 + 返回第一个存在的
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (seen.Add(c) && File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>
    /// 读取暴龙引擎当前使用的 Electron 版本号（从 node_modules/electron/package.json 推断）。
    /// 返回 null 表示找不到暴龙引擎。
    /// </summary>
    public static string? GetEngineElectronVersion()
    {
        var pkgPath = FindEngineElectronPackagePath();
        if (pkgPath == null) return null;
        try
        {
            var text = File.ReadAllText(pkgPath);
            var m = VersionFieldRegex.Match(text);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 修改暴龙引擎 node_modules/electron/package.json 的 version 字段，
    /// 让 electron-packager 推断时使用指定版本。
    /// 返回 (是否成功, 错误信息)。
    /// </summary>
    public static (bool ok, string error) SetEngineElectronVersion(string version)
    {
        var pkgPath = FindEngineElectronPackagePath();
        if (pkgPath == null)
            return (false, "未找到暴龙引擎的 electron/package.json，请确认暴龙引擎已安装");

        return SetEngineElectronVersionAtPath(version, pkgPath);
    }

    /// <summary>
    /// 修改指定路径下的 node_modules/electron/package.json 的 version 字段。
    /// pkgPath 必须是完整的 package.json 文件路径。
    /// </summary>
    public static (bool ok, string error) SetEngineElectronVersionAtPath(string version, string pkgPath)
    {
        if (!File.Exists(pkgPath))
            return (false, $"找不到文件：{pkgPath}");

        try
        {
            var text = File.ReadAllText(pkgPath);
            if (!VersionFieldRegex.IsMatch(text))
                return (false, "package.json 中未找到 version 字段");

            // 用正则替换 version 字段，保留其他内容格式
            var newText = VersionFieldRegex.Replace(text, $"\"version\": \"{version}\"", 1);
            if (newText == text)
                return (true, "");  // 已是目标版本，无需修改

            File.WriteAllText(pkgPath, newText);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 返回 Steam 中暴龙引擎根目录（TyranoBuilder 文件夹）。
    /// 用于 OpenFolderDialog 的默认路径。返回空字符串表示未找到。
    /// </summary>
    public static string GetDefaultSteamEngineRoot()
    {
        // 1. 优先用 ProjectScanner 推断的 myproject 父目录
        var myproject = ProjectScanner.GetDefaultEnginePath();
        if (!string.IsNullOrEmpty(myproject) && Directory.Exists(myproject))
        {
            var root = Path.GetDirectoryName(myproject);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                return root;
        }
        return "";
    }

    /// <summary>
    /// 从引擎根目录（TyranoBuilder 文件夹）拼接 electron package.json 路径。
    /// </summary>
    public static string BuildElectronPackagePath(string engineRoot)
        => Path.Combine(engineRoot, "node_modules", "electron", "package.json");

    /// <summary>
    /// 修改暴龙引擎 @electron/get 的 artifact-utils.js 中 BASE_URL 常量，
    /// 强制暴龙引擎打包时用与工具相同的下载源（不依赖 ELECTRON_MIRROR 环境变量）。
    /// 这样 Steam 进程读不到环境变量也能命中缓存。
    /// sourceIndex: 0=国内镜像（npmmirror），1=官方源（GitHub）
    /// 返回 (是否成功修改, 修改的文件数, 错误信息)。
    /// </summary>
    public static (bool ok, int patchedCount, string error) PatchEngineDownloadSource(int sourceIndex)
    {
        var targetUrl = DownloadSourceOptions[sourceIndex].url;
        // BASE_URL 在 artifact-utils.js 第 4 行：
        //   const BASE_URL = 'https://github.com/electron/electron/releases/download/';
        // 把它替换成目标 URL（保留单引号和分号）。
        const string baseUrlPattern = @"const BASE_URL = '[^']+';";
        var baseUrlReplacement = $"const BASE_URL = '{targetUrl}';";

        // 候选路径：所有可能找到 @electron/get 的位置（内层 resources/app + 外层）
        var candidates = new List<string>();
        var myproject = ProjectScanner.GetDefaultEnginePath();
        if (!string.IsNullOrEmpty(myproject) && Directory.Exists(myproject))
        {
            var engineRoot = Path.GetDirectoryName(myproject);
            if (!string.IsNullOrEmpty(engineRoot))
            {
                candidates.Add(Path.Combine(engineRoot, "resources", "app", "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
                candidates.Add(Path.Combine(engineRoot, "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
            }
        }
        // Steam 路径
        var steamPath = ProjectScanner.GetSteamPath();
        if (!string.IsNullOrEmpty(steamPath))
        {
            candidates.Add(Path.Combine(steamPath, "steamapps", "common", "TyranoBuilder", "resources", "app", "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
            candidates.Add(Path.Combine(steamPath, "steamapps", "common", "TyranoBuilder", "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
            // Steam 自定义库
            var libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libFile))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(libFile))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;
                        var idx = trimmed.IndexOf('"', 5);
                        if (idx < 0 || idx + 1 >= trimmed.Length) continue;
                        var start = idx + 1;
                        var end = trimmed.IndexOf('"', start);
                        if (end < 0) continue;
                        var lib = trimmed.Substring(start, end - start).Replace(@"\\", @"\");
                        if (Directory.Exists(lib))
                        {
                            candidates.Add(Path.Combine(lib, "steamapps", "common", "TyranoBuilder", "resources", "app", "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
                            candidates.Add(Path.Combine(lib, "steamapps", "common", "TyranoBuilder", "node_modules", "@electron", "get", "dist", "cjs", "artifact-utils.js"));
                        }
                    }
                }
                catch { }
            }
        }

        // 去重
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctCandidates = candidates.Where(c => seen.Add(c)).ToList();

        int patched = 0;
        foreach (var p in distinctCandidates)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var text = File.ReadAllText(p);
                var newText = System.Text.RegularExpressions.Regex.Replace(text, baseUrlPattern, baseUrlReplacement);
                if (newText != text)
                {
                    File.WriteAllText(p, newText);
                    patched++;
                }
            }
            catch { }
        }
        return (patched > 0, patched, patched == 0 ? "未找到任何 @electron/get artifact-utils.js 文件" : "");
    }

    private static long GetDirSize(string dir)
    {
        long size = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { size += new FileInfo(f).Length; } catch { }
        }
        return size;
    }

    private static int VersionComparerDescending(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var v1) ? v1 : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var v2) ? v2 : 0;
            if (va != vb) return vb.CompareTo(va);
        }
        return 0;
    }

    public void Dispose()
    {
        _client.Dispose();
        foreach (var kv in _downloads)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        _downloads.Clear();
    }
}
