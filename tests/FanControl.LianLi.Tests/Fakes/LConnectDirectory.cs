using System;
using System.IO;
using System.Threading;
using FanControl.LianLi.Devices;

namespace FanControl.LianLi.Tests.Fakes;

// A throwaway stand-in for L-Connect's data directory, so no test ever reads the machine's own
// (a developer running the suite on Windows may well have L-Connect installed). Created empty;
// a test writes whatever documents it needs under Locations, and Dispose removes the lot.
internal sealed class LConnectDirectory : IDisposable {
    public LConnectDirectory() {
        Locations = new LConnectLocations(
            Path.Combine(Path.GetTempPath(), "lianli-lconnect-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Locations.DataDirectory);
    }

    // Locations under a directory that does not exist: L-Connect was never installed.
    public static LConnectLocations Absent => new LConnectLocations(
        Path.Combine(Path.GetTempPath(), "lianli-no-lconnect-" + Guid.NewGuid().ToString("N")));

    public LConnectLocations Locations { get; }

    // A file a test left open is a leak the test should fail on, so a delete that still fails once
    // an antivirus scan or the indexer has had a moment to let go is thrown.
    public void Dispose() {
        for (int attempt = 1; ; attempt++) {
            try {
                Directory.Delete(Locations.DataDirectory, recursive: true);
                return;
            } catch (IOException) when (attempt < 5) {
                Thread.Sleep(100);
            }
        }
    }
}
