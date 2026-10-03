namespace TyranoToolKit.Models;

/// <summary>
/// 表示一个外部依赖（java/javac/adb/cordova/gradle）的检测结果。
/// </summary>
public class DependencyStatus
{
    public string Name { get; set; } = "";     // java/javac/adb/cordova/gradle
    public bool Ok { get; set; }
    public string Version { get; set; } = "";
    public string Error { get; set; } = "";
    public string Description { get; set; } = "";
}
