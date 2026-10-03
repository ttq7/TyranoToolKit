using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TyranoToolKit.Controls;
using TyranoToolKit.Models;
using TyranoToolKit.Services;

namespace TyranoToolKit.Views;

public class GameProjectRow : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasIndex { get; set; }
    public string Source { get; set; } = "";
    public string HasIndexText => HasIndex ? "有" : "缺";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class DependencyRow : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Error { get; set; } = "";
    public string Description { get; set; } = "";
    public string OkText => Ok ? "✓" : "✗";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ApkBuilderPage : Page
{
    private readonly ApkBuilderService _builder = new();
    private GameProjectRow? _selectedProject;
    private List<DependencyStatus> _deps = new();
    private int _step = 0;
    private CancellationTokenSource? _buildCts;

    public ApkBuilderPage()
    {
        InitializeComponent();
        Steps.Steps = new List<StepBar.StepInfo>
        {
            new() { Title = "选择项目", Description = "扫描 TyranoBuilder 项目" },
            new() { Title = "依赖检测", Description = "java/cordova/adb" },
            new() { Title = "构建配置", Description = "应用信息与图标" },
            new() { Title = "构建", Description = "Cordova 打包日志" },
        };
        Steps.CurrentStep = 0;
        Loaded += (_, _) =>
        {
            UpdateEnginePathText();
            _ = ScanAsync();
        };
    }

    private void UpdateEnginePathText()
    {
        var path = _builder.EngineProjectRoot;
        EnginePathText.Text = string.IsNullOrEmpty(path)
            ? "未检测到 Steam 路径，请点击「浏览...」手动选择 TyranoBuilder 引擎的 myproject 目录"
            : path;
    }

    private void BrowseEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 TyranoBuilder 引擎的 myproject 目录",
        };
        if (!string.IsNullOrEmpty(_builder.EngineProjectRoot))
        {
            try
            {
                var full = Path.GetFullPath(_builder.EngineProjectRoot);
                if (Directory.Exists(full))
                    dialog.InitialDirectory = full;
            }
            catch { }
        }
        if (dialog.ShowDialog() == true)
        {
            _builder.EngineProjectRoot = dialog.FolderName;
            UpdateEnginePathText();
            _ = ScanAsync();
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        Step1Panel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (step == 3)
            ExportDirText.Text = $"APK 输出目录：{ApkBuilderService.ExportRoot}";
        Steps.CurrentStep = step;
        PrevBtn.IsEnabled = step > 0;
        NextBtn.Content = step == 3 ? "完成" : "下一步";
    }

    private async Task ScanAsync()
    {
        ScanBtn.IsEnabled = false;
        ScanBtn.Content = "扫描中...";
        try
        {
            var list = ProjectScanner.ScanProjects(_builder.EngineProjectRoot);
            var rows = list.Select(p => new GameProjectRow
            {
                Name = p.Name,
                Path = p.Path,
                HasIndex = p.HasIndex,
                Source = p.Source,
            }).ToList();
            ProjectGrid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"扫描失败：{ex.Message}", "错误");
        }
        finally
        {
            ScanBtn.IsEnabled = true;
            ScanBtn.Content = "扫描项目";
        }
    }

    private void ScanBtn_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private void ProjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is GameProjectRow row)
        {
            _selectedProject = row;
            ProjNameText.Text = row.Name;
            AppNameBox.Text = row.Name;
            AppIdBox.Text = "com.example." + row.Name.ToLowerInvariant().Replace(" ", "");
        }
    }

    private async Task CheckDepsAsync()
    {
        RecheckBtn.IsEnabled = false;
        InstallCordovaBtn.IsEnabled = false;
        RecheckBtn.Content = "检测中...";
        DepProgress.Value = 0;
        DepProgressText.Text = "正在检测...";
        var rows = new List<DependencyRow>();
        DepGrid.ItemsSource = rows;
        try
        {
            var progress = new Progress<(int done, int total, DependencyStatus dep)>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    rows.Add(new DependencyRow
                    {
                        Name = p.dep.Name,
                        Ok = p.dep.Ok,
                        Version = p.dep.Version,
                        Error = p.dep.Error,
                        Description = p.dep.Description,
                    });
                    DepGrid.ItemsSource = null;
                    DepGrid.ItemsSource = rows;
                    DepProgress.Value = p.done * 100.0 / p.total;
                    DepProgressText.Text = $"检测中 {p.done}/{p.total}：{p.dep.Name} {(p.dep.Ok ? "✓" : "✗")}";
                });
            });
            _deps = await DependencyChecker.CheckAll(progress);
            DepProgress.Value = 100;
            DepProgressText.Text = $"检测完成（{_deps.Count(d => d.Ok)}/{_deps.Count} 项可用）";
        }
        finally
        {
            RecheckBtn.IsEnabled = true;
            InstallCordovaBtn.IsEnabled = true;
            RecheckBtn.Content = "重新检测";
        }
    }

    private void RecheckBtn_Click(object sender, RoutedEventArgs e) => _ = CheckDepsAsync();

    private async void InstallCordovaBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallCordovaBtn.IsEnabled = false;
        InstallCordovaBtn.Content = "安装中...";
        try
        {
            var progress = new Progress<BuildLog>(log =>
                Dispatcher.Invoke(() =>
                {
                    DepLog?.Append(log.Stream, log.Line);
                    BuildLog?.Append(log.Stream, log.Line);
                }));
            var (ok, version, error) = await _builder.InstallCordovaAsync(progress, CancellationToken.None);
            if (ok)
            {
                MessageBox.Show($"Cordova 安装成功：{version}", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await CheckDepsAsync();
            }
            else
            {
                var msg = string.IsNullOrEmpty(error)
                    ? "Cordova 安装失败，请检查 npm 与网络。"
                    : $"Cordova 安装失败：\n\n{error}";
                MessageBox.Show(msg, "Cordova 安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallCordovaBtn.IsEnabled = true;
            InstallCordovaBtn.Content = "安装 Cordova";
        }
    }

    private AndroidSdkService? _sdkService;

    private async void InstallJdkBtn_Click(object sender, RoutedEventArgs e)
    {
        await InstallToolAsync(InstallJdkBtn, "安装 JDK", "JDK",
            (sdk, p, l, t) => sdk.DownloadAndInstallJdkAsync(p, l, t));
    }

    private async void InstallNodeBtn_Click(object sender, RoutedEventArgs e)
    {
        await InstallToolAsync(InstallNodeBtn, "安装 Node.js", "Node.js",
            (sdk, p, l, t) => sdk.DownloadAndInstallNodeAsync(p, l, t));
    }

    private async void InstallGradleBtn_Click(object sender, RoutedEventArgs e)
    {
        await InstallToolAsync(InstallGradleBtn, "安装 Gradle", "Gradle",
            (sdk, p, l, t) => sdk.DownloadAndInstallGradlePublicAsync(p, l, t));
    }

    private async Task InstallToolAsync(Button btn, string btnText, string toolName,
        Func<AndroidSdkService, IProgress<SdkProgress>, IProgress<BuildLog>?, CancellationToken, Task<bool>> action)
    {
        btn.IsEnabled = false;
        btn.Content = "安装中...";
        DisableAllInstallButtons();
        try
        {
            _sdkService = new AndroidSdkService();
            var sdkProgress = new Progress<SdkProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    DepProgress.Value = p.Percent;
                    var speedText = p.SpeedKBs > 0 ? $"  {p.SpeedKBs:F0} KB/s" : "";
                    DepProgressText.Text = $"{p.Stage}: {p.Message}{speedText}";
                });
            });
            var logProgress = new Progress<BuildLog>(log =>
            {
                Dispatcher.Invoke(() =>
                {
                    DepLog?.Append(log.Stream, log.Line);
                    BuildLog?.Append(log.Stream, log.Line);
                });
            });

            var ok = await action(_sdkService, sdkProgress, logProgress, CancellationToken.None);
            if (ok)
            {
                MessageBox.Show($"{toolName} 安装完成！", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await CheckDepsAsync();
            }
            else
            {
                MessageBox.Show($"{toolName} 安装失败，请查看日志。", "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _sdkService?.Dispose();
            _sdkService = null;
            btn.IsEnabled = true;
            btn.Content = btnText;
            EnableAllInstallButtons();
        }
    }

    private void DisableAllInstallButtons()
    {
        InstallJdkBtn.IsEnabled = false;
        InstallGradleBtn.IsEnabled = false;
        InstallCordovaBtn.IsEnabled = false;
        InstallSdkBtn.IsEnabled = false;
        RecheckBtn.IsEnabled = false;
    }

    private void EnableAllInstallButtons()
    {
        InstallJdkBtn.IsEnabled = true;
        InstallGradleBtn.IsEnabled = true;
        InstallCordovaBtn.IsEnabled = true;
        InstallSdkBtn.IsEnabled = true;
        RecheckBtn.IsEnabled = true;
    }

    private async void InstallSdkBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallSdkBtn.IsEnabled = false;
        InstallSdkBtn.Content = "安装中...";
        RecheckBtn.IsEnabled = false;
        InstallCordovaBtn.IsEnabled = false;
        try
        {
            _sdkService = new AndroidSdkService();
            var sdkProgress = new Progress<SdkProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    DepProgress.Value = p.Percent;
                    var speedText = p.SpeedKBs > 0 ? $"  {p.SpeedKBs:F0} KB/s" : "";
                    DepProgressText.Text = $"{p.Stage}: {p.Message}{speedText}";
                });
            });
            var logProgress = new Progress<BuildLog>(log =>
            {
                Dispatcher.Invoke(() =>
                {
                    DepLog?.Append(log.Stream, log.Line);
                    BuildLog?.Append(log.Stream, log.Line);
                });
            });

            var ok = await _sdkService.DownloadAndInstallAsync(sdkProgress, logProgress, CancellationToken.None);
            if (ok)
            {
                MessageBox.Show("Android SDK 安装完成！", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await CheckDepsAsync();
            }
            else
            {
                MessageBox.Show("SDK 安装失败，请查看日志。", "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"安装异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _sdkService?.Dispose();
            _sdkService = null;
            InstallSdkBtn.IsEnabled = true;
            InstallSdkBtn.Content = "安装 SDK";
            RecheckBtn.IsEnabled = true;
            InstallCordovaBtn.IsEnabled = true;
        }
    }

    private void BrowseIconBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
            Title = "选择应用图标（自动裁切缩放为 512x512）",
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var processedPath = ProcessIcon(dlg.FileName);
                IconPathBox.Text = processedPath;
                IconPathBox.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgBrush");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"图标处理失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// 将图标中心裁切为正方形并缩放到 512x512，保存到临时目录。
    /// </summary>
    private string ProcessIcon(string srcPath)
    {
        var src = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(srcPath, UriKind.Absolute));
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w == 0 || h == 0) throw new InvalidOperationException("无法读取图片尺寸");

        int size = Math.Min(w, h);
        int x = (w - size) / 2, y = (h - size) / 2;

        // 中心裁切为正方形
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(src,
            new Int32Rect(x, y, size, size));

        // 缩放到 512x512
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(cropped,
            new System.Windows.Media.ScaleTransform(512.0 / size, 512.0 / size));

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));

        var outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TyranoToolKit", "icons");
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, $"icon_{DateTime.Now:yyyyMMddHHmmss}.png");
        using var fs = new FileStream(outPath, FileMode.Create);
        encoder.Save(fs);
        return outPath;
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0) ShowStep(_step - 1);
    }

    private async void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            if (_selectedProject == null)
            {
                MessageBox.Show("请先选择一个项目", "提示");
                return;
            }
            ShowStep(1);
            await CheckDepsAsync();
            return;
        }
        if (_step == 1)
        {
            var failed = _deps.Where(d => !d.Ok).Select(d => d.Name).ToList();
            if (failed.Count > 0)
            {
                MessageBox.Show($"以下依赖未通过检测，请先安装：\n{string.Join("、", failed)}", "不能继续", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ShowStep(2);
            return;
        }
        if (_step == 2)
        {
            ShowStep(3);
            return;
        }
        if (_step == 3)
        {
            ShowStep(0);
        }
    }

    private BuildOptions BuildOptions()
    {
        return new BuildOptions
        {
            ProjectName = _selectedProject?.Name ?? "",
            AppName = AppNameBox.Text.Trim(),
            AppId = AppIdBox.Text.Trim(),
            AppVersion = AppVersionBox.Text.Trim(),
            IconPath = IconPathBox.Text.Contains("可选") ? "" : IconPathBox.Text.Trim(),
            BuildType = ReleaseRadio.IsChecked == true ? "release" : "debug",
        };
    }

    private async void BuildBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedProject?.Name))
        {
            MessageBox.Show("未选择项目", "提示");
            return;
        }
        BuildBtn.IsEnabled = false;
        CancelBuildBtn.IsEnabled = true;
        BuildStatusText.Text = "构建中...";
        BuildLog.Clear();
        _buildCts = new CancellationTokenSource();
        try
        {
            var opts = BuildOptions();
            var progress = new Progress<BuildLog>(log =>
            {
                BuildLog.Append(log.Stream, log.Line);
                if (!string.IsNullOrEmpty(log.Stage))
                    BuildStatusText.Text = log.Stage switch
                    {
                        "scaffold" => "1. 创建 Cordova 项目...",
                        "copy" => "2. 拷贝游戏文件...",
                        "platform" => "3. 添加 Android 平台...",
                        "build" => "4. 执行构建...",
                        "done" => "完成",
                        "error" => "构建失败",
                        "install" => "安装 Cordova...",
                        _ => BuildStatusText.Text,
                    };
            });
            var (ok, outPath) = await _builder.BuildAsync(opts, progress, _buildCts.Token);
            if (ok)
            {
                BuildStatusText.Text = $"构建成功：{outPath}";
                BuildLog.Append("done", $"产物：{outPath}");
                // 自动打开输出文件夹
                var outDir = Path.GetDirectoryName(outPath) ?? ApkBuilderService.ExportRoot;
                if (Directory.Exists(outDir))
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", outDir); } catch { }
                }
                MessageBox.Show($"APK 构建成功：\n{outPath}", "完成");
            }
            else
            {
                BuildStatusText.Text = "构建失败";
            }
        }
        catch (OperationCanceledException)
        {
            BuildStatusText.Text = "已取消";
            BuildLog.Append("stage", "构建已取消");
        }
        catch (Exception ex)
        {
            BuildStatusText.Text = "异常";
            BuildLog.Append("error", ex.Message);
        }
        finally
        {
            BuildBtn.IsEnabled = true;
            CancelBuildBtn.IsEnabled = false;
            _buildCts?.Dispose();
            _buildCts = null;
        }
    }

    private void CancelBuildBtn_Click(object sender, RoutedEventArgs e)
    {
        _buildCts?.Cancel();
        _builder.CancelBuild();
    }

    private void OpenExportDirBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = ApkBuilderService.ExportRoot;
        if (Directory.Exists(dir))
            System.Diagnostics.Process.Start("explorer.exe", dir);
        else
            MessageBox.Show($"目录尚未创建：\n{dir}", "提示");
    }
}
