namespace TyranoToolKit.Models;

/// <summary>
/// APK 构建选项。
/// </summary>
public class BuildOptions
{
    public string ProjectName { get; set; } = "";
    public string AppName { get; set; } = "MyGame";
    public string AppId { get; set; } = "com.example.mygame";
    public string AppVersion { get; set; } = "1.0.0";
    public string IconPath { get; set; } = "";
    public string BuildType { get; set; } = "debug";  // debug/release
}
