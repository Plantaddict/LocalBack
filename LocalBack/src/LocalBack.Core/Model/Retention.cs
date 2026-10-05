using System.Text.Json.Serialization;

namespace LocalBack.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter<RetentionKind>))]
public enum RetentionKind
{
    /// <summary>Keep the last N versions of each file.</summary>
    KeepLast,
    /// <summary>Keep one version per day for 30 days, then one per week.</summary>
    DailyThenWeekly,
    /// <summary>Remove versions that were replaced more than N days ago.</summary>
    OlderThan,
}

public sealed record RetentionPlan(RetentionKind Kind, int Count = 3, int Days = 90)
{
    public static readonly RetentionPlan Default = new(RetentionKind.DailyThenWeekly);

    [JsonIgnore]
    public string Title => Kind switch
    {
        RetentionKind.KeepLast => $"Keep only the last {Count} versions of each file",
        RetentionKind.DailyThenWeekly => "Keep 1 version per day for 30 days, then 1 per week",
        _ => $"Remove versions older than {Days} days",
    };

    [JsonIgnore]
    public string Description => Kind switch
    {
        RetentionKind.KeepLast => "Older copies of the same file are removed. Good if you rarely go back more than a few saves.",
        RetentionKind.DailyThenWeekly => "Thins out the many copies made on the same day. Keeps a long trail of changes.",
        _ => Days == 90
            ? "Everything from the last three months stays exactly as it is."
            : $"Everything from the last {Days} days stays exactly as it is.",
    };

    public static IReadOnlyList<RetentionPlan> Choices { get; } = new[]
    {
        new RetentionPlan(RetentionKind.KeepLast, Count: 3),
        new RetentionPlan(RetentionKind.DailyThenWeekly),
        new RetentionPlan(RetentionKind.OlderThan, Days: 90),
    };
}
