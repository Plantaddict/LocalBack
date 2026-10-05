using LocalBack.Core.Model;
using LocalBack.Core.Service;

namespace LocalBack.Core.Tests;

public class ServiceTests
{
    private static async Task WaitFor(Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Live_set_backs_up_a_saved_file_within_seconds()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "one");
        using var service = new BackupService(new AppPaths(Path.Combine(env.Dir, "home"), Path.Combine(env.Dir, "tmp")));
        service.Settings.DebounceSeconds = 1;
        service.Engine.YoungEmptyFileAge = TimeSpan.Zero;
        service.Start(checkOnStartup: false);

        var set = service.AddSet("Desktop", new[] { env.Source }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        await WaitFor(() => service.GetStatus(set).Health == SetHealth.UpToDate);
        Assert.Equal(1, service.GetStatus(set).Versions);

        File.WriteAllText(Path.Combine(env.Source, "b.txt"), "saved");
        File.WriteAllText(Path.Combine(env.Source, "~$b.txt"), "lock file, ignored");
        await WaitFor(() => service.GetStatus(set).Versions == 2);

        var latest = service.GetStatus(set).Latest!;
        Assert.Equal(2, latest.Files);
        Assert.Equal(1, latest.Added);
    }

    [Fact]
    public async Task Changes_while_paused_are_queued_then_backed_up_on_resume()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "one");
        using var service = new BackupService(new AppPaths(Path.Combine(env.Dir, "home"), Path.Combine(env.Dir, "tmp")));
        service.Settings.DebounceSeconds = 1;
        service.Engine.YoungEmptyFileAge = TimeSpan.Zero;
        service.Start(checkOnStartup: false);
        var set = service.AddSet("Docs", new[] { env.Source }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        await WaitFor(() => service.GetStatus(set).Health == SetHealth.UpToDate);

        service.Pause(TimeSpan.FromHours(1));
        File.WriteAllText(Path.Combine(env.Source, "c.txt"), "while paused");
        await WaitFor(() => service.GetStatus(set).PendingCount == 1);
        Assert.Equal(SetHealth.Paused, service.GetStatus(set).Health);
        Assert.Equal(1, service.GetStatus(set).Versions);

        service.Resume();
        await WaitFor(() => service.GetStatus(set).Versions == 2 && service.GetStatus(set).PendingCount == 0);
    }
}
