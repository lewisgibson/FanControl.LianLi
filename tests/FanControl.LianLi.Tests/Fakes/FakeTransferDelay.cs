using System.Collections.Generic;
using FanControl.LianLi.Transport;

namespace FanControl.LianLi.Tests.Fakes;

/// <summary>Records each pause a transport asks for instead of taking it.</summary>
internal sealed class FakeTransferDelay : ITransferDelay {
    public List<int> Waits { get; } = new List<int>();

    public void Wait(int milliseconds) => Waits.Add(milliseconds);
}
