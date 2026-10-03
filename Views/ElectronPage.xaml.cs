using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TyranoToolKit.Models;
using TyranoToolKit.Services;

namespace TyranoToolKit.Views;

public class ElectronVersionRow : INotifyPropertyChanged
{
    public string Version { get; set; } = "";
    public bool IsLatest { get; set; }
    public bool IsStable { get; set; }
    public bool IsCached { get; set; }
    public long SizeBytes { get; set; }
    public string Stage { get; set; } = "";
    public double Progress { get; set; }
    public string SpeedText { get; set; } = "";

    public string TypeText => Version == "24.8.8" ? "推荐使用" : (IsLatest ? "latest" : (IsStable ? "稳定" : "全部"));
    public string CacheText => IsCached ? "已缓存" : "—";
    public string SizeText => IsCached ? FormatSize(SizeBytes) : "—";
    public string StageText
    {
        get
        {
            if (Stage == "downloading") return $"下载中 {Progress:0.0}% {SpeedText}";
            if (Stage == "verifying") return "校验中";
            if (Stage == "done") return "已完成";
            if (Stage == "error") return "错误";
            if (Stage == "cancelled") return "已取消";
            return IsCached ? "就绪" : "";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TypeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CacheText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StageText)));
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
    }
}

public partial class ElectronPage : Page
{
    private readonly ElectronService _svc = new();
    private List<ElectronVersionRow> _rows = new();
    private string _latestVersion = "";
    private string _platform = "win32";
    private string _arch = "x64";

    public ElectronPage()
    {
        InitializeComponent();
        LoadCacheInfo();
        LoadDownloadSourceCombo();
        LoadEngineVersion();
        Loaded += (_, _) => _ = RefreshAsync();
    }

    private void LoadCacheInfo()
    {
        var (dir, _) = _svc.GetCacheInfo();
        CacheDirText.Text = dir;
    }

    private void LoadDownloadSourceCombo()
    {
        DownloadSourceCombo.Items.Clear();
        foreach (var (name, _) in ElectronService.DownloadSourceOptions)
            DownloadSourceCombo.Items.Add(name);
        DownloadSourceCombo.SelectedIndex = ElectronService.ActiveSourceIndex;
        // 启动时同步一次环境变量，确保与默认选择一致
        ApplyDownloadSource(ElectronService.ActiveSourceIndex);
    }

