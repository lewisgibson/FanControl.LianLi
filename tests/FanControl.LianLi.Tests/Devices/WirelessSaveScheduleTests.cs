using System;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>RFController.SaveConfig's debounce and MasterDevice.CheckSaveConfig's periodic save.</summary>
public sealed class WirelessSaveScheduleTests {
    private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void TakeDebouncedSave_NothingIsDueWithoutAStream() {
        var schedule = new WirelessSaveSchedule(Start);

        Assert.False(schedule.TakeDebouncedSave(At(3600)));
    }

    // SaveConfig: Thread.Sleep(10000), then save unless a request came in the last five seconds.
    [Fact]
    public void TakeDebouncedSave_SavesTenSecondsAfterAQuietStream() {
        var schedule = new WirelessSaveSchedule(Start);
        schedule.EffectStreamed(At(1));

        Assert.False(schedule.TakeDebouncedSave(At(10.9)));
        Assert.True(schedule.TakeDebouncedSave(At(11)));
        Assert.False(schedule.TakeDebouncedSave(At(30)));
    }

    // SaveConfig: a request while the wait runs only moves dtBindingChanged; within five seconds of
    // the check the wait goes round again for another ten.
    [Fact]
    public void TakeDebouncedSave_ABusyStreamPostponesTheSaveInTenSecondSteps() {
        var schedule = new WirelessSaveSchedule(Start);
        schedule.EffectStreamed(At(0));
        schedule.EffectStreamed(At(7));

        Assert.False(schedule.TakeDebouncedSave(At(10))); // 3 s since the last request
        Assert.False(schedule.TakeDebouncedSave(At(19)));
        Assert.True(schedule.TakeDebouncedSave(At(20)));
    }

    // CheckSaveConfig: (now - start_time > 1 h || now - last_save_time > 3 h) && now - last_set_effect_time > 30 s,
    // then start_time = now.AddMonths(1).
    // A save made on the way out of a controller FanControl's refresh closes ends the wait, so the
    // controller that takes the schedule over does not save the same stream again.
    [Fact]
    public void Saved_EndsAPendingWait() {
        var schedule = new WirelessSaveSchedule(Start);
        schedule.EffectStreamed(At(1));

        schedule.Saved(At(3));

        Assert.False(schedule.HasPendingSave);
        Assert.False(schedule.TakeDebouncedSave(At(20)));
    }

    [Fact]
    public void TakePeriodicSave_OnceAnHourAfterStartThenEveryThreeHours() {
        var schedule = new WirelessSaveSchedule(Start);

        Assert.False(schedule.TakePeriodicSave(At(3600)));
        Assert.True(schedule.TakePeriodicSave(At(3601)));
        schedule.Saved(At(3601));
        Assert.False(schedule.TakePeriodicSave(At(3601 + 10800)));
        Assert.True(schedule.TakePeriodicSave(At(3602 + 10800)));
    }

    [Fact]
    public void TakePeriodicSave_WaitsThirtySecondsAfterAStream() {
        var schedule = new WirelessSaveSchedule(Start);
        schedule.EffectStreamed(At(3590));

        Assert.False(schedule.TakePeriodicSave(At(3620)));
        Assert.True(schedule.TakePeriodicSave(At(3621)));
    }

    [Fact]
    public void AClockThatWentBack_DoesNotHoldTheDebouncedSaveBack() {
        var start = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        var schedule = new WirelessSaveSchedule(start);
        schedule.EffectStreamed(start);

        // The clock is set back an hour: the save, due ten seconds after the stream, is not held for an hour.
        Assert.True(schedule.TakeDebouncedSave(start.AddHours(-1)));
    }
}
