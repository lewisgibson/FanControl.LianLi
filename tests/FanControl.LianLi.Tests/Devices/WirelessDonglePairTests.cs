using System;
using System.IO;
using System.Linq;
using FanControl.LianLi.Devices;
using FanControl.LianLi.Tests.Fakes;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>WinUsb.RfSend's and RfRead's failure counts and the cross-wired reset (MasterDevice.ResetTx / ResetRx).</summary>
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
        Assert.False(pair.IsFaulted);
        Assert.False(pair.IsTransmitterFaulted);
        receiver.IsFaulted = true;
        Assert.True(pair.IsFaulted);
        Assert.False(pair.IsTransmitterFaulted);
        receiver.IsFaulted = false;
        transmitter.IsFaulted = true;
        Assert.True(pair.IsFaulted);
        Assert.True(pair.IsTransmitterFaulted);
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

    // WinUsb.RfRead: a reply whose first byte is zero counts (iReadErr++); at five in a
    // row the dongle read from is reset through its partner (ResetRx) and the count starts again.
    [Fact]
    public void FiveEmptyReceiverReads_ResetItThroughTheTransmitter() {
        var logger = new FakeLogger();
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 2, logger);

        for (int i = 0; i < 4; i++) {
            Assert.Equal(new byte[3], pair.ReadReceiver(3));
        }

        Assert.Empty(transmitter.Writes);
        Assert.Equal(new byte[3], pair.ReadReceiver(3));

        Assert.True(IsReset(Assert.Single(transmitter.Writes)));
        Assert.Contains(logger.Messages, m => m == "W2: receiver read nothing for the 5th time in a row across both dongles; reset it through the transmitter");

        for (int i = 0; i < 4; i++) {
            pair.ReadReceiver(3);
        }

        Assert.Single(transmitter.Writes);
    }

    // A reply of no bytes at all is as empty as one of zeros.
    [Fact]
    public void FiveEmptyTransmitterReads_ResetItThroughTheReceiver() {
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        for (int i = 0; i < 3; i++) {
            pair.ReadTransmitter(64);
        }

        Assert.Empty(pair.ReadTransmitter(0));
        Assert.Empty(receiver.Writes);
        Assert.Empty(pair.ReadTransmitter(0));

        Assert.True(IsReset(Assert.Single(receiver.Writes)));
    }

    // iReadErr = 0 on a reply that carries anything; a read that throws counts like an empty one,
    // since ReadAll hands back the zeros it got, and still throws to the caller.
    [Fact]
    public void ANonEmptyRead_StartsTheCountAgain_AndAFailedReadCounts() {
        bool answer = false;
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle { Responder = _ => answer ? new byte[] { 0x10 } : null };
        using var pair = new WirelessDonglePair(transmitter, receiver, 0, new FakeLogger());

        for (int i = 0; i < 4; i++) {
            pair.ReadReceiver(3);
        }

        answer = true;
        Assert.Equal(new byte[] { 0x10, 0, 0 }, pair.ReadReceiver(3));
        answer = false;
        for (int i = 0; i < 2; i++) {
            pair.ReadReceiver(3);
        }

        receiver.ReadFailure = new IOException("simulated read failure");
        Assert.Throws<IOException>(() => pair.ReadReceiver(3));
        Assert.Throws<IOException>(() => pair.ReadReceiver(3));
        Assert.Empty(transmitter.Writes);
        Assert.Throws<IOException>(() => pair.ReadReceiver(3));
        Assert.True(IsReset(Assert.Single(transmitter.Writes)));
    }

    // Reads and writes are counted apart (iReadErr and iSendErr), and the reset is a write to the
    // partner, counted against the partner like any other.
    [Fact]
    public void ReadsAndWrites_AreCountedApart_AndAResetCountsAgainstThePartner() {
        var logger = new FakeLogger();
        var transmitter = new FakeWirelessDongle { FailWrite = _ => true };
        var receiver = new FakeWirelessDongle { FailWrite = _ => true };
        using var pair = new WirelessDonglePair(transmitter, receiver, 1, logger);

        for (int i = 0; i < 3; i++) {
            pair.ReadReceiver(3);
            Assert.Throws<IOException>(() => pair.WriteReceiver(Packet));
        }

        Assert.Empty(transmitter.Writes);
        Assert.Empty(logger.Messages);

        for (int i = 0; i < 2; i++) {
            pair.ReadReceiver(3);
        }

        Assert.Contains(logger.Messages, m => m == "W1: receiver read nothing for the 5th time in a row across both dongles; resetting it through the transmitter failed too: simulated write failure");
    }

    // iReadErr is one static count in L-Connect's WinUsb, shared by both dongles: empty reads from
    // either add to it, and the fifth resets whichever dongle it was read from.
    [Fact]
    public void EmptyReads_AreCountedAcrossBothDongles_AndResetTheOneReadFifth() {
        var logger = new FakeLogger();
        var transmitter = new FakeWirelessDongle();
        var receiver = new FakeWirelessDongle();
        using var pair = new WirelessDonglePair(transmitter, receiver, 3, logger);

        pair.ReadTransmitter(64);
        pair.ReadTransmitter(64);
        pair.ReadReceiver(64);
        pair.ReadReceiver(64);
        Assert.Empty(transmitter.Writes);
        Assert.Empty(receiver.Writes);

        pair.ReadTransmitter(64);

        Assert.True(IsReset(Assert.Single(receiver.Writes)));
        Assert.Empty(transmitter.Writes);
        Assert.Contains(logger.Messages, m => m == "W3: transmitter read nothing for the 5th time in a row across both dongles; reset it through the receiver");
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
