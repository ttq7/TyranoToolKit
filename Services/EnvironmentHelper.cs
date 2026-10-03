using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TyranoToolKit.Services;

/// <summary>
/// 读写当前用户的环境变量，并通过广播通知其他进程刷新环境。
/// </summary>
public static class EnvironmentHelper
{
    private const string EnvironmentSubKey = "Environment";

    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint Msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// 读取当前用户的环境变量值。不存在返回空字符串。
    /// </summary>
    public static string GetUserEnvironment(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentSubKey);
        if (key == null) return "";
        var v = key.GetValue(name);
        return v as string ?? "";
    }

    /// <summary>
    /// 写入当前用户环境变量（REG_EXPAND_SZ），并广播 WM_SETTINGCHANGE 通知。
    /// </summary>
    public static void SetUserEnvironment(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentSubKey, writable: true);
        if (key == null) return;
        key.SetValue(name, value, RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
    }

    /// <summary>
    /// 删除当前用户环境变量。不存在时不报错。
    /// </summary>
    public static void DeleteUserEnvironment(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentSubKey, writable: true);
        if (key == null) return;
        if (key.GetValue(name) == null) return;
        key.DeleteValue(name, throwOnMissingValue: false);
        BroadcastEnvironmentChange();
    }

    /// <summary>
    /// 返回当前 ELECTRON_MIRROR 环境变量值。
    /// </summary>
    public static string GetElectronMirrorInfo()
    {
        return GetUserEnvironment("ELECTRON_MIRROR");
    }

    private static void BroadcastEnvironmentChange()
    {
        SendMessageTimeoutW((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
            "Environment", SMTO_ABORTIFHUNG, 1000, out _);
    }
}
