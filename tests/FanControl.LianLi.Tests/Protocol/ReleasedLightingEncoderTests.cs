#if ENABLE_LIGHTING
using System.Collections.Generic;
using System.Linq;
using FanControl.LianLi.Protocol;
using Xunit;

namespace FanControl.LianLi.Tests.Protocol;

/// <summary>
/// The lighting encoders against their released v1.1.34 sources, kept verbatim under
/// <c>Released/</c>: a user of that release gets every transfer they got before, the same bytes and
/// in order, with the merge-order register appended where the family gained it since.
/// </summary>
public sealed class ReleasedLightingEncoderTests
{
    private static readonly int[] Quantity = { 4, 4, 4, 4 };
    private static readonly byte[] MergeOrder = { 0xE0, 0x10, 0x63, 0, 1, 2, 3, 8 };

    // Three SL-Infinity controllers with port 0 saved as Lottery_Inner (46) and the odd ports as
    // Lottery_Outer (79), FanQuantity [4,4,4,4] and MergeOrder [0,1,2,3].
    [Fact]
    public void ThreeSlInfinityLotteryLooks_KeepEveryReleasedByte_ThenAppendTheMergeOrder()
    {
        for (int controller = 0; controller < 3; controller++)
        {
            LightingPortState[] ports = Enumerable.Range(0, 8).Select(port => Port(port, port % 2 == 0 ? 46 : 79, controller)).ToArray();

            Compare(
                ReleasedSlInfinityLightingEncoder.Encode(ports, Quantity),
                SlInfinityLightingEncoder.Encode(ports, Quantity, mergeOrder: new[] { 0, 1, 2, 3 }),
                MergeOrder);
        }
    }

    [Fact]
    public void EverySlInfinityNonMergeMode_KeepsEveryReleasedByte_ThenAppendsTheMergeOrder()
    {
        for (int mode = 0; mode <= 100; mode++)
        {
            LightingPortState[] ports = Enumerable.Range(0, 8).Select(port => Port(port, mode, 0)).ToArray();

            Compare(ReleasedSlInfinityLightingEncoder.Encode(ports, Quantity), SlInfinityLightingEncoder.Encode(ports, Quantity), MergeOrder);
        }
    }

    [Theory]
    [InlineData("Sl")]
    [InlineData("Al")]
    [InlineData("SlV2")]
    [InlineData("AlV2")]
    public void EveryUniNonMergeMode_KeepsEveryReleasedByte(string family)
    {
        (UniFanLightingProfile current, ReleasedUniFanLightingProfile released) = family switch
        {
            "Sl" => (UniFanLightingProfiles.Sl, ReleasedUniFanLightingProfiles.Sl),
            "Al" => (UniFanLightingProfiles.Al, ReleasedUniFanLightingProfiles.Al),
            "SlV2" => (UniFanLightingProfiles.SlV2, ReleasedUniFanLightingProfiles.SlV2),
            _ => (UniFanLightingProfiles.AlV2, ReleasedUniFanLightingProfiles.AlV2),
        };
        for (int mode = 0; mode <= 140; mode++)
        {
            if (current.MergeModes.Contains(mode))
            {
                continue;
            }

            LightingPortState[] ports = Enumerable.Range(0, family == "Sl" ? 4 : 8).Select(port => Port(port, mode, 0)).ToArray();

            Compare(
                ReleasedUniFanLightingEncoder.Encode(released, ports, Quantity),
                UniFanLightingEncoder.Encode(current, ports, Quantity),
                current.MergeOrderRegister.HasValue ? MergeOrder : null);
        }
    }

    [Fact]
    public void EveryStrimerMode_KeepsEveryReleasedByte()
    {
        for (int mode = 0; mode <= 100; mode++)
        {
            LightingPortState[] ports = Enumerable.Range(0, 12).Select(port => Port(port, mode, 0)).ToArray();

            Compare(ReleasedStrimerPlusLightingEncoder.Encode(ports), StrimerPlusLightingEncoder.Encode(ports), null);
        }
    }

    [Fact]
    public void EveryGalahadTrinityMode_KeepsEveryReleasedByte()
    {
        var colours = new[] { new RgbColor(17, 39, 239), new RgbColor(223, 5, 113) };
        for (int scope = 0; scope < 3; scope++)
        {
            for (int mode = 0; mode < 20; mode++)
            {
                var fan = new Galahad2FanLightingState(mode, 2, 1, 3, 24, true, colours);
                var pump = new Galahad2PumpLightingState(scope, (1000 * scope) + mode, 2, 1, 3, colours);

                Compare(ReleasedGalahad2LightingEncoder.Encode(fan, pump), Galahad2LightingEncoder.Encode(fan, new[] { pump }), null);
            }
        }
    }

    private static LightingPortState Port(int port, int mode, int seed) => new LightingPortState(
        port, mode, 1, 0, 0,
        new[] { new RgbColor((byte)(7 + seed), 13, 19), new RgbColor(37, 101, 229), new RgbColor(211, 23, 79), new RgbColor(11, 31, 53) });

    // Every released transfer first, unchanged, then the suffix the family gained and nothing more.
    private static void Compare(IReadOnlyList<LightingTransfer> released, IReadOnlyList<LightingTransfer> current, byte[]? suffix)
    {
        Assert.Equal(released.Count + (suffix is null ? 0 : 1), current.Count);
        for (int transfer = 0; transfer < released.Count; transfer++)
        {
            Assert.Equal(released[transfer].IsFeature, current[transfer].IsFeature);
            Assert.Equal(released[transfer].Report, current[transfer].Report);
        }

        if (suffix != null)
        {
            Assert.True(current[^1].IsFeature);
            Assert.Equal(suffix, current[^1].Report);
        }
    }
}
#endif
