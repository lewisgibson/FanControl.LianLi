using System;
using System.IO;
using FanControl.LianLi.Logging;
using FanControl.LianLi.Tests.Fakes;
using FanControl.Plugins;
using Xunit;

namespace FanControl.LianLi.Tests.Logging;

public class LoggingTests {
    [Fact]
    public void CompositeLog_FansOutToEverySink() {
        var a = new FakeLogger();
        var b = new FakeLogger();
        var composite = new CompositeLog(a, b);

        composite.Write("hello");

        Assert.Equal(new[] { "hello" }, a.Messages);
        Assert.Equal(new[] { "hello" }, b.Messages);
    }

    [Fact]
    public void CompositeLog_ToleratesNullSink() {
        var composite = new CompositeLog(new ILog[] { null! });
        composite.Write("no throw"); // the null sink is skipped via null-conditional
    }

    [Fact]
    public void FileLogger_DefaultsToThePluginsLocalAppDataFolder() {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl.LianLi", "plugin.log");

        Assert.Equal(expected, new FileLogger().FilePath);
    }

    [Fact]
    public void FileLogger_AppendsTimestampedLines() {
        string dir = Path.Combine(Path.GetTempPath(), "lianli-log-" + Guid.NewGuid().ToString("N"));
        try {
            var logger = new FileLogger(dir);

            logger.Write("first");
            logger.Write("second");

            Assert.Equal(Path.Combine(dir, "plugin.log"), logger.FilePath);
            string[] lines = File.ReadAllLines(logger.FilePath);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("  first", lines[0]);
            Assert.EndsWith("  second", lines[1]);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  ", lines[0]);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileLogger_FallsBackToTemp_WhenItsFolderCannotBeCreated() {
        // A folder cannot be created beneath a file.
        string file = Path.GetTempFileName();
        try {
            var logger = new FileLogger(Path.Combine(file, "FanControl.LianLi"));

            Assert.Equal(Path.Combine(Path.GetTempPath(), "plugin.log"), logger.FilePath);
        } finally {
            File.Delete(file);
        }
    }

    [Fact]
    public void FileLogger_WriteFailure_NeverThrows() {
        // The log "file" is a directory, so every append fails; the logger must swallow it.
        string dir = Path.Combine(Path.GetTempPath(), "lianli-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "plugin.log"));
        try {
            var logger = new FileLogger(dir);

            logger.Write("lost");

            Assert.True(Directory.Exists(logger.FilePath));
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileLogger_RotatesOnceFull_KeepingOnePreviousFile() {
        string dir = Path.Combine(Path.GetTempPath(), "lianli-log-" + Guid.NewGuid().ToString("N"));
        try {
            var logger = new FileLogger(dir, maximumBytes: 64);

            logger.Write(new string('a', 80)); // passes the limit
            logger.Write("second");            // rotates, then starts afresh
            logger.Write(new string('b', 80));
            logger.Write("fourth");            // rotates again, replacing the first previous file

            Assert.EndsWith("  fourth", Assert.Single(File.ReadAllLines(logger.FilePath)));
            string[] previous = File.ReadAllLines(logger.PreviousFilePath);
            Assert.Equal(2, previous.Length);
            Assert.EndsWith("  second", previous[0]);
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A rotation that fails costs no line: the current file keeps growing, the failure is noted
    // once, and a later rotation that succeeds carries on as usual.
    [Fact]
    public void FileLogger_ARotationThatFails_KeepsEveryLine_AndNotesItOnce() {
        string dir = Path.Combine(Path.GetTempPath(), "lianli-log-" + Guid.NewGuid().ToString("N"));
        try {
            var logger = new FileLogger(dir, maximumBytes: 64);
            logger.Write(new string('a', 80));
            Directory.CreateDirectory(logger.PreviousFilePath); // nothing can be moved there

            logger.Write("second");
            logger.Write("third");

            string[] lines = File.ReadAllLines(logger.FilePath);
            Assert.Contains(lines, l => l.EndsWith("  second", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith("  third", StringComparison.Ordinal));
            Assert.Single(lines, l => l.Contains("log rotation failed, this file keeps growing until it succeeds"));

            Directory.Delete(logger.PreviousFilePath);
            logger.Write("fourth");
            Assert.EndsWith("  fourth", Assert.Single(File.ReadAllLines(logger.FilePath)));
        } finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileLogger_RejectsANonPositiveLimit()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FileLogger(Path.GetTempPath(), 0));

    [Fact]
    public void CompositeLog_WithNoSinkArray_WritesNowhere()
        => new CompositeLog(null!).Write("dropped");
}

