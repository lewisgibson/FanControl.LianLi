using System;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>WinUsb.RfSend's failure count and the cross-wired reset (MasterDevice.ResetTx / ResetRx).</summary>
public sealed class WirelessDonglePairTests {
    private static readonly byte[] Packet = { 0x42 };

    private static bool IsReset(byte[] packet) => packet.Length == 64 && packet[0] == 0x15 && packet.Skip(1).All(b => b == 0);

    [Fact]
    public void Writes_GoToTheirOwnDongle() {
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        pair.WriteTransmitter(new byte[] { 1 });
        pair.WriteReceiver(new byte[] { 2 });

        Assert.Equal(new byte[] { 1 }, Assert.Single(transmitter.Writes));
        Assert.Equal(new byte[] { 2 }, Assert.Single(receiver.Writes));
    }

    [Fact]
    public void Reads_AndGenerations_ComeFromTheirOwnDongle() {
        var transmitter = new FakeWirelessDongle { Generation = 3, Responder = _ => new byte[] { 7 } };
        var receiver = new FakeWirelessDongle { Generation = 5, Responder = _ => new byte[] { 9 } };
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        Assert.Equal(new byte[] { 7, 0 }, pair.ReadTransmitter(2));
        Assert.Equal(new byte[] { 9, 0, 0 }, pair.ReadReceiver(3));
        Assert.Equal(3, pair.TransmitterGeneration);
        Assert.Equal(5, pair.ReceiverGeneration);
    }

    // WinUsb.RfSend: five failed sends in a row on the transmitter call RFController.ResetTx, which
    // sends 0x15 through the receiver (MasterDevice.ResetTx uses RFReceiver).
    [Fact]
    public void FiveFailedTransmitterWrites_ResetItThroughTheReceiver() {
        var logger = new FakeLogger();
        var transmitter = new FakeWirelessDongle { FailWrite = _ => true };
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 2, logger);

        for (int i = 0; i < 4; i++) {
            Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));
        }

        Assert.Empty(receiver.Writes);
        Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));

        Assert.True(IsReset(Assert.Single(receiver.Writes)));
        Assert.Contains(logger.Messages, m => m == "W2: transmitter failed 5 writes in a row; reset it through the receiver");

        // The count starts again after the reset.
        for (int i = 0; i < 4; i++) {
            Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));
        }

        Assert.Single(receiver.Writes);
    }

    // MasterDevice.ResetRx sends 0x15 through RFSender.
    [Fact]
    public void FiveFailedReceiverWrites_ResetItThroughTheTransmitter() {
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle { FailWrite = _ => true };
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        for (int i = 0; i < 5; i++) {
            Assert.Throws<IOException>(() => pair.WriteReceiver(Packet));
        }

        Assert.True(IsReset(Assert.Single(transmitter.Writes)));
    }

    // iSendErr = 0 on a successful send.
    [Fact]
    public void ASuccessfulWrite_StartsTheCountAgain() {
        bool fail = true;
        var transmitter = new FakeWirelessDongle { FailWrite = _ => fail };
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        for (int i = 0; i < 4; i++) {
            Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));
        }

        fail = false;
        pair.WriteTransmitter(Packet);
        fail = true;
        for (int i = 0; i < 4; i++) {
            Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));
        }

        Assert.Empty(receiver.Writes);
    }

    [Fact]
    public void AResetThatFailsToo_IsLoggedAndTheOriginalFailureStillThrows() {
        var logger = new FakeLogger();
        var transmitter = new FakeWirelessDongle { FailWrite = _ => true };
        var receiver = new FakeWirelessDongle { FailWrite = _ => true };
        using var pair = new WirelessDonglePair(transmitter, receiver, 1, logger);

        for (int i = 0; i < 4; i++) {
            Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));
        }

        IOException thrown = Assert.Throws<IOException>(() => pair.WriteTransmitter(Packet));

        Assert.Equal("simulated write failure", thrown.Message);
        Assert.Contains(logger.Messages, m => m == "W1: transmitter failed 5 writes in a row; resetting it through the receiver failed too: simulated write failure");
    }

    [Fact]
    public void Dispose_ReleasesBothDonglesOnce() {
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle();
        var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        pair.Dispose();
        pair.Dispose();

        Assert.Equal(1, transmitter.DisposeCount);
        Assert.Equal(1, receiver.DisposeCount);
    }

    [Fact]
    public void Constructor_RejectsMissingDependencies() {
        var dongle = new FakeWirelessDongle();

        Assert.Throws<ArgumentNullException>(() => new WirelessDonglePair(null!, dongle, 0, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new WirelessDonglePair(dongle, null!, 0, new FakeLogger()));
        Assert.Throws<ArgumentNullException>(() => new WirelessDonglePair(dongle, dongle, 0, null!));
    }
}
