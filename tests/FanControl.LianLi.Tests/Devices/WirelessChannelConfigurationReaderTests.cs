using System;
using System.IO;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>RFController.GetChannel: &lt;master address&gt;.config, the channel as a number, 0 meaning none.</summary>
public sealed class WirelessChannelConfigReaderTests : IDisposable {
    private const string Master = "112233445566";
    private readonly string _dir;

    public WirelessChannelConfigReaderTests() {
        _dir = Path.Combine(Path.GetTempPath(), "lianli-wireless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() {
        if (Directory.Exists(_dir)) {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Save(string master, string text) => File.WriteAllText(Path.Combine(_dir, master + ".config"), text);

    [Fact]
    public void Read_ReturnsTheChannelSavedForThisMaster() {
        Save(Master, "11");

        Assert.Equal(11, WirelessChannelConfigurationReader.Read(_dir, Master));
    }

    // GetChannel looks up MasterMacAddrStr's own file, so another master's is never used.
    [Fact]
    public void Read_IgnoresAnotherMastersFile() {
        Save("aabbccddeeff", "11");

        Assert.Null(WirelessChannelConfigurationReader.Read(_dir, Master));
    }

    // SaveChannel writes StreamWriter.Write(byte): the decimal number; Convert.ToByte accepts
    // surrounding white space.
    [Theory]
    [InlineData("255", 255)]
    [InlineData(" 9\r\n", 9)]
    public void Read_ParsesTheNumberAsConvertToByteDoes(string text, int expected) {
        Save(Master, text);

        Assert.Equal(expected, WirelessChannelConfigurationReader.Read(_dir, Master));
    }

    // RFController.Init applies the channel only when GetChannel returns non-zero; an empty file is skipped.
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    public void Read_AnEmptyOrZeroFileIsNoChannel(string text) {
        Save(Master, text);

        Assert.Null(WirelessChannelConfigurationReader.Read(_dir, Master));
    }

    [Fact]
    public void Read_NoFileOrNoDirectoryIsNoChannel() {
        Assert.Null(WirelessChannelConfigurationReader.Read(_dir, Master));
        Assert.Null(WirelessChannelConfigurationReader.Read(Path.Combine(_dir, "absent"), Master));
    }

    // Convert.ToByte throws for text that is not a byte; GetChannel logs it and returns 0.
    [Fact]
    public void Read_ThrowsForAFileThatIsNotAByte() {
        Save(Master, "twelve");
        Assert.Throws<FormatException>(() => WirelessChannelConfigurationReader.Read(_dir, Master));

        Save(Master, "256");
        Assert.Throws<OverflowException>(() => WirelessChannelConfigurationReader.Read(_dir, Master));
    }

    [Fact]
    public void Read_RejectsMissingArguments() {
        Assert.Throws<ArgumentNullException>(() => WirelessChannelConfigurationReader.Read(null!, Master));
        Assert.Throws<ArgumentNullException>(() => WirelessChannelConfigurationReader.Read(_dir, null!));
    }
}
