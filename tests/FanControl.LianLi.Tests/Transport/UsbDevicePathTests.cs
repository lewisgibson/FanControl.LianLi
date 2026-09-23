using System;
using System.Collections.Generic;
using FanControl.LianLi.Transport;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// The pure string handling behind the WinUSB locator: pulling the ids out of the two shapes of
/// Windows device identifier, and splitting the null-terminated lists the configuration manager and
/// the registry hand back.
/// </summary>
public class UsbDevicePathTests {
    [Theory]
    [InlineData(@"USB\VID_0416&PID_8040\6&1b2c3d4e&0&2", 0x0416, 0x8040)]
    [InlineData(@"\\?\usb#vid_0416&pid_8041#6&1b2c3d4e&0&2#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", 0x0416, 0x8041)]
    [InlineData(@"USB\VID_1a86&PID_e304\5&abcdef&0&1", 0x1A86, 0xE304)]
    public void TryParseIds_ReadsBothShapesOfIdentifier(string path, int expectedVendor, int expectedProduct) {
        Assert.True(UsbDevicePath.TryParseIds(path, out int vendorId, out int productId));
        Assert.Equal(expectedVendor, vendorId);
        Assert.Equal(expectedProduct, productId);
    }

    [Theory]
    [InlineData(@"USB\ROOT_HUB30\4&1a2b3c&0")]       // no ids at all
    [InlineData(@"USB\VID_0416\6&1b2c3d4e&0&2")]      // no product id
    [InlineData(@"USB\VID_zzzz&PID_8040\6&x&0&2")]    // not hex
    [InlineData("vid_")]                              // truncated
    public void TryParseIds_RejectsAnythingElse(string path)
        => Assert.False(UsbDevicePath.TryParseIds(path, out _, out _));

    [Fact]
    public void TryParseIds_NullPath_Throws()
        => Assert.Throws<ArgumentNullException>(() => UsbDevicePath.TryParseIds(null!, out _, out _));

    [Fact]
    public void SplitNullTerminatedList_ReadsEveryEntryAndStopsAtTheEmptyOne() {
        char[] buffer = "one\0two\0three\0\0trailing rubbish".ToCharArray();

        IReadOnlyList<string> entries = UsbDevicePath.SplitNullTerminatedList(buffer, buffer.Length);

        Assert.Equal(new[] { "one", "two", "three" }, entries);
    }

    [Fact]
    public void SplitNullTerminatedList_HonoursTheLengthOverTheBuffer() {
        char[] buffer = new char[64];
        "first\0second\0\0".CopyTo(0, buffer, 0, 14);

        Assert.Equal(new[] { "first", "second" }, UsbDevicePath.SplitNullTerminatedList(buffer, 14));
        // A length that stops mid-list yields only what it covers.
        Assert.Equal(new[] { "first" }, UsbDevicePath.SplitNullTerminatedList(buffer, 6));
    }

    [Fact]
    public void SplitNullTerminatedList_UnterminatedBufferStillYieldsItsContent()
        => Assert.Equal(new[] { "only" }, UsbDevicePath.SplitNullTerminatedList("only".ToCharArray(), 4));

    [Fact]
    public void SplitNullTerminatedList_EmptyBufferYieldsNothing() {
        Assert.Empty(UsbDevicePath.SplitNullTerminatedList(Array.Empty<char>(), 0));
        Assert.Empty(UsbDevicePath.SplitNullTerminatedList(new char[4], 4));
    }

    [Fact]
    public void SplitNullTerminatedList_NullBuffer_Throws()
        => Assert.Throws<ArgumentNullException>(() => UsbDevicePath.SplitNullTerminatedList(null!, 0));
}
