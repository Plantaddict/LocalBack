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

public class ServiceReviewFixTests
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
    public async Task A_disabled_set_is_never_backed_up_but_can_be_turned_on()
    {
        using var env = new TestEnv();
        env.Write("a.txt", "one");
        using var service = new BackupService(new AppPaths(Path.Combine(env.Dir, "home"), Path.Combine(env.Dir, "tmp")));
        service.Settings.DebounceSeconds = 1;
        service.Engine.YoungEmptyFileAge = TimeSpan.Zero;
        service.Start(checkOnStartup: false);
        var set = service.AddSet("Desktop", new[] { env.Source }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        await WaitFor(() => service.GetStatus(set).Health == SetHealth.UpToDate);

        service.SetEnabled(set, false);
        Assert.Equal(SetHealth.Disabled, service.GetStatus(set).Health);
        File.WriteAllText(Path.Combine(env.Source, "b.txt"), "saved while disabled");
        await service.BackUpNowAsync(); // completes without running the disabled set
        await Task.Delay(2500);
        Assert.Equal(1, service.GetStatus(set).Versions);

        service.SetEnabled(set, true);
        await WaitFor(() => service.GetStatus(set).Versions == 2);
    }

    [Fact]
    public async Task Backing_up_a_set_that_was_removed_does_not_hang()
    {
        using var env = new TestEnv();
        using var service = new BackupService(new AppPaths(Path.Combine(env.Dir, "home"), Path.Combine(env.Dir, "tmp")));
        service.Start(checkOnStartup: false);
        var set = service.AddSet("Gone", new[] { env.Source }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true);
        await WaitFor(() => service.GetStatus(set).Health == SetHealth.UpToDate);
        await service.RemoveSetAsync(set, deleteBackups: false);

        var run = service.BackUpNowAsync(set);
        Assert.True(await Task.WhenAny(run, Task.Delay(10000)) == run, "BackUpNowAsync hung on a removed set");
        Assert.False(service.IsBusy);
    }

    [Fact]
    public void Adding_nested_folders_fails_cleanly()
    {
        using var env = new TestEnv();
        var sub = Path.Combine(env.Source, "sub");
        Directory.CreateDirectory(sub);
        using var service = new BackupService(new AppPaths(Path.Combine(env.Dir, "home"), Path.Combine(env.Dir, "tmp")));
        Assert.Throws<ArgumentException>(() =>
            service.AddSet("Nested", new[] { env.Source, sub }, env.Usb, RunSchedule.Live, ExclusionRules.DefaultEnabled, Array.Empty<string>(), true));
        Assert.Empty(service.Sets);
    }
}
