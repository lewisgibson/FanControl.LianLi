using System;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Plugin;

/// <summary>
/// How one controller build ended within the deadline <see cref="ControllerBuilder"/> set: it
/// built, it threw, or it was still running when the deadline passed.
/// </summary>
internal sealed class BuildOutcome {
    private BuildOutcome(IFanDevice? controller, Exception? error, bool timedOut) {
        Controller = controller;
        Error = error;
        TimedOut = timedOut;
    }

    /// <summary>The controller, when the build finished in time; the caller owns it.</summary>
    public IFanDevice? Controller { get; }

    /// <summary>What the build threw, when it failed in time.</summary>
    public Exception? Error { get; }

    /// <summary>Whether the build was still running at the deadline.</summary>
    public bool TimedOut { get; }

    /// <summary>A build that produced <paramref name="controller"/>.</summary>
    public static BuildOutcome Built(IFanDevice controller)
        => new BuildOutcome(controller ?? throw new ArgumentNullException(nameof(controller)), null, false);

    /// <summary>A build that threw <paramref name="error"/>.</summary>
    public static BuildOutcome Failed(Exception error)
        => new BuildOutcome(null, error ?? throw new ArgumentNullException(nameof(error)), false);

    /// <summary>A build still running at the deadline.</summary>
    public static BuildOutcome Late() => new BuildOutcome(null, null, true);

    /// <summary>
    /// Hand the result to whichever callback fits: the controller (which the callback then owns)
    /// or the error. A late outcome carries neither, and delivers nothing.
    /// </summary>
    public void Deliver(int index, Action<int, IFanDevice> built, Action<int, Exception> failed) {
        if (built is null) {
            throw new ArgumentNullException(nameof(built));
        }

        if (failed is null) {
            throw new ArgumentNullException(nameof(failed));
        }

        if (Controller != null) {
            built(index, Controller);
        } else if (Error != null) {
            failed(index, Error);
        }
    }
}
