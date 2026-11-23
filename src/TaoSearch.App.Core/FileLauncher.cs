using System.Diagnostics;
using System.Runtime.Versioning;

namespace TaoSearch.App.Core;

/// <summary>文件打开与资源管理器定位（主窗/快速窗共用）。</summary>
public static class FileLauncher
{
    /// <summary>用系统默认程序打开文件；失败回退到定位目录。</summary>
    [SupportedOSPlatform("windows")]
    public static bool OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return RevealInExplorer(path);
        }
    }

    /// <summary>在资源管理器中定位文件所在目录。</summary>
    [SupportedOSPlatform("windows")]
    public static bool RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
