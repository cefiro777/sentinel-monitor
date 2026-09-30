using Sentinel.Contracts.Modules;

namespace Sentinel.Contracts.Tests;

public class BackupScheduleTests
{
    private static DateTimeOffset Local(int y, int m, int d, int h, int min) => new(new DateTime(y, m, d, h, min, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(y, m, d, h, min, 0)));

    [Fact]
    public void Daily_after_last_run_goes_to_next_day_slot()
    {
        var s = new BackupSchedule { Type = "daily", Time = "02:00" };
        var lastRun = Local(2026, 9, 17, 2, 0);
        var next = s.NextRun(lastRun, Local(2026, 9, 17, 10, 0));
        Assert.Equal(Local(2026, 9, 18, 2, 0), next.ToLocalTime());
    }

    [Fact]
    public void Daily_never_run_and_slot_passed_today_runs_immediately()
    {
        var s = new BackupSchedule { Type = "daily", Time = "02:00" };
        var now = Local(2026, 9, 17, 10, 0);
        Assert.Equal(now, s.NextRun(null, now));
    }

    [Fact]
    public void Daily_never_run_and_slot_ahead_waits_for_slot()
    {
        var s = new BackupSchedule { Type = "daily", Time = "23:00" };
        var now = Local(2026, 9, 17, 10, 0);
        Assert.Equal(Local(2026, 9, 17, 23, 0), s.NextRun(null, now).ToLocalTime());
    }

    [Fact]
    public void Hourly_is_relative_to_last_run()
    {
        var s = new BackupSchedule { Type = "hourly", EveryHours = 4 };
        var last = Local(2026, 9, 17, 10, 0);
        Assert.Equal(last.AddHours(4), s.NextRun(last, last.AddHours(1)));
        Assert.Equal(TimeSpan.FromHours(4), s.ExpectedPeriod());
    }

    [Fact]
    public void Weekly_skips_days_not_in_list()
    {
        // 17.09.2026 — четверг (4). Разрешены только пн (1) и пт (5).
        var s = new BackupSchedule { Type = "weekly", Time = "03:00", DaysOfWeek = new List<int> { 1, 5 } };
        var next = s.NextRun(Local(2026, 9, 17, 3, 0), Local(2026, 9, 17, 12, 0)).ToLocalTime();
        Assert.Equal(DayOfWeek.Friday, next.DayOfWeek);
        Assert.Equal(new DateTime(2026, 9, 18, 3, 0, 0), next.DateTime);
    }

    [Fact]
    public void Expected_period_for_dead_mans_switch()
    {
        Assert.Equal(TimeSpan.FromDays(1), new BackupSchedule { Type = "daily" }.ExpectedPeriod());
        Assert.Equal(TimeSpan.FromDays(7), new BackupSchedule { Type = "weekly", DaysOfWeek = new List<int> { 6 } }.ExpectedPeriod());
        Assert.Equal(TimeSpan.FromDays(1), new BackupSchedule { Type = "weekly", DaysOfWeek = new List<int> { 1, 2, 3, 4, 5 } }.ExpectedPeriod());
    }
}