    private void DownloadSourceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 初始化期间也会触发，用 SelectedIndex 守卫
        if (DownloadSourceCombo.SelectedIndex < 0) return;
        ElectronService.ActiveSourceIndex = DownloadSourceCombo.SelectedIndex;
        ApplyDownloadSource(DownloadSourceCombo.SelectedIndex);
    }

    /// <summary>
    /// 根据下载源选择，强制暴龙引擎 @electron/get 用同样的源：
    /// 1. 修改暴龙引擎 artifact-utils.js 的 BASE_URL 常量（不依赖环境变量，Steam 进程也能生效）
    /// 2. 同时设置/清除 ELECTRON_MIRROR 环境变量（双保险）
    /// 选国内镜像 → BASE_URL 改 npmmirror + 设 ELECTRON_MIRROR
    /// 选官方源 → BASE_URL 改回 GitHub + 清除 ELECTRON_MIRROR
    /// </summary>
    private static void ApplyDownloadSource(int sourceIndex)
    {
        // 1. 直接修改暴龙引擎 @electron/get 的 BASE_URL（强制生效，不依赖环境变量）
        var (ok, patched, err) = ElectronService.PatchEngineDownloadSource(sourceIndex);

        // 2. 同时设置/清除环境变量（双保险，对能读到环境变量的进程生效）
        if (sourceIndex == 0)
            EnvironmentHelper.SetUserEnvironment("ELECTRON_MIRROR",
                ElectronService.DownloadSourceOptions[0].url);
        else
            EnvironmentHelper.DeleteUserEnvironment("ELECTRON_MIRROR");
    }

    private void LoadEngineVersion()
    {
        var pkgPath = ElectronService.FindEngineElectronPackagePath();
        if (pkgPath == null)
        {
            EngineVerText.Text = "未检测到暴龙引擎";
            return;
        }
        var ver = ElectronService.GetEngineElectronVersion();
        EngineVerText.Text = string.IsNullOrEmpty(ver)
            ? "已找到 package.json，但未读取到 version 字段"
            : $"{ver}  ({Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(pkgPath)))})";
    }

    private async Task RefreshAsync()
    {
        RefreshBtn.IsEnabled = false;
        RefreshBtn.Content = "获取中...";
        try
        {
            var (latest, stable, all) = await _svc.FetchVersionList();
            _latestVersion = latest;
            var stableSet = stable.ToHashSet();
            var (dir, cached) = _svc.GetCacheInfo();
            var cacheMap = cached.ToDictionary(c => c.version, c => c.size);

            // 只显示稳定版（过滤 alpha/beta/nightly/rc 等预发布版本）
            _rows = stable.Select(v => new ElectronVersionRow
            {
                Version = v,
                IsLatest = v == latest,
                IsStable = stableSet.Contains(v),
                IsCached = cacheMap.TryGetValue(v, out var sz),
                SizeBytes = sz,
            }).ToList();

            // 排序：24.8.8 永远第一（暴龙引擎默认锁定版本，无需配置即可直接复用），其余按版本号降序
            _rows = _rows
                .OrderByDescending(r => r.Version == "24.8.8" ? 1 : 0)
                .ThenByDescending(r => r.Version, new ElectronVersionComparer())
                .ToList();

            VersionGrid.ItemsSource = _rows;
            LoadEngineVersion();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"获取版本列表失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
            RefreshBtn.Content = "刷新版本列表";
        }
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void VersionGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中状态由 DataGrid 自身维护
    }

    private ElectronVersionRow? SelectedRow => VersionGrid.SelectedItem as ElectronVersionRow;

    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        DownloadBtn.IsEnabled = false;
        DownloadProgress.Value = 0;
        ProgressText.Text = $"准备下载 {row.Version} ...";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    DownloadProgress.Value = p.Percent;
                    ProgressText.Text = $"[{p.Stage}] {p.Percent:0.0}%  速度 {p.SpeedKBs:0.0} KB/s  ({DownloadProgressFormat(p)})";
                    row.Stage = p.Stage;
                    row.Progress = p.Percent;
                    row.SpeedText = $"{p.SpeedKBs:0.0} KB/s";
                    row.Refresh();
                });
            });
            await _svc.DownloadAsync(row.Version, _platform, _arch, progress, CancellationToken.None);
            row.IsCached = true;
            row.Stage = "done";
            row.Refresh();
            DownloadProgress.Value = 100;
            ProgressText.Text = $"{row.Version} 下载完成";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "下载已取消";
            row.Stage = "cancelled";
            row.Refresh();
        }
        catch (Exception ex)
        {
            ProgressText.Text = $"下载失败：{ex.Message}";
            row.Stage = "error";
            row.Refresh();
        }
        finally
        {
            DownloadBtn.IsEnabled = true;
        }
    }

    private static string DownloadProgressFormat(DownloadProgress p)
    {
        string Fmt(long b) => ElectronVersionRow.FormatSize(b);
        return p.Total > 0 ? $"{Fmt(p.Downloaded)} / {Fmt(p.Total)}" : Fmt(p.Downloaded);
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { } row)
        {
            _svc.CancelDownload(row.Version);
            ProgressText.Text = "已发出取消请求";
        }
    }

    private async void RemoveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        var ok = MessageBox.Show($"确认删除 {row.Version} 的缓存？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        var (success, freed) = await _svc.RemoveVersionAsync(row.Version);
        if (success)
        {
            row.IsCached = false;
            row.SizeBytes = 0;
            row.Refresh();
            ProgressText.Text = $"已释放 {ElectronVersionRow.FormatSize(freed)}";
        }
        else
        {
            MessageBox.Show("删除失败", "提示");
        }
    }

    private void SetEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            MessageBox.Show("请先选择一个版本", "提示");
            return;
        }
        if (!row.IsCached)
        {
            MessageBox.Show("该版本尚未下载，请先下载再设为暴龙引擎使用版本", "提示");
            return;
        }

        // 弹文件夹对话框，默认路径预填 Steam 中的暴龙引擎根目录
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择暴龙引擎根目录（包含 resources/app 子目录的 TyranoBuilder 文件夹）",
        };
        var defaultRoot = ElectronService.GetDefaultSteamEngineRoot();
        if (!string.IsNullOrEmpty(defaultRoot) && Directory.Exists(defaultRoot))
            dialog.InitialDirectory = defaultRoot;

        if (dialog.ShowDialog() != true) return;

        var engineRoot = dialog.FolderName;
        var pkgPath = ElectronService.BuildElectronPackagePath(engineRoot);
        if (!File.Exists(pkgPath))
        {
            MessageBox.Show(
                $"所选目录下找不到 node_modules/electron/package.json\n\n选中的目录：{engineRoot}\n\n请确认选中的是 TyranoBuilder 根目录（包含 resources、myproject 等子目录）。",
                "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (ok, err) = ElectronService.SetEngineElectronVersionAtPath(row.Version, pkgPath);
        if (ok)
        {
            LoadEngineVersion();
            var msg = string.IsNullOrEmpty(err)
                ? $"已将暴龙引擎的 node_modules/electron/package.json 的 version 改为 {row.Version}。\n\n下次暴龙引擎打包时会使用该版本，会自动命中工具缓存的 zip。"
                : err;
            MessageBox.Show(msg, "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"设置失败：{err}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

}

/// <summary>
/// Electron 版本号比较器（自然升序：小→大）。
/// 配合 OrderByDescending 使用，实现新版本在前。
/// 支持预发布版本（如 45.0.0-alpha.14）。
/// </summary>
public class ElectronVersionComparer : IComparer<string>
{
    public int Compare(string? a, string? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;   // null 排前面
        if (b == null) return 1;

        var pa = a.Split('.');
        var pb = b.Split('.');
        int len = Math.Max(pa.Length, pb.Length);
        for (int i = 0; i < len; i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            if (int.TryParse(sa, out var va) && int.TryParse(sb, out var vb))
            {
                if (va != vb) return va.CompareTo(vb);  // 自然升序
            }
            else
            {
                // 含字母（如 alpha）按字符串比较，自然升序
                var cmp = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return 0;
    }
}
