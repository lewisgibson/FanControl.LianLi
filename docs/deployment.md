# Deployment

How the built plugin actually reaches a running FanControl, and the non-obvious things that bite if you skip them.

## What you ship: one DLL (per variant)

The deployable artifact is a single file: `FanControl.LianLi.dll` (standard), `FanControl.LianLi.Argb.dll` (the ARGB variant - see [protocol.md](protocol.md)), or `FanControl.LianLi.Lighting.dll` (the Lighting variant - see [lighting.md](lighting.md)). A user installs one of the three, never more than one. You do NOT ship anything else:

- `HidSharp.dll` - FanControl already ships HidSharp in its own install folder (it uses HidSharp for its built-in HID controllers). HidSharp is not strong-named, so the plugin binds by simple name to whatever 2.6.x the host has loaded. Shipping a second copy is pointless (the host's already-loaded one wins) and just clutters the Plugins folder.
- `FanControl.Plugins.dll` - the host contract assembly, always provided by the host. Never ship it.

The **Lighting** variant needs no companion file either: it reads L-Connect's own saved configuration directly from `C:\ProgramData\Lian-Li\L-Connect 3` (read-only) and re-applies the look. With no L-Connect configuration present it behaves exactly like the standard build. See [lighting.md](lighting.md).

Install: use FanControl's **Install plugin** button, or copy the DLL into `<FanControl>\Plugins\`.

## Plugins are loaded by the FanControl service

Modern FanControl (4.x) splits hardware access into a background Windows service, `FanControl.Service` (running as `LocalSystem`). **Plugins are loaded and run by that service, not by `FanControl.exe`.** Consequences:

- Load a plugin with FanControl's **Install plugin** button: it loads the chosen DLL immediately, with no restart of FanControl. (The service also scans `Plugins\*.dll` - files whose name starts with `FanControl` - at its own start, but you do not need to restart anything to add a plugin; use the button.)
- The plugin runs as `LocalSystem`. Anything path-relative (for example the file logger, which writes to `%LOCALAPPDATA%\FanControl.LianLi\plugin.log`) resolves under the SYSTEM profile (`C:\Windows\System32\config\systemprofile\AppData\Local\...`), not your user profile. Look there when diagnosing.
- FanControl's own error log (`<FanControl>\service_log.txt`) is written by the service (SYSTEM), so it captures plugin load/Initialize failures. The main app's `log.txt` lives in the same Program Files folder and is not writable by a non-elevated `FanControl.exe`, so do not rely on it.

## Updating the plugin file

Re-installing through the **Install plugin** button is the normal way to update a plugin - no restart needed. If you are iterating during development and replacing the DLL on disk directly, note that the service holds the loaded DLL locked while it is in use, so overwriting it can fail; close FanControl to free the lock (or stop `FanControl.Service`, elevated), swap the file, then reopen and re-install. The DLL name is stable across versions, so a version-to-version update within the same variant reuses the same file name and preserves your bindings. Switching between the standard, ARGB, and Lighting builds uses different file names (`FanControl.LianLi.dll`, `FanControl.LianLi.Argb.dll`, `FanControl.LianLi.Lighting.dll`), so remove the one you have first (the same lock applies - close FanControl to delete it) and never leave more than one in the Plugins folder. Each variant advertises a distinct plugin name in FanControl (`Lian Li Uni`, `Lian Li Uni (ARGB)`, `Lian Li Uni (Lighting)`), and FanControl folds that name into each control's binding key - so switching variants re-keys the controls and your fan-curve bindings must be re-pointed (or remapped in `userConfig.json`; see the binding-remap note in [lighting.md](lighting.md)).

## Controller identity and bindings

A control's binding key includes the controller's index, and the plugin now orders controllers by their OS device path so the same physical port keeps the same index across reboots, sleep, and hibernate - that is what stops bindings drifting between identical controllers. A single-controller system is unaffected. On a system with two or more identical controllers, upgrading from a build that ordered controllers by raw enumeration order can re-key the controls once if the stable order differs from the old order; re-point the affected curves after the first load (a one-time step). Duplicate channel sensors from a controller that exposed several HID interfaces are also gone, since those interfaces are now collapsed to one logical controller - if you previously bound curves to a duplicated set, re-point them to the single remaining set.

## Verifying a deployment

After reloading the plugin, `plugin.log` under the SYSTEM profile should show `Initialize: standard build, scan located N HID interface(s), M controller(s)` (or `ARGB build` / `Lighting build`) followed by `Set C{i}:{ch} = N%` lines (FanControl driving the curves) and, every 15s, the same lines tagged `(refresh)` - that is the keepalive re-assert. `M controller(s)` is the number of physical controllers; it is `N` minus any duplicate HID interfaces of the same controller that were collapsed (one physical controller can expose more than one matching HID interface), so `M < N` is normal and means a controller surfaced multiple interfaces. No `poll err` lines means RPM telemetry is reading cleanly. On the **Lighting** build, when L-Connect has a saved look you should also see `Lighting: read N L-Connect controller look(s)` near the top and a `lighting applied for <token> (N writes)` line for each controller whose look was re-applied (see [lighting.md](lighting.md)). Make sure Lian Li L-Connect is not running while you test: it drives the same controller and will fight the plugin.
