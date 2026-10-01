# Troubleshooting

Answers to the problems people hit with the FanControl.LianLi plugin, each with the fix and the line to look for in the plugin log. The log is described at the [bottom of this page](#where-the-plugin-log-is), along with how to report a bug.

## Lian Li fans not showing up in FanControl

Check these in order:

1. **The plugin DLL was blocked by Windows.** Windows marks files downloaded from the internet as blocked, and FanControl silently ignores a blocked plugin. Right-click the `.dll`, choose Properties, tick Unblock, click OK, then install it again through FanControl's Install plugin button.
2. **L-Connect is still running.** Lian Li's L-Connect drives the same devices, and its background service keeps running after you close its window. Switch the service off as described in the README section [Using it with L-Connect](../README.md#using-it-with-l-connect), then restart FanControl. The log shows `open failed for <device>` while something else has the device open. This is the most common cause.
3. **Another tool has the controller open.** OpenRGB and other tools that talk to the controller directly over USB can clash in the same way. Close them and restart FanControl.
4. **FanControl is older than V243.** See the next section.
5. **The log says `Initialize: ... scan located 0 HID interface(s), 0 controller(s)`.** Windows did not list any Lian Li controller. Check the controller's USB cable and that it appears in Device Manager.

## FanControl lost every sensor after installing the plugin

The plugin needs FanControl V243 (October 2025) or newer. On an older FanControl the plugin can't be loaded, and the error stops FanControl loading all of its other sensors too, so FanControl looks empty. Your fans fall back to their default speeds (the BIOS for motherboard headers, the controller's own default for Lian Li hubs) until it is fixed, so nothing is at risk.

To fix it, delete `FanControl.LianLi*.dll` from FanControl's `Plugins` folder, update FanControl from [getfancontrol.com](https://getfancontrol.com/) or its [releases page](https://github.com/Rem0o/FanControl.Releases/releases), then install the plugin again.

## Fans stopped responding after sleep or hibernate

Windows re-plugs the controller on the way back from sleep, and the plugin reconnects to it by itself within a few seconds. The log shows a `timed out ... handle faulted` line or two, then `reopened <path> after N faulted transfer(s)` and the `Set` lines resuming. If an older version still freezes for you, update. If the current version does not recover, open an issue with the plugin log.

## FanControl sat on a blank window after waking

A controller can come back from sleep in a state where Windows never completes an open on it. Older versions waited on it with no deadline, on the very thread FanControl uses to refresh its sensors after a resume, so FanControl froze. Every wait on a controller is now bounded: FanControl stays responsive and the plugin keeps retrying the controller in the background. The log records every call it gave up on (`timed out after N ms`). If you still see a freeze on the current version, open an issue with the plugin log.

## Fans are missing after a reboot, or only some channels show

The plugin hides the channels on a UNI FAN hub that have no fan plugged in, so you get one control per real fan. A fan that is present but stopped while the plugin is starting looks the same as an empty channel and stays hidden until it next spins; if no channel looks populated, all four are shown.

A controller that is slow to answer at boot is remembered from the last run: its sensors stay registered, your curves stay bound, and it connects as soon as it answers. The plugin keeps that memory in `remembered-controllers.json` beside its log. It is safe to delete; the plugin relearns your devices on the next start.

## Fan speeds or lighting are erratic

L-Connect is almost certainly running alongside the plugin. Both write to the same controller and fight each other. Switch L-Connect's service off as described in the README section [Using it with L-Connect](../README.md#using-it-with-l-connect) and restart FanControl.

## Lighting resets to the factory rainbow on every boot

You are on the ARGB build with a controller that does not keep its lighting in its own memory (the UNI FAN SL-Infinity 120 V1, for one). The ARGB build hands the lighting to the motherboard's ARGB header every time the plugin starts, and on those controllers that wipes the lighting. Switch to the Standard build, which never touches lighting, or to the Lighting build, which re-applies your saved L-Connect look.

## The Lighting build does not apply my look

The Lighting build reads L-Connect's own saved settings under `C:\ProgramData\Lian-Li\L-Connect 3`, so L-Connect has to have been installed and the look applied there at least once. Near the top of the log you should see `Lighting: read N L-Connect controller look(s)` and then `lighting applied for <device> (N writes)` for each controller. If it reads 0 looks, L-Connect has nothing saved for that controller. If the configuration is unreadable, the log says so and lighting is switched off for that device while fan control carries on. A controller whose "sync to motherboard" switch is on in L-Connect is handed to the motherboard instead of getting a look; the log says `lighting left to the motherboard for <device>`.

