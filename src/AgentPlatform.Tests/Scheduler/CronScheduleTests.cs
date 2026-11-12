using AgentPlatform.Core.Scheduling;

namespace AgentPlatform.Tests.Scheduler;

/// <summary>
/// Минимальный cron-парсер планировщика: 5 полей, списки/диапазоны/шаги,
/// UTC и правило «день месяца ИЛИ день недели».
/// </summary>
public sealed class CronScheduleTests
{
    private static readonly DateTimeOffset ThursdayNoon =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_minute_expression_runs_at_the_next_minute()
    {
        Assert.True(CronSchedule.TryParse("* * * * *", out var schedule, out var error), error);
        Assert.NotNull(schedule);

        Assert.True(schedule.TryGetNextOccurrence(ThursdayNoon.AddSeconds(30), out var next));
        Assert.Equal(ThursdayNoon.AddMinutes(1), next);
    }

    [Fact]
    public void IsDue_is_false_before_occurrence_and_true_after_it()
    {
        Assert.True(CronSchedule.TryParse("* * * * *", out var schedule, out _));

        Assert.False(schedule.IsDue(ThursdayNoon, ThursdayNoon.AddSeconds(30)));
        Assert.True(schedule.IsDue(ThursdayNoon, ThursdayNoon.AddMinutes(1)));
        Assert.True(schedule.IsDue(ThursdayNoon, ThursdayNoon.AddHours(2)));
    }

    [Fact]
    public void Steps_and_ranges_are_supported()
    {
        Assert.True(CronSchedule.TryParse("*/15 9-18 * * 1-5", out var schedule, out var error), error);
        Assert.NotNull(schedule);

        // 2026-01-01 — четверг; ближайшие 15 минут в рабочем окне — 12:15.
        Assert.True(schedule.TryGetNextOccurrence(ThursdayNoon, out var next));
        Assert.Equal(ThursdayNoon.AddMinutes(15), next);

        // После 19:00 запуск переезжает на следующий рабочий день в 09:00.
        Assert.True(schedule.TryGetNextOccurrence(new DateTimeOffset(2026, 1, 1, 19, 0, 0, TimeSpan.Zero), out var nextDay));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero), nextDay);
    }

    [Fact]
    public void Sunday_can_be_specified_as_seven()
    {
        Assert.True(CronSchedule.TryParse("0 9 * * 7", out var schedule, out var error), error);
        Assert.NotNull(schedule);

        // 2026-01-04 — воскресенье.
        Assert.True(schedule.TryGetNextOccurrence(ThursdayNoon, out var next));
        Assert.Equal(new DateTimeOffset(2026, 1, 4, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Day_of_month_or_day_of_week_matches_either_restriction()
    {
        // 13-е число ИЛИ пятница. Ближайшая пятница — 2026-01-02.
        Assert.True(CronSchedule.TryParse("0 0 13 * 5", out var schedule, out var error), error);
        Assert.NotNull(schedule);

        Assert.True(schedule.TryGetNextOccurrence(ThursdayNoon, out var next));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), next);
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    public void Invalid_expressions_are_rejected(string expression)
    {
        Assert.False(CronSchedule.TryParse(expression, out var schedule, out var error));
        Assert.Null(schedule);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Impossible_date_is_not_due()
    {
        // 30 февраля не существует: запусков нет.
        Assert.True(CronSchedule.TryParse("0 0 30 2 *", out var schedule, out var error), error);
        Assert.NotNull(schedule);

        Assert.False(schedule.TryGetNextOccurrence(ThursdayNoon, out _));
        Assert.False(schedule.IsDue(ThursdayNoon, ThursdayNoon.AddYears(1)));
    }
}
