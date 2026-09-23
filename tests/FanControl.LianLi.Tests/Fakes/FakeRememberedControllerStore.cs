using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Plugin;

namespace FanControl.LianLi.Tests.Fakes;

// Keeps what is saved in memory, and hands back whatever a test put in it.
internal sealed class FakeRememberedControllerStore : IRememberedControllerStore {
    public List<StoredController> Stored { get; } = new List<StoredController>();

    public int Saves { get; private set; }

    /// <summary>How many loads report the store unreadable before one succeeds.</summary>
    public int UnreadableLoads { get; set; }

    public IReadOnlyList<StoredController>? Load(ILog log) {
        if (UnreadableLoads > 0) {
            UnreadableLoads--;
            return null;
        }

        return Stored.ToArray();
    }

    /// <summary>Run inside each save, e.g. to hold it while another thread tries to save.</summary>
    public Action? DuringSave { get; set; }

    /// <summary>The most saves ever running at once.</summary>
    public int MostAtOnce { get; private set; }

    private int _running;

    public void Save(IReadOnlyList<StoredController> controllers, ILog log) {
        MostAtOnce = Math.Max(MostAtOnce, Interlocked.Increment(ref _running));
        DuringSave?.Invoke();
        Interlocked.Decrement(ref _running);
        Saves++;
        Stored.Clear();
        Stored.AddRange(controllers);
    }
}
