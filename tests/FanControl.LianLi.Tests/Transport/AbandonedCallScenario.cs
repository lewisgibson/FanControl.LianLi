using System;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// Runs a scenario about a call that was under way when the bound gave up on it, until it was. A
/// thread that had not started by the deadline never runs the call (its own tests are in
/// <see cref="BoundedDeviceCallTests"/>), and on a loaded machine that happens even with a 50 ms
/// deadline, so an attempt in which the call never started proves nothing about the case and is
/// made again. The scenario says whether the call started, and only once the thread is done with it
/// either way, so nothing the scenario owns is disposed under a thread still using it.
/// </summary>
internal static class AbandonedCallScenario {
    public static void Run(Func<bool> attempt) {
        for (int attempts = 0; attempts < 20; attempts++) {
            if (attempt()) {
                return;
            }
        }

        Assert.Fail("the call never started before the caller gave up");
    }
}
