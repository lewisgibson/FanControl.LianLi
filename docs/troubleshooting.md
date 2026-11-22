# Troubleshooting

Answers to the problems people hit with the FanControl.LianLi plugin, each with the fix and the line to look for in the plugin log. The log is described at the [bottom of this page](#where-the-plugin-log-is), along with how to report a bug.

## Lian Li fans not showing up in FanControl

Check these in order:

1. **The plugin DLL was blocked by Windows.** Windows marks files downloaded from the internet as blocked, and FanControl silently ignores a blocked plugin. Right-click the `.dll`, choose Properties, tick Unblock, click OK, then install it again through FanControl's Install plugin button.
2. **L-Connect is still running.** Lian Li's L-Connect drives the same devices, and its background service keeps running after you close its window. Switch the service off as described in the README section [Using it with L-Connect](../README.md#using-it-with-l-connect), then restart FanControl. The log shows `open failed for <device>` while something else has the device open. This is the most common cause.
3. **Another tool has the controller open.** OpenRGB and other tools that talk to the controller directly over USB can clash in the same way. Close them and restart FanControl.
4. **FanControl is older than V245.** See the next section.
5. **The log says `Initialize: ... scan located 0 HID interface(s), 0 controller(s)`.** Windows did not list any Lian Li controller. Check the controller's USB cable and that it appears in Device Manager.

## FanControl lost every sensor after installing the plugin

The plugin needs FanControl V245 (October 2025) or newer. On an older FanControl the plugin can't be loaded, and the error stops FanControl loading all of its other sensors too, so FanControl looks empty. Your fans fall back to their default speeds (the BIOS for motherboard headers, the controller's own default for Lian Li hubs) until it is fixed, so nothing is at risk.

To fix it, delete `FanControl.LianLi*.dll` from FanControl's `Plugins` folder, update FanControl from [getfancontrol.com](https://getfancontrol.com/) or its [releases page](https://github.com/Rem0o/FanControl.Releases/releases), then install the plugin again.

## Fans stopped responding after sleep or hibernate

Windows re-plugs the controller on the way back from sleep, and the plugin reconnects to it by itself within a few seconds. The log shows a `timed out ... handle faulted` line or two, then `reopened <path> after N faulted transfer(s)` and the `Set` lines resuming. If an older version still freezes for you, update. If the current version does not recover, open an issue with the plugin log.

## FanControl sat on a blank window after waking

A controller can come back from sleep in a state where Windows never completes an open on it. Older versions waited on it with no deadline, on the very thread FanControl uses to refresh its sensors after a resume, so FanControl froze. Every wait on a controller is now bounded: FanControl stays responsive and the plugin keeps retrying the controller in the background. The log records every call it gave up on (`timed out after N ms`). If you still see a freeze on the current version, open an issue with the plugin log.

## Fans are missing after a reboot, or only some channels show

The plugin hides the channels on a UNI FAN hub that have no fan plugged in, so you get one control per real fan. A fan that is present but stopped while the plugin is starting looks the same as an empty channel and stays hidden until it next spins; if no channel looks populated, all four are shown.

A controller that is slow to answer at boot is remembered from the last run: its sensors stay registered, your curves stay bound, and it connects as soon as it answers. The plugin keeps that memory in `remembered-controllers.json` beside its log. It is safe to delete; the plugin relearns your devices on the next start.

## Fan speeds or lighting are erratic

L-Connect is almost certainly running alongside the plugin. Both write to the same controller and fight each other. Switch L-Connect's service off as described in the README section [Using it with L-Connect](../README.md#using-it-with-l-connect) and restart FanControl. Another FanControl plugin for the same hub, or a second copy of this one in the Plugins folder, does the same.

These lines in the log show a TL hub or a GALAHAD II cooler not answering the plugin's RPM request:

