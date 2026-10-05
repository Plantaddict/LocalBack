using System.Diagnostics;
using LocalBack.Core.Util;
using Microsoft.Win32;

namespace LocalBack.App.Services;

/// <summary>Per-user registry entries: Run-key autostart and the Explorer context menu. No admin, no shell extension DLL.</summary>
public static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalBack";
    private static readonly string[] MenuKeys = { @"Software\Classes\*\shell\LocalBack", @"Software\Classes\Directory\shell\LocalBack" };

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LocalBack.exe");

    public static void Apply(bool startWithWindows, bool explorerMenu)
    {
        try
        {
            SetAutostart(startWithWindows);
            SetExplorerMenu(explorerMenu);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn($"Could not update Windows integration: {ex.Message}");
        }
    }

    private static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, $"\"{ExePath}\" --tray");
        else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
    }

    private static void SetExplorerMenu(bool on)
    {
        foreach (var path in MenuKeys)
        {
            if (!on)
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                continue;
            }
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("", "Show LocalBack versions");
            key.SetValue("Icon", $"\"{ExePath}\",0");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{ExePath}\" --history \"%1\"");
        }
    }

    public static void OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void ShowInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static bool IsOnBattery()
    {
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
        }
        catch (Exception) { return false; }
    }
}
