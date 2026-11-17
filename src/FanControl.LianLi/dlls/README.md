# Vendored host-contract assemblies

`FanControl.Plugins.dll` (and its `FanControl.Plugins.xml` documentation) is the
FanControl host's plugin-contract assembly, vendored here as a compile-time
reference only. It is referenced with `Private=false`, so it is never copied into
the build output and never shipped in the release zip - the running FanControl
host supplies its own copy at load time. Shipping a second copy would break
plugin loading.

## Provenance

- Source: extracted from a FanControl install. FanControl is authored by Rem0o
  (https://github.com/Rem0o/FanControl.Releases).
- Assembly version: `1.0.0.0`. The plugin-contract assembly is versioned
  independently of, and changes far more slowly than, the FanControl application
  itself, so the application release is not recorded in the binary. The current
  copy was taken from FanControl V269.
- The plugin uses `IPlugin3` (the refresh request), which FanControl added in V243
  (released 9 October 2025), and `IPluginControlSensor2` (the fan sensor a control
  is paired with), which it added in V245 (released 18 October 2025), so V245 is
  the oldest FanControl it loads into.
- On V244 and older the host cannot resolve `IPluginControlSensor2` (and before
  V243, `IPlugin3` either), and its loader does not guard `Assembly.GetTypes()`:
  the failure escapes plugin loading and takes FanControl's other sensor sources
  down with it. That is why the minimum is stated at the top of the README.

## Refreshing

To update the reference, copy `FanControl.Plugins.dll` and
`FanControl.Plugins.xml` from the `FanControl` folder of a current install over
the files here. Do not depend on an API surface beyond what a released FanControl
can satisfy (see `.claude/rules/operational-awareness.md`); doing so makes the
plugin throw `TypeLoadException`/`MissingMethodException` inside the host.
