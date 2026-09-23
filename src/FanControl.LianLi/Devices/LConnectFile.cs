using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FanControl.LianLi.Devices;

/// <summary>
/// Reads one of L-Connect's saved documents. Everything L-Connect writes under its ProgramData
/// directory - the per-controller lighting configs, the start/stop profiles, the wireless device
/// settings - is gzipped UTF-8 JSON, so the decompress-and-parse step lives here once rather than
/// in each reader. The decompressed size is capped: these documents are kilobytes, and a file that
/// claims otherwise is either corrupt or hostile, and either way is not worth expanding.
/// </summary>
internal static class LConnectFile {
    // Generous next to any real L-Connect document and small enough that a zip bomb cannot hurt.
    private const int MaxDecompressedBytes = 8 * 1024 * 1024;

    /// <summary>Parse a gzipped JSON document. Throws <see cref="FormatException"/> on a file that is neither.</summary>
    public static JsonValue Read(string path) => JsonValue.Parse(ReadText(path));

    /// <summary>The decompressed text of a gzipped document, capped at a size no real one reaches.</summary>
    public static string ReadText(string path) {
        if (path is null) {
            throw new ArgumentNullException(nameof(path));
        }

        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();

        byte[] chunk = new byte[8192];
        int read;
        while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0) {
            if (buffer.Length + read > MaxDecompressedBytes) {
                throw new FormatException(string.Format(
                    CultureInfo.InvariantCulture,
                    "L-Connect file {0} decompresses to more than {1} bytes.",
                    Path.GetFileName(path),
                    MaxDecompressedBytes));
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The lowercase hex MD5 of a string, which is how L-Connect names the directories and files
    /// under its <c>device</c> folder and its start/stop profiles. This reproduces L-Connect's own
    /// naming scheme, not a security context, so MD5 is the required algorithm.
    /// </summary>
#pragma warning disable CA5351 // MD5 is used to match L-Connect's filenames, not for security
    public static string HashName(string value) {
        if (value is null) {
            throw new ArgumentNullException(nameof(value));
        }

        using MD5 md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        var name = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) {
            name.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return name.ToString();
    }
#pragma warning restore CA5351

    /// <summary>
    /// The path of one of L-Connect's device settings: a directory named for the device key and a
    /// file named for the setting key, both hashed, with L-Connect's fixed <c>0</c> extension.
    /// </summary>
    public static string DeviceSettingPath(string deviceSettingDirectory, string deviceKey, string settingKey) {
        if (deviceSettingDirectory is null) {
            throw new ArgumentNullException(nameof(deviceSettingDirectory));
        }

        return Path.Combine(deviceSettingDirectory, HashName(deviceKey), HashName(settingKey) + ".0");
    }
}
