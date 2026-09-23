# Contributing

Thanks for your interest in improving FanControl.LianLi. This is a small project; issues and pull requests are welcome.

## Prerequisites

- The .NET 9 SDK on Windows (the plugin targets `netstandard2.0`; the test project targets `net8.0`).
- A real Lian Li Uni controller is needed to verify hardware behavior, but the build, tests, and most logic can be developed without one (the HID layer is behind an interface that the tests fake).

## Build and test

| Command                                          | What it does                                            |
| ------------------------------------------------ | ------------------------------------------------------- |
| `dotnet restore`                                 | Restore NuGet packages                                  |
| `dotnet format`                                  | Auto-format (use `--verify-no-changes` to check)        |
| `dotnet build -c Release`                        | Build the plugin (analyzer-clean, warnings are errors)  |
| `dotnet test -c Release`                         | Run the unit tests                                      |
| `dotnet test -c Release -p:CollectCoverage=true` | Run them and fail below full line and branch coverage   |
| `./build.ps1`                                    | Run the whole gate: restore, format-verify, build, test |

`./build.ps1` mirrors what CI runs (it builds and tests all three variants - standard, ARGB, and Lighting); run it before opening a PR. To target a single variant yourself, pass the matching flag: `-p:EnableArgb=true` for ARGB or `-p:EnableLighting=true` for Lighting (for example `dotnet test -c Release -p:EnableLighting=true`). The two flags are mutually exclusive - never combine them.

Every line and every branch of the plugin that a variant compiles has to run under that variant's tests; CI and `./build.ps1` fail the build otherwise, and the report lands in `coverage/coverage.cobertura.xml`. The only exemption is `[ExcludeFromCodeCoverage]`, which is reserved for the P/Invoke seams that need a real Windows device; everything they decide is pulled out into plain code so it can be tested.

## Project layout

```
src/FanControl.LianLi/      The plugin (netstandard2.0). Builds the DLL FanControl loads.
tests/FanControl.LianLi.Tests/  xUnit tests (net8.0).
docs/                       Architecture, device protocol, deployment notes.
```

See [docs/architecture.md](docs/architecture.md) for the layering, and [docs/protocol.md](docs/protocol.md) for the byte-level device contract. The detailed internal coding standards live under [.claude/rules/](.claude/rules/).

## Coding standards (the load-bearing ones)

- **netstandard2.0 only in the plugin.** The DLL must load into FanControl's runtime, so do not use APIs unavailable on `netstandard2.0`, even if the SDK offers them. Test code (net8.0) may use newer APIs.
- **One public type.** Only the `IPlugin3` implementation (`LianLiPlugin`) is `public`; everything else is `internal`. Tests see internals via `InternalsVisibleTo`.
- **Native USB calls stay behind the seam.** Every `hid.dll`, `winusb.dll`, `cfgmgr32.dll` and `kernel32.dll` call lives in one of `Transport/`'s thin adapters (`WindowsHidApi`, `WindowsWinUsbApi`, `WindowsConfigurationManagerApi`, `WindowsThreadCanceller`), behind an interface the transports are tested through; the rest of the code knows only `IDeviceTransport` and byte buffers. The plugin does not use HidSharp, the host's shared HID library (see `docs/architecture.md`).
- **Protocol encoders are pure.** Device state in, exact byte buffer out - no I/O and no clock. Any change to an encoder must be covered by a test that asserts the exact bytes, byte for byte.
- **Analyzer-clean.** The build treats warnings as errors and requires XML docs on public members. Keep it green; justify any suppression inline.

## Pull requests

1. Fork and create a branch.
2. Make your change with tests, and run `./build.ps1` until it is green.
3. Open a PR. The template has a short checklist; CI runs build, lint, and a security scan on every PR.

By contributing, you agree your contributions are licensed under the project's [MIT license](LICENSE).
