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
        t.Start();
        Assert.True(t.Join(TimeSpan.FromMinutes(3)), "UI test timed out");
        if (error != null) throw new Exception("UI test failed: " + error, error);
    }

    private static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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

        var service = new BackupService(new AppPaths(Path.Combine(root, "home"), Path.Combine(root, "tmp")));
        service.Engine.YoungEmptyFileAge = TimeSpan.Zero;
        var set1 = service.AddSet("Desktop", new[] { desktop }, usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        var set2 = service.AddSet("Documents", new[] { docs }, usb, RunSchedule.Hourly, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        service.Engine.BackupAsync(set1, Core.Storage.SnapshotTrigger.FirstBackup).GetAwaiter().GetResult();
        service.Engine.BackupAsync(set2, Core.Storage.SnapshotTrigger.FirstBackup).GetAwaiter().GetResult();
        Write(Path.Combine(desktop, "Quarterly report.xlsx"), 1250_000, 60);
        Write(Path.Combine(desktop, "Invoice 2026-118.pdf"), 340_000, 60);
        Write(Path.Combine(desktop, "screenshot-2026-10-05.png"), 2600_000, 60);
        File.Delete(Path.Combine(desktop, "old-draft.docx"));
        service.Engine.BackupAsync(set1, Core.Storage.SnapshotTrigger.Live).GetAwaiter().GetResult();
        service.RefreshAll();

        var app = new App();
        app.InitializeComponent();
        app.UseServiceForTests(service);

        var main = new MainWindow();
        main.Show();
        Pump(500);
        Snap(main, "Main");

        main.ViewModel.ShowHistory(set1, null);
        Pump(1500);
        Snap(main, "History");

        main.ViewModel.ShowHistory(null, Path.Combine(desktop, "notes.txt"));
        Pump(1500);
        Snap(main, "History-file");

        main.ViewModel.Navigate(Page.Drives);
        Pump(1500);
        Snap(main, "Drives");

        main.ViewModel.Navigate(Page.Settings);
        Pump(300);
        Snap(main, "Settings");

        var add = new AddSetWindow(null);
        add.Show();
        Pump(500);
        Snap(add, "AddSet");
        add.Close();

        var edit = new AddSetWindow(set2);
        edit.Show();
        Pump(300);
        Snap(edit, "EditSet");
        edit.Close();

        var free = new FreeSpaceWindow(service.DriveFor(set1)!, lowSpace: true);
        free.Show();
        Pump(2000);
        Snap(free, "FreeSpace");
        free.Close();

        var tray = new TrayFlyout();
        tray.Show();
        Pump(300);
        Snap(tray, "Tray");
        tray.Close();

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
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new DispatcherOperationCallback(_ =>
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
        using var fs = File.Create(Path.Combine(Shots, name + ".png"));
        enc.Save(fs);
    }
}
