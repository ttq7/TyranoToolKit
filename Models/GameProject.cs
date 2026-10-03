namespace TyranoToolKit.Models;

/// <summary>
/// 表示一个被识别的 Tyrano 游戏项目。
/// </summary>
public class GameProject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool HasIndex { get; set; }
    public string Source { get; set; } = "";  // "本地"/"Steam"
}
