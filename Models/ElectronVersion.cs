namespace TyranoToolKit.Models;

/// <summary>
/// 表示一个 Electron 版本及其本地缓存与下载状态。
/// </summary>
public class ElectronVersion
{
    public string Version { get; set; } = "";
    public bool IsLatest { get; set; }
    public bool IsStable { get; set; }
    public bool IsCached { get; set; }
    public bool IsActive { get; set; }
    public long SizeBytes { get; set; }
    public string Stage { get; set; } = "";  // idle/downloading/verifying/done/error
    public double Progress { get; set; }    // 0-100
    public string SpeedText { get; set; } = "";
}
