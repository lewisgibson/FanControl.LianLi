using System;
using System.Collections.Generic;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// Reading one interface class's present interfaces: the flag asked for, the size-then-fill pair
/// retried while the list grows, every failure reported with its step and code, and a read given up
/// on making no further call.
/// </summary>
public class DeviceInterfaceListReaderTests {
    private static readonly Guid Class = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

    private readonly FakeConfigurationManagerApi _api = new FakeConfigurationManagerApi();
    private readonly List<(string Step, int Code)> _failures = new List<(string Step, int Code)>();

    private IReadOnlyList<string>? Read(CancellationToken token)
        => DeviceInterfaceListReader.Read(_api, Class, null, (step, code) => _failures.Add((step, code)), token);

    [Fact]
    public void Read_ReturnsThePresentInterfacesOfTheClass() {
        _api.Interfaces[(Class, null)] = new[] { "a", "b" };

        Assert.Equal(new[] { "a", "b" }, Read(CancellationToken.None));
        Assert.Equal(Class + "  0x00000000", Assert.Single(_api.InterfaceListQueries));
        Assert.Empty(_failures);
    }

    [Fact]
    public void Read_AnEmptyList_IsEmpty() {
        _api.InterfaceListLengthOverride = 0;

        Assert.Empty(Read(CancellationToken.None)!);
        Assert.Empty(_failures);
    }

    [Fact]
    public void Read_AListThatGrewBetweenSizingAndFilling_IsSizedAgain() {
        _api.Interfaces[(Class, null)] = new[] { "a" };
        _api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);

        Assert.Equal(new[] { "a" }, Read(CancellationToken.None));
        Assert.Equal(2, _api.InterfaceListQueries.Count);
    }

    [Fact]
    public void Read_AListThatKeepsGrowing_GivesUp_AndSaysSo() {
        for (int i = 0; i < 3; i++) {
            _api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrBufferSmall);
        }

        Assert.Null(Read(CancellationToken.None));
        Assert.Equal(("listing the interfaces (it kept growing)", FakeConfigurationManagerApi.CrBufferSmall), Assert.Single(_failures));
    }

    [Fact]
    public void Read_ASizingFailure_IsReported() {
        _api.InterfaceListSizeResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Null(Read(CancellationToken.None));
        Assert.Equal(("sizing the interface list", FakeConfigurationManagerApi.CrFailure), Assert.Single(_failures));
    }

    [Fact]
    public void Read_AFillFailure_IsReported() {
        _api.InterfaceListResults.Enqueue(FakeConfigurationManagerApi.CrFailure);

        Assert.Null(Read(CancellationToken.None));
        Assert.Equal(("listing the interfaces", FakeConfigurationManagerApi.CrFailure), Assert.Single(_failures));
    }

    [Fact]
    public void Read_GivenUpOnWhileSizing_DoesNotFill() {
        using var abandonment = new CancellationTokenSource();
        var calls = new List<string>();
        _api.OnCall = name => {
            calls.Add(name);
            abandonment.Cancel();
        };

        Assert.Throws<OperationCanceledException>(() => Read(abandonment.Token));

        Assert.Equal(new[] { "GetDeviceInterfaceListSize" }, calls);
    }

    [Fact]
    public void Read_ValidatesItsArguments() {
        Assert.Throws<ArgumentNullException>(
            () => DeviceInterfaceListReader.Read(null!, Class, null, (_, _) => { }, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(
            () => DeviceInterfaceListReader.Read(_api, Class, null, null!, CancellationToken.None));
    }
}
