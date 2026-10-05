using System.IO;
using LocalBack.App.Services;
using LocalBack.Core.Service;
using Microsoft.Win32;

namespace LocalBack.App.Tests;

/// <summary>Windows pieces that the screen test does not touch: registry, tray icon, device window, single instance.</summary>
public class IntegrationTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "timed out");
        if (error != null) throw new Exception(error.ToString(), error);
    }

    [Fact]
    public void Autostart_and_explorer_menu_are_written_and_removed()
    {
        try
        {
            WindowsIntegration.Apply(startWithWindows: true, explorerMenu: true);

            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Assert.EndsWith("--tray", (string)run!.GetValue("LocalBack")!);
            foreach (var key in new[] { @"Software\Classes\*\shell\LocalBack", @"Software\Classes\Directory\shell\LocalBack" })
            {
                using var verb = Registry.CurrentUser.OpenSubKey(key);
                Assert.Equal("Show LocalBack versions", verb!.GetValue(""));
                using var cmd = verb.OpenSubKey("command");
                Assert.Contains("--history \"%1\"", (string)cmd!.GetValue("")!);
            }

            WindowsIntegration.Apply(startWithWindows: false, explorerMenu: false);

            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                Assert.Null(run?.GetValue("LocalBack"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\LocalBack"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\LocalBack"));
        }
        finally
        {
            WindowsIntegration.Apply(false, false);
        }
    }

    [Fact]
    public void Tray_icon_draws_every_state() => OnSta(() =>
    {
        using var tray = new TrayIcon();
        foreach (var h in Enum.GetValues<SetHealth>())
            tray.Update(h, "LocalBack — " + h + new string('x', 80), paused: h == SetHealth.Paused);
        tray.ShowBalloon("Title", "Text", error: false);
    });

    [Fact]
    public void Device_window_can_be_created() => OnSta(() =>
    {
        using var d = new DeviceNotifier();
    });

    [Fact]
    public void Second_launch_hands_its_arguments_to_the_first()
    {
        using var first = new SingleInstance();
        Assert.True(first.TryAcquire());
        var received = new TaskCompletionSource<string[]>();
        first.ArgumentsReceived += a => received.TrySetResult(a);
        first.Listen();

        using (var second = new SingleInstance())
            Assert.False(second.TryAcquire());

        SingleInstance.Send(new[] { "--history", @"C:\Users\You\Desktop\notes.txt" });
        Assert.True(received.Task.Wait(TimeSpan.FromSeconds(10)), "arguments not received");
        Assert.Equal(new[] { "--history", @"C:\Users\You\Desktop\notes.txt" }, received.Task.Result);
    }
}