- `T<n>: the hub did not answer the handshake (...)`: the TL hub was asked for its fans' RPM, twice unless the first ask used up the 450 ms a poll allows, and sent something else each time. The readings stay where they were until it answers, which is logged as `T<n>: the hub answers the handshake again`, or `for the first time` if it never answered when the plugin started.
- `G<n>: the cooler did not answer the handshake (...)`: the same for a GALAHAD II or HydroShift LCD cooler, which gets one ask and up to a second, with `G<n>: the cooler answers the handshake again` (or `for the first time`) once it does.
- `HidD_FlushQueue failed on <path> ...`: Windows would not empty the plugin's queue of the controller's replies before a write. The write still went out, but an older reply may be read as the answer to the next request. It is logged once until a flush succeeds.

Another program talking to the same controller is the usual cause of the first two.

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

## Install plugin says "Wrong plugin format" or "Could not find plugin dll in zip archive"

The ARGB and Lighting builds up to v1.1.39 were named `FanControl.LianLi.Argb.dll` and `FanControl.LianLi.Lighting.dll`, which FanControl's Install plugin button does not accept: it only takes a file named `FanControl.<Name>.dll`. They are now named `FanControl.LianLi-Argb.dll` and `FanControl.LianLi-Lighting.dll`, which it does. Download the current release and install that. Your curves stay bound, since the plugin's name in FanControl and its sensors are unchanged.

## Two copies of the plugin, or the plugin is idle

If you copied an older ARGB or Lighting DLL into FanControl's `Plugins` folder by hand and then install the current one, FanControl loads both. Only one copy drives the fans: the one with the higher version (or, for the same version, the one installed last). It renames the other file to `<name>.dll.removed` so FanControl stops loading it, and deletes it at a later start. You don't need to delete anything yourself. When the other copy is from v1.1.39 or earlier, it keeps the fans until FanControl's next refresh. FanControl refreshes when the PC resumes from sleep or hibernation, when you switch Windows user sessions, when a plugin asks for one, or when you refresh it by hand; restart FanControl to make the switch straight away. When both copies are current ones and the older copy happened to start first, the older one asks FanControl for that refresh itself as soon as the newer one has removed it, so the switch takes effect at once. While a copy is idle, FanControl says the plugin could not initialise or has no sensors, and tries it again. That is expected. The log shows what happened:

- `removed the other copy of the plugin (<path> (version X)) from FanControl's Plugins folder`: the old file was renamed and FanControl no longer loads it.
- `is the newest but stays idle for now: the older copy at <path> (version X) may still be driving the fans in this FanControl process, and two copies would fight over them. Its file is no longer in the Plugins folder, so this copy takes over at FanControl's next refresh, or straight away if FanControl is restarted`: nothing to do, or restart FanControl to switch straight away.
- `is the newest but stays idle: the older copy at <path> (version X) is still driving the fans and could not be removed`: close FanControl, delete the file it names from the `Plugins` folder, then start FanControl again.
- `drives the fans for now: the newer copy at <path> (version X) is loaded but has not started`: FanControl started the older copy first. Once the newer one starts it removes the older one and the older one asks FanControl to refresh, so this line is usually followed straight away by `refresh wanted: a newer copy of the plugin removed this copy's file`.
- `drives the fans: the newer copy at <path> (version X) has not taken over, because it is disabled in FanControl's plugin settings or could not remove this copy`: enable the newer copy, or close FanControl and delete the file you don't want from the `Plugins` folder.
- `is the newest but stays idle: the copy at <path> (version X) is driving the fans and could not be removed`: the older copy starts first on every refresh, so it keeps the fans. Close FanControl, delete the file it names from the `Plugins` folder, then start FanControl again.
- `stays idle: the copy at <path> (version X) is newer and takes over the fans`: this is the copy being replaced. Nothing to do.
- `could not remove the other copy of the plugin` or `could not delete <path>`: Windows refused the change, usually because FanControl was running as the app under your own account, which can't change files under `Program Files (x86)`. Close FanControl and delete the file it names from the `Plugins` folder yourself.

