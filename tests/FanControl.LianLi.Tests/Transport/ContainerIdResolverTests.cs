using System;
using System.Threading;
using FanControl.LianLi.Tests.Fakes;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// ContainerId resolution: interface path to device instance id to device node to the container GUID,
/// and every way that chain can come up empty, each of which must fall back to "unknown" rather than
/// a wrong id.
/// </summary>
public class ContainerIdResolverTests {
    private const string Path = @"\\?\hid#vid_0cf2&pid_a102&mi_00#7&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string Instance = @"HID\VID_0CF2&PID_A102&MI_00\7&1&0&0000";
    private const uint Node = 42;
    private static readonly Guid Container = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static FakeConfigurationManagerApi Resolvable() {
        var api = new FakeConfigurationManagerApi();
        api.InterfaceInstances[Path] = Instance;
        api.DeviceNodes[Instance] = Node;
        api.ContainerIds[Node] = Container.ToByteArray();
        return api;
    }

    [Fact]
    public void Resolve_FollowsTheChain_ToTheLowercasedBracedContainerId() {
        FakeConfigurationManagerApi api = Resolvable();

        Assert.Equal("{0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0}", new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
        // CM_LOCATE_DEVNODE_NORMAL: a present node only.
        Assert.Equal(new uint[] { 0 }, api.LocateFlags);
    }

    [Theory]
    [InlineData(FakeConfigurationManagerApi.CrBufferSmall)]
    [InlineData(FakeConfigurationManagerApi.CrSuccess)]
    public void Resolve_AcceptsEitherAnswerToASizingCall(int sizingResult) {
        FakeConfigurationManagerApi api = Resolvable();
        api.InterfacePropertySizingResult = sizingResult;
        api.DeviceNodePropertySizingResult = sizingResult;

        Assert.NotNull(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_UnknownInterface_IsNull()
        => Assert.Null(new ContainerIdResolver(Resolvable()).Resolve(@"\\?\hid#other", CancellationToken.None));

    [Fact]
    public void Resolve_InstanceIdSizingFails_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.InterfacePropertySizingResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_InstanceIdReadFails_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.InterfacePropertyReadResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_EmptyInstanceId_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.InterfaceInstances[Path] = string.Empty;

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_NoDeviceNode_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.DeviceNodes.Clear();

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_NoContainerProperty_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.ContainerIds.Clear();

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_EmptyContainerProperty_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.ContainerIds[Node] = Array.Empty<byte>();

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_ContainerPropertyReadFails_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.DeviceNodePropertyReadResult = FakeConfigurationManagerApi.CrFailure;

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_ContainerPropertyNotAGuid_IsNull() {
        FakeConfigurationManagerApi api = Resolvable();
        api.ContainerIds[Node] = new byte[] { 1, 2, 3, 4 };

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    [Fact]
    public void Resolve_AllZeroContainer_IsNull_SoSuchDevicesNeverCollapseTogether() {
        FakeConfigurationManagerApi api = Resolvable();
        api.ContainerIds[Node] = Guid.Empty.ToByteArray();

        Assert.Null(new ContainerIdResolver(api).Resolve(Path, CancellationToken.None));
    }

    // The five configuration-manager calls of a resolve, in order: the interface's instance id sized and
    // read, its node located, the node's ContainerId sized and read. A scan given up on while any one
    // of them is in flight stops there instead of making the next.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Resolve_GivenUpOnDuringACall_MakesNoFurtherCall(int blockedCall) {
        FakeConfigurationManagerApi api = Resolvable();
        using var abandonment = new CancellationTokenSource();
        int calls = 0;
        api.OnCall = _ => {
            if (++calls == blockedCall) {
                abandonment.Cancel();
            }
        };

        var resolver = new ContainerIdResolver(api);

        if (blockedCall == 5) {
            // The last call has nothing after it to skip; the resolve completes.
            Assert.NotNull(resolver.Resolve(Path, abandonment.Token));
        } else {
            Assert.Throws<OperationCanceledException>(() => resolver.Resolve(Path, abandonment.Token));
        }

        Assert.Equal(blockedCall, calls);
    }

    [Fact]
    public void Resolve_NullPath_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ContainerIdResolver(Resolvable()).Resolve(null!, CancellationToken.None));

    [Fact]
    public void Constructor_NullApi_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ContainerIdResolver(null!));
}
