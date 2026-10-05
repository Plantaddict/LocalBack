using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalBack.App.ViewModels;
using LocalBack.App.Views;
using LocalBack.Core.Model;
using LocalBack.Core.Service;

namespace LocalBack.App.Tests;

/// <summary>
/// Opens every screen against a real backup set, so XAML and binding problems that only show at run time fail the build.
/// Each screen is saved as a PNG under screenshots/ for comparing with design/.
/// </summary>
public class ScreensTests
{
    private static readonly string Shots = Path.Combine(AppContext.BaseDirectory, "screenshots");
    private static readonly List<string> Steps = new();

    private static void Step(string s)
    {
        lock (Steps) Steps.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
    }

    [Fact]
    public void Every_screen_opens_and_renders()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { Run(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        bool finished = t.Join(TimeSpan.FromMinutes(2));
        string steps;
        lock (Steps) steps = string.Join(Environment.NewLine, Steps);
        Assert.True(finished, "UI test timed out after these steps:" + Environment.NewLine + steps + Environment.NewLine + StackOf(t.ManagedThreadId));
        if (error != null) throw new Exception("UI test failed after these steps:" + Environment.NewLine + steps + Environment.NewLine + error, error);
    }

    /// <summary>Stack of a hung thread, read from a snapshot of this process.</summary>
    private static string StackOf(int managedThreadId)
    {
        try
        {
            using var target = Microsoft.Diagnostics.Runtime.DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
            var runtime = target.ClrVersions[0].CreateRuntime();
            var thread = runtime.Threads.FirstOrDefault(x => x.ManagedThreadId == managedThreadId);
            if (thread == null) return "(thread not found)";
            return string.Join(Environment.NewLine, thread.EnumerateStackTrace().Take(60).Select(f => "  at " + (f.Method?.Signature ?? f.FrameName ?? f.Kind.ToString())));
        }
        catch (Exception ex)
        {
            return "(no stack: " + ex.Message + ")";
        }
    }

    private static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => Step("dispatcher error: " + e.Exception);
        Directory.CreateDirectory(Shots);
        var root = Path.Combine(Path.GetTempPath(), "lb-ui-" + Guid.NewGuid().ToString("N")[..6]);
        var desktop = Path.Combine(root, "Desktop");
        var docs = Path.Combine(root, "Documents");
        var usb = Path.Combine(root, "usb");
        foreach (var d in new[] { desktop, docs, usb, Path.Combine(docs, "Invoices") }) Directory.CreateDirectory(d);
        Write(Path.Combine(desktop, "Quarterly report.xlsx"), 1200_000, 120);
        Write(Path.Combine(desktop, "notes.txt"), 4_000, 110);
        Write(Path.Combine(desktop, "old-draft.docx"), 88_000, 100);
        Write(Path.Combine(docs, "Invoices", "Invoice 2026-117.pdf"), 340_000, 100);

        Step("service");
        var service = new BackupService(new AppPaths(Path.Combine(root, "home"), Path.Combine(root, "tmp")));
        service.Engine.YoungEmptyFileAge = TimeSpan.Zero;
        var set1 = service.AddSet("Desktop", new[] { desktop }, usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        var set2 = service.AddSet("Documents", new[] { docs }, usb, RunSchedule.Hourly, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        Step("first backups");
        service.Engine.BackupAsync(set1, Core.Storage.SnapshotTrigger.FirstBackup).GetAwaiter().GetResult();
        service.Engine.BackupAsync(set2, Core.Storage.SnapshotTrigger.FirstBackup).GetAwaiter().GetResult();
        Write(Path.Combine(desktop, "Quarterly report.xlsx"), 1250_000, 60);
        Write(Path.Combine(desktop, "Invoice 2026-118.pdf"), 340_000, 60);
        Write(Path.Combine(desktop, "screenshot-2026-10-05.png"), 2600_000, 60);
        File.Delete(Path.Combine(desktop, "old-draft.docx"));
        service.Engine.BackupAsync(set1, Core.Storage.SnapshotTrigger.Live).GetAwaiter().GetResult();
        service.RefreshAll();

        Step("app");
        App.TestMode = true;
        var app = new App();
        app.InitializeComponent();
        app.UseServiceForTests(service);

        Step("main window");
        var main = new MainWindow();
        Step("show main");
        main.Show();
        Step("shown");
        Pump(500);
        Step("pumped");
        Step("snap main");
        Snap(main, "Main");

        Step("history");
        main.ViewModel.ShowHistory(set1, null);
        Pump(1500);
        Snap(main, "History");

        main.ViewModel.ShowHistory(null, Path.Combine(desktop, "notes.txt"));
        Pump(1500);
        Snap(main, "History-file");

        Step("drives");
        main.ViewModel.Navigate(Page.Drives);
        Pump(1500);
        Snap(main, "Drives");

        Step("settings");
        main.ViewModel.Navigate(Page.Settings);
        Pump(300);
        Snap(main, "Settings");

        Step("add set");
        var add = new AddSetWindow(null);
        add.Show();
        Pump(500);
        Snap(add, "AddSet");
        add.Close();

        Step("edit set");
        var edit = new AddSetWindow(set2);
        edit.Show();
        Pump(300);
        Snap(edit, "EditSet");
        edit.Close();

        Step("free space");
        var free = new FreeSpaceWindow(service.DriveFor(set1)!, lowSpace: true);
        free.Show();
        Pump(2000);
        Snap(free, "FreeSpace");
        free.Close();

        Step("tray");
        var tray = new TrayFlyout();
        tray.Show();
        Pump(300);
        Snap(tray, "Tray");
        tray.Close();

        Step("close");
        main.Close();
        Pump(100);
        service.Dispose();
        app.Shutdown();
    }

    private static void Write(string path, int size, int secondsAgo)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-secondsAgo));
    }

    /// <summary>Runs the dispatcher for a while so async loads and bindings complete.</summary>
    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(_ =>
            {
                frame.Continue = false;
                return null;
            }), null);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        } while (DateTime.UtcNow < until);
    }

    private static void Snap(Window w, string name)
    {
        Step("snap " + name);
        var content = (FrameworkElement)w.Content;
        content.UpdateLayout();
        int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
        Assert.True(width > 0 && height > 0, $"{name} has no size");
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(Shots, name + ".png"))) enc.Save(fs);
    }
}
