# Target Framework

The plugin project targets `netstandard2.0`. This is not a default or a preference - it is the only target framework that works, and this document records why.

## The host ships as two different CLRs

FanControl is distributed in two parallel builds:

- A .NET Framework 4.8 build, running on the classic Windows-only CLR.
- A modern .NET build, currently .NET 8 and transitioning toward .NET 10, running on the cross-platform CoreCLR runtime.

A user may have either one installed. The plugin is a single DLL that the user drops into the host's `Plugins` folder, and that one DLL must load into whichever host the user is running. There is no way to ship "the right DLL per host" through the plugin-folder mechanism - it is one file, loaded by whatever CLR the host happens to be.

## Why the obvious targets fail

- **`net48`** cannot load on the modern .NET host. A .NET Framework 4.8 assembly is not loadable by CoreCLR.
- **`net8.0-windows` (or any `net*-windows`)** cannot load on the .NET Framework 4.8 host. The modern Windows-targeted assemblies are not loadable by the classic CLR.

So neither concrete-runtime target satisfies both hosts. Picking either one breaks the plugin for half the user base.

## Why netstandard2.0 works

`netstandard2.0` is the API contract that both runtimes implement. An assembly compiled against `netstandard2.0` loads into the .NET Framework 4.8 CLR and into modern CoreCLR alike, because both expose the netstandard2.0 surface. That is the point of the standard: one binary, both worlds.

This is also why the FanControl SDK contract assembly itself (`FanControl.Plugins.dll`) targets `netstandard2.0`. The host author made the same decision for the same reason - the reference assembly that plugins compile against has to be loadable into both host builds, so it too is netstandard2.0. The plugin follows the SDK's lead.

## Why the tests target net8.0

The constraint above is about the plugin DLL loading into the host process. The tests never load into FanControl - they run in their own test host, which we fully control. That host can be modern .NET, so the test project targets `net8.0`. Nothing in the tests needs to be loadable by the .NET Framework 4.8 CLR, so there is no reason to constrain them to netstandard2.0; targeting a concrete modern runtime gives the tests the full modern API and a faster, more capable test host.
