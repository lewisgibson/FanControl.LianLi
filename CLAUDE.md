# CLAUDE.md

Guidance for AI agents (and humans) working in this repository.

## What this is

A `netstandard2.0` FanControl plugin that drives Lian Li Uni fan controllers. It compiles to a single DLL that FanControl loads from its `Plugins` folder. The `netstandard2.0` target is a hard constraint: the same DLL must load into both the .NET Framework and the modern .NET host the FanControl process may use. The only public type is the `IPlugin3` implementation the host reflects over; everything else is `internal`. The test project targets `net8.0`. See `docs/tfm.md` for the rationale and `docs/architecture.md` for the layering.

## Build and test

| Command                                          | Description                                          |
| ------------------------------------------------ | ---------------------------------------------------- |
| `dotnet restore`                                 | Restore NuGet packages                               |
| `dotnet format --verify-no-changes`              | Verify formatting (CI gate); `dotnet format` to fix  |
| `dotnet build -c Release`                        | Build the plugin in Release (warnings are errors)    |
| `dotnet test -c Release`                         | Run the test suite                                   |
| `dotnet test -c Release -p:CollectCoverage=true` | Run it and fail below 100% line and branch coverage  |
| `./build.ps1`                                    | Full local gate: restore, format-verify, build, test |

CI (`.github/workflows/`) runs the same steps on `windows-latest`.

## Conventions

The binding engineering standards live in `.claude/rules/` (architecture seams, the device protocol, the review checklist) and the contributor guide is `CONTRIBUTING.md`. Read those before making changes. Key invariants: one public type, native USB calls confined to `Transport/`'s adapters (no HidSharp), pure protocol encoders covered by byte-level tests, keepalive driven by an injected clock, and the `netstandard2.0` API limit.

## Deployment

Ship only `FanControl.LianLi.dll` (or `FanControl.LianLi.Argb.dll` for the ARGB variant); it has no dependencies to ship beside it. Users install it with FanControl's in-app **Install plugin** button, which loads the plugin immediately - no restart of FanControl is needed. FanControl runs plugins in its background service (`FanControl.Service`, as LocalSystem), so the plugin runs as SYSTEM and its file log lands under the SYSTEM profile. See `docs/deployment.md`.
