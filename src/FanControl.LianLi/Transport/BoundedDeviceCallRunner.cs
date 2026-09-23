using System;
using System.Globalization;
using System.Threading;
using FanControl.LianLi.Logging;

namespace FanControl.LianLi.Transport;

/// <summary>
/// The production <see cref="IDeviceCallRunner"/>: <see cref="BoundedDeviceCall"/> on a real thread,
/// with a failure that lands after the caller gave up written to the plugin log. That failure is often
/// the only record of what the wedged device finally did - the open that failed, the close that threw -
/// once the timeout line has already been written.
/// </summary>
internal sealed class BoundedDeviceCallRunner : IDeviceCallRunner {
    private readonly ILog _log;

    public BoundedDeviceCallRunner(ILog log) {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool TryRun(
        string operation, Action<CancellationToken> call, int timeoutMilliseconds, Action onTimeout, Action onReturned) {
        if (operation is null) {
            throw new ArgumentNullException(nameof(operation));
        }

        return BoundedDeviceCall.TryRun(
            call,
            timeoutMilliseconds,
            onTimeout,
            failure => _log.Write(string.Format(
                CultureInfo.InvariantCulture,
                "  {0} failed after its {1} ms bound had already given up on it: {2}: {3}",
                operation,
                timeoutMilliseconds,
                failure.GetType().Name,
                failure.Message)),
            onReturned);
    }
}