This doesn't apply to the Standard build copied by hand into `Plugins\FanControl.LianLi.dll`: that file has the same assembly name as the one Install plugin puts in `Plugins\LianLi\`, so FanControl only loads whichever it finds first, which may be the old one. Close FanControl, delete the copy in the `Plugins` folder itself, and install again.

## My fan curves came unbound after updating

Each build shows up in FanControl under its own name (Lian Li Uni, Lian Li Uni (ARGB), Lian Li Uni (Lighting)), and FanControl folds that name into each control's identity. Switching between builds therefore re-keys the controls, and you need to point your curves at the new ones once. Updating within the same build keeps your bindings. One exception: on a system with two or more identical controllers, an old build ordered them in whatever order Windows listed them, and the plugin now keeps each controller's index with its physical port, so the first load of a current version can re-key those controls once. Re-point the affected curves and they stay put.

## A fan shows `-` instead of its RPM

The plugin hasn't measured that fan yet, or can't at the moment: a hub or cooler that hasn't answered since FanControl started, a controller still reconnecting after sleep, or a wireless fan that isn't being heard. It shows `-` rather than 0 so FanControl doesn't take it for a fan that failed to start and speed it up. The reading comes back on its own as soon as the fan is measured again. While it shows `-`, the tray icon may keep showing the last RPM (when FanControl runs as a service), and a control on a Sync curve in RPM mode that follows that fan stops until it reads again. If it stays at `-`, check the log for that controller's lines (`did not answer the handshake`, `not heard for 30 list reads`, `timed out ... handle faulted`).

## Unpair doesn't stick, or a control has no paired RPM

The plugin tells FanControl which RPM belongs to each control, and FanControl uses that whenever you haven't picked one yourself, so clicking Unpair puts the plugin's pairing straight back (on V245 it comes back at the next restart). Unpair also clears the control's calibration. To use a different reading, pair the control with that sensor instead: your choice is saved and kept.

A control with no pairing at all is one whose RPM sensor the plugin couldn't register, usually a V150 with no front fan fitted. The log says so with a line like `LianLi/w<address>/ctl has no fan speed registered, so FanControl is given no pairing for it`.

## Where the plugin log is

The plugin keeps its log in `%LOCALAPPDATA%\FanControl.LianLi`, under the Windows account that runs it. FanControl runs it either in the app under your own account or in its background service (`FanControl.Service`) as SYSTEM, and it can switch between the two, so look in both places. Open File Explorer, paste this into the address bar and press Enter:

```
%LOCALAPPDATA%\FanControl.LianLi
```

If `plugin.log` isn't there, or hasn't changed recently, FanControl is running it in the service. Open File Explorer as administrator and paste this instead:

```
C:\Windows\System32\config\systemprofile\AppData\Local\FanControl.LianLi
```

The folder also holds `remembered-controllers.json`, the controllers the plugin has seen before; attach it with the log when you report a bug. The log is kept to a bounded size; the previous one is `plugin.log.1` beside it. FanControl's own `service_log.txt`, in its Program Files folder, records a plugin that failed to load at all.

A healthy log starts with `Initialize: standard build, scan located N HID interface(s), M controller(s)` (or `ARGB build` or `Lighting build`), then a `Set C{i}:{ch} = N%` line each time FanControl changes a fan's speed. The plugin re-sends every speed every 15 seconds so the controller does not fall back to full speed, but it does not log those re-sends. `M` can be smaller than `N` when a controller exposes more than one USB interface; that is normal.

## Reporting a bug

Open an issue at [github.com/lewisgibson/FanControl.LianLi/issues](https://github.com/lewisgibson/FanControl.LianLi/issues) and include:

- the plugin log (see above) and which build you installed
- your FanControl version and Windows version
- the device's Name, VID and PID from Device Manager (right-click the device, Properties, Details, Hardware Ids)

The bug report template walks you through it. Questions and install help go in [Discussions](https://github.com/lewisgibson/FanControl.LianLi/discussions).
