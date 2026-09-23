using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using FanControl.LianLi.Devices;
using Xunit;

namespace FanControl.LianLi.Tests.Devices;

/// <summary>
/// Reading one of L-Connect's saved documents: they are all gzipped UTF-8 JSON, and they are all
/// named by an MD5 of the key L-Connect used, so both live here rather than in each reader.
/// </summary>
public sealed class LConnectFileTests : IDisposable {
    private readonly string _dir;

    public LConnectFileTests() {
        _dir = Path.Combine(Path.GetTempPath(), "lianli-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() {
        if (Directory.Exists(_dir)) {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteGZip(string content, string name = "doc.0") {
        string path = Path.Combine(_dir, name);
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress)) {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    [Fact]
    public void ReadText_DecompressesTheDocument()
        => Assert.Equal("{\"a\":1}", LConnectFile.ReadText(WriteGZip("{\"a\":1}")));

    [Fact]
    public void Read_ParsesTheDecompressedJson() {
        JsonValue root = LConnectFile.Read(WriteGZip("{\"Type\":\"Pump\",\"Data\":{\"x\":2}}"));

        Assert.Equal("Pump", root.Member("Type")!.AsString());
        Assert.Equal(2, root.Member("Data")!.Member("x")!.AsInt());
    }

    [Fact]
    public void ReadText_RefusesADocumentThatExpandsBeyondTheCap() {
        // A few megabytes of zeros compress to almost nothing: the shape of a zip bomb, and the
        // reason the reader caps what it will expand (the plugin runs as SYSTEM).
        string path = Path.Combine(_dir, "bomb.0");
        using (FileStream file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionMode.Compress)) {
            var chunk = new byte[1024 * 1024];
            for (int i = 0; i < 9; i++) {
                gzip.Write(chunk, 0, chunk.Length);
            }
        }

        Assert.Throws<FormatException>(() => LConnectFile.ReadText(path));
    }

    [Fact]
    public void ReadText_APlainFileIsNotADocument()
        => Assert.ThrowsAny<Exception>(() => LConnectFile.ReadText(WritePlain("not gzipped")));

    private string WritePlain(string content) {
        string path = Path.Combine(_dir, "plain.0");
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    // L-Connect's own names, so the plugin finds the files it actually wrote.
    [InlineData("LWireless-Controller", "137b3f244d3c5d1568121556b5b076e5")]
    [InlineData("Pump", "cf82720db122ae41719df5b05503b749")]
    [InlineData("Fan", "50bd8c21bfafa6e4e962f6a948b1ef92")]
    [InlineData("Case", "cd14c323902024e72c850aa828d634a7")]
    public void HashName_MatchesLConnectsScheme(string key, string expected)
        => Assert.Equal(expected, LConnectFile.HashName(key));

    [Fact]
    public void HashName_IgnoresCase()
        => Assert.Equal(LConnectFile.HashName("PUMP"), LConnectFile.HashName("pump"));

    [Fact]
    public void DeviceSettingPath_IsTheHashedDirectoryAndFile() {
        string path = LConnectFile.DeviceSettingPath("/settings", "LWireless-Controller", "Pump");

        Assert.Equal(
            Path.Combine("/settings", "137b3f244d3c5d1568121556b5b076e5", "cf82720db122ae41719df5b05503b749.0"),
            path);
    }

    [Fact]
    public void NullArgumentsThrow() {
        Assert.Throws<ArgumentNullException>(() => LConnectFile.ReadText(null!));
        Assert.Throws<ArgumentNullException>(() => LConnectFile.HashName(null!));
        Assert.Throws<ArgumentNullException>(() => LConnectFile.DeviceSettingPath(null!, "a", "b"));
    }
}