## Wireless fans not showing

The wireless fans pair with the L-Wireless SYNC controller, whose two dongles Windows binds to its WinUSB driver. The log shows the dongles being found, as `controller wireless master=... devices=...`, or why not:

- `has no WinUSB interface registered`: Windows has not bound the dongle to WinUSB. In Device Manager it should sit under Universal Serial Bus devices. The plugin reaches the dongles the way L-Connect does, so if L-Connect can see them, the plugin should too.
- `open failed`: L-Connect's service is still running and has the dongles open. Switch it off ([Using it with L-Connect](../README.md#using-it-with-l-connect)).
- `master=not answering yet` or `devices=0`: the dongle has not finished starting, or nothing is paired to it yet. The plugin keeps listening and adds the fans as soon as they check in. Pairing is done in L-Connect; the plugin never pairs or unpairs anything.

The very first time you run the plugin with a new wireless controller you may see a temperature called "Lian Li: waiting for devices" with no reading. It goes away by itself once the fans appear.

## HydroShift II OLED Curve pump not showing

Its pump rides on the cooler's lighting device, which Windows binds to WinUSB like the wireless dongles, so the same log lines apply: `HydroShift II OLED Curve pump ... has no WinUSB interface registered` means Lian Li's driver is not bound to it, and `open failed` means L-Connect's service still has it open. The plugin talks to it exactly the way L-Connect does, so if L-Connect can see the cooler, the plugin should too.

## FLEX fans on a USB receiver not showing, or not taking a speed

The receiver is a WinUSB device like the dongles, so the same log lines apply: `UNI FAN TL FLEX receiver ... has no WinUSB interface registered` means Windows has not bound Lian Li's driver to it, and `open failed` means L-Connect's service still has it open. Two more lines are specific to the receivers:

- `left to the radio`: the chain is also paired to your L-Wireless dongle, and the dongle drives it, as in L-Connect. Unpair it in L-Connect if you want it driven over USB.
- `now answers as`: another receiver is answering on that USB port (two swapped between ports while the PC slept, say). Nothing is sent to it, and it is picked up under its own address on the refresh that follows.

The plain SL-INF FLEX, SL FLEX and CL FLEX receivers are not driven over USB at all, because L-Connect gives them no fan control there either. Pair them to the L-Wireless SYNC controller instead.

## My fan curves came unbound after updating

Each build shows up in FanControl under its own name (Lian Li Uni, Lian Li Uni (ARGB), Lian Li Uni (Lighting)), and FanControl folds that name into each control's identity. Switching between builds therefore re-keys the controls, and you need to point your curves at the new ones once. Updating within the same build keeps your bindings. One exception: on a system with two or more identical controllers, an old build ordered them in whatever order Windows listed them, and the plugin now keeps each controller's index with its physical port, so the first load of a current version can re-key those controls once. Re-point the affected curves and they stay put.

## Where the plugin log is

FanControl loads plugins in its background service (`FanControl.Service`), which runs as SYSTEM, so the plugin's log is under the SYSTEM profile , not your own:

```
C:\Windows\System32\config\systemprofile\AppData\Local\FanControl.LianLi\plugin.log
```

If you run the plugin outside the service it is at `%LOCALAPPDATA%\FanControl.LianLi\plugin.log` instead. The log is kept to a bounded size; the previous one is `plugin.log.1` beside it. FanControl's own `service_log.txt`, in its Program Files folder, records a plugin that failed to load at all.

A healthy log starts with `Initialize: standard build, scan located N HID interface(s), M controller(s)` (or `ARGB build` or `Lighting build`), then `Set C{i}:{ch} = N%` lines as FanControl drives the curves, and the same lines tagged `(refresh)` every 15 seconds. `M` can be smaller than `N` when a controller exposes more than one USB interface; that is normal.

## Reporting a bug

Open an issue at [github.com/lewisgibson/FanControl.LianLi/issues](https://github.com/lewisgibson/FanControl.LianLi/issues) and include:

- the plugin log (see above) and which build you installed
- your FanControl version and Windows version
- the device's Name, VID and PID from Device Manager (right-click the device, Properties, Details, Hardware Ids)

The bug report template walks you through it. Questions and install help go in [Discussions](https://github.com/lewisgibson/FanControl.LianLi/discussions).
