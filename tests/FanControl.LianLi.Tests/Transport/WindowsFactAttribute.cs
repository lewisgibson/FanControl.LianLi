using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace FanControl.LianLi.Tests.Transport;

/// <summary>
/// A fact that calls the real Windows APIs behind the native adapters, so it runs only on Windows (CI
/// builds on windows-latest) and is skipped everywhere else, where those APIs do not exist.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class WindowsFactAttribute : FactAttribute {
    public WindowsFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) {
        if (!OperatingSystem.IsWindows()) {
            Skip = "Calls the real Windows APIs, which exist only on Windows.";
        }
    }
}
