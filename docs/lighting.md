# Lighting replay (the `Lighting` build)

This document describes the **Lighting** build variant (`FanControl.LianLi.Lighting.dll`): what it does, how it works, the SL-Infinity wire protocol it reproduces, and how it stays fail-safe. For the user-facing summary see the README's [Which download?](../README.md#which-download) and [Using it with L-Connect](../README.md#using-it-with-l-connect) sections. What it says L-Connect does was read out of L-Connect 3's own binaries (assembly version 2.1.29); where 2.1.11 differs, it says so.

## What it is for

The Lian Li Uni controllers do **not** persist their LED state to onboard flash. The look you set survives while the PC is powered, but a power-off or cold boot reverts the fans to the factory rainbow. The usual way to restore it is to leave L-Connect installed so it re-applies your profile on boot - but L-Connect drives the same USB controller as this plugin and the two fight, which is why the plugin otherwise asks you to remove it.

The Lighting build resolves that tension: it reads the look you already designed in L-Connect and **re-applies it itself at startup**, and again whenever it reconnects to a controller that came back from sleep or hibernate. You design once in L-Connect, stop L-Connect so it is no longer fighting for the controller, and this plugin keeps the look across every reboot.

## How it works

The plugin reads L-Connect's **own saved configuration directly** - there is no import step, no capture, and no intermediary file. On startup it reads the gzipped JSON under `C:\ProgramData\Lian-Li\L-Connect 3\device` (read-only) and re-applies the saved look to the controllers.

On startup the Lighting build:

1. Reads L-Connect's per-device settings from `C:\ProgramData\Lian-Li\L-Connect 3\device` (each setting is a gzipped JSON file holding `{ DeviceID, Type, Data }`). It only reads; it never writes to L-Connect's files.
2. Groups the `LightingPort*`, `FanQuantity` and `MergeOrder` settings (and the other families' equivalents) by the USB instance token in each `DeviceID`, together with that controller's `MotherboardARGBSync` switch (see [Motherboard ARGB sync](#motherboard-argb-sync-the-l-connect-switch)).
3. For each controller FanControl locates, matches it to a saved look by that instance token, and - for an SL-Infinity controller - encodes the exact HID transfers L-Connect itself would send and writes them to the device before fan setup.

All of the translation between L-Connect's saved settings and the controller (mode and colour encoding, fan-quantity, frame latch, apply order) is in a single pure C# encoder, `Protocol/SlInfinityLightingEncoder.cs`, with the test suite asserting its output. Nothing about it is offline or external: build the DLL, drop it in, done.

### SL-Infinity merge effects

L-Connect's merge effects (Runway, MopUp, Mixing, Stack, Tide, Scan, Door, HeartBeatRunway and ElectricCurrent in their `_Merge` form) run one effect across every fan group from port 0. When L-Connect applies one it saves only port 0, so the `LightingPort1`..`LightingPort7` files still hold whatever each port showed before. L-Connect shows a merge effect with its own sequence (`setMergeLighting`): it blanks ports 7 down to 1 with an effect-only feature report `{E0, 0x10|port, 0x32, 0, 0, 0x08}` (Rainbow at brightness Off, no colour report because L-Connect's empty colour list sends none), then writes port 0's colour and effect last, and latches no frame.

On a cold start that sequence is never the first thing the controller receives. `SLInfinityController.Init` runs `setupDefaultLightingConfig` (StaticColor_Inner on the even ports and StaticColor_Outer on the odd, red, blue, green and yellow, brightness Highest) and then `setFanQuantity`, whose `setAllLightingConfig` writes that default look on all eight ports and latches a frame (`syncLightingFrame`), before any saved setting has been read; only then does `ApplyAll` run the merge sequence. Whether the merge effect needs that earlier latch is not known without the hardware: L-Connect's own resume (`ResumeSuspend`) sends the merge sequence without it, but every cold start has it, and it costs nothing on a controller that does not need it.

The plugin reproduces both: after the fan quantity it writes the default look on ports 7 down to 0 (colour report and effect `{E0, 0x10|port, 1, 0, 0, 0}` each) and the frame `{E0, 96, 0, 1}`, then the merge order, then the seven blanks and port 0, and no further frame. The saved looks of ports 1..7 are not replayed in merge mode: they are stale, and a frame latched after them would let an old per-port effect show through wherever the firmware let it win. Only port 0's saved mode decides merge mode, as L-Connect's own `isMergeMode` reads only port 0.

### Merge effects on the other Uni families

The SL, AL, SL v2, AL v2 and Redragon controllers each have their own merge sequence in L-Connect (`setMergeLighting` in each family's controller, unchanged between 2.1.11 and 2.1.29), and the plugin sends each one exactly. On these families L-Connect's `setFanQuantity` always writes every saved port and latches the frame before anything else happens, in merge mode or not, so the plugin does the same: the fan quantity, every saved port in the family's order, the frame, and then, when port 0 holds one of the family's merge effects:

- **SL and Redragon:** the `StartMerge` report `{E0, 0x10, 0x33, 0, 1, 2, 3, 8}` (an 8-byte report that names the four groups in order), then port 0's colour and effect again. The SL's merge effects are Meteor_Merge and Runway_Merge.
- **AL:** the merge command written on, `{E0, 0x10, 0x43, 1}`, then port 0 again. The AL's merge effects are Contest_Merge and Scan_Merge.
- **SL v2:** port 0 again and nothing else; the merge is carried by the effect byte alone. Its merge effects are Meteor, Mixing, Runway, StackMulti and Tide in their `_Merge` form.
- **AL v2:** port 0 again, then ports 1 up to 7 blanked with the same effect-only report the SL-Infinity uses, but with the AL v2's own Rainbow byte: `{E0, 0x10|port, 43, 0, 0, 0x08}`. Its merge effects are every `_Merge` mode in its table.

No frame is latched after any of these, as L-Connect latches none. Only port 0's saved mode decides merge mode on every family; a merge mode saved on another port is replayed like any other port. L-Connect never sends the SL's `StopMerge` or the AL's merge command written off at a start or a resume, only when the user changes a merged look back in its UI, so neither does the plugin.

### The merge order

The SL-Infinity, SL v2 and AL v2 also take a **merge order**: the order the four fan groups chain in a merge effect, written as `{E0, 0x10, 0x63, o0, o1, o2, o3, 8}` (8 bytes). The order the user saved (`MergeOrder`) reaches the register only when its four entries are each 0 to 4; otherwise it is refused and nothing is written. The SL and AL have no merge-order register.

What L-Connect writes, and when: `Init` and `ResumeSuspend` (in all three controllers) call `setFanQuantity` and then `setMergeOrder(DefaultMergeOrder)`, which is always `0, 1, 2, 3`. The saved order reaches the register only at startup, when `ApplyAll` applies the saved settings after `Init`, and whenever the user changes it. `ApplyAll` turns `autoSendToDevice` off and applies each saved setting in turn: a saved port look is only stored then, but `handleMergeOrder` goes straight to `setMergeOrder`, which writes the register whatever that flag says, so the saved order is written inside the loop, and after the loop `ApplyAll` writes the saved look once more (`setMergeLighting` in merge mode, `setAllLightingConfig` otherwise).

The loop takes the settings in the order `DeviceSettingManager.LoadAll` enumerates their files, which are named by the MD5 hash of each setting key, so whether the `FanQuantity` setting (whose handler writes a look and a frame) comes before or after `MergeOrder` is set by those hashes rather than by any intent.

A look and a frame precede the register either way: `setFanQuantity` does not return before it has written a look and latched a frame (on the SL-Infinity `isMergeMode() ? setMergeLighting() : setAllLightingConfig()`, which at a cold start is the default look since no saved setting has been read yet; on the SL v2 and AL v2 `setAllLightingConfig()` unconditionally), so in `Init` and `ResumeSuspend` the controller has a look and a frame before the default order is written, and `Init` has done that before `ApplyAll` runs.

So at a start the register ends up with the saved order, and the saved look is the last thing written. On a resume L-Connect writes the default order and never the saved one (the saved look is written a second time after it, the order is not), so a saved `[2, 0, 1, 3]` chains as `0, 1, 2, 3` from a wake until L-Connect is next restarted.

The plugin sends the saved order - or `0, 1, 2, 3` when none is saved or the saved one fails that validation - after the look and its frame at every start and again on every reconnect: the saved look in the ordinary case, the default look in merge mode, either written once, then the report, then in merge mode the merge sequence. It sends it whether or not the controller's sync switch is on, since L-Connect sends it regardless; with the switch on there is no look, and the report precedes the sync register as `setMergeOrder` precedes `setMotherboardARGBSync`.

That placement, after the look and its frame, is the plugin's own consistent choice: it matches where `Init` and `ResumeSuspend` put the register instead of copying `ApplyAll`'s hash-ordered loop, and the register's end state at a start is L-Connect's, without its redundant default write. On a reconnect it is a chosen departure from L-Connect's resume, which loses the saved order until a restart - an L-Connect bug the plugin does not copy. With the default order saved the two are the same bytes.

### Motherboard ARGB sync (the L-Connect switch)

L-Connect keeps its "sync to motherboard" lighting switch per controller (2.1.11 had one global setting), saved beside that controller's look as a device setting of type `MotherboardARGBSync` (a gzipped `{ DeviceID, Type: "MotherboardARGBSync", Data: true|false }`, in the folder `device\<md5(device path)>\<md5("motherboardargbsync")>.0`). A user upgrading from 2.1.11 has no such file, and L-Connect itself starts them all off, so a missing, unreadable or non-boolean file means off. The plugin reads the switch with the look and, when it is on, does for that controller what L-Connect does at its start and after a resume, instead of replaying the look:

- **Uni SL / AL / SL-Infinity / SL v2 / AL v2 / Redragon:** the fan quantity (and on the SL-Infinity, SL v2 and AL v2 the merge order), then the family's ARGB-sync register written on (`{E0, 0x10, 48|65|97, 1}`, see [protocol.md](protocol.md#argb-sync-report-argb-build-and-a-synced-controller-on-the-lighting-build)), and nothing else. The look, merge mode or not, is not written and no frame is latched.
- **Strimer Plus:** the sync register `{E0, 0x10, 64, 1, 0, 0}` and a port-0 enable `{E0, 0x20, 0, 0}`, and no effects.
- **Galahad II Trinity:** the fan and pump lights are still written, with the ARGB signal source byte set to the motherboard (`1`) instead of the on-board MCU (`0`), which is how L-Connect writes them when the switch is on. `Galahad2TrinityController.ApplyAll` calls `setFanLEDLighting` and `setPumpLEDLighting` whatever was saved, writing its own default `FanLightingSetting` (Rainbow, brightness 2, speed 2, 24 LEDs) or `PumpLightingSetting` (scope All, Rainbow, brightness 2, speed 2) for a half that never was, so the plugin does the same and says which half was defaulted in the log; without the switch a half that was never saved still skips the whole look. `setPumpLEDLighting` writes every entry of the saved `PumpLEDLighting` array, which L-Connect's individual mode saves as two (scope Inner then Outer), so every saved scope is written, switch or no switch.
- **Uni Fan TL:** nothing. L-Connect writes a TL hub no lighting at all while its switch is on (it only sends the per-fan sync packets at the moment the switch is flipped), so the plugin leaves the hub as it found it and says so in the log.
- **Galahad II Vision and HydroShift LCD:** L-Connect has no per-controller switch for them (their controllers load no such setting and their fan light is always written with the on-board MCU as its source), so their saved look is always replayed and a `MotherboardARGBSync` file, which L-Connect never writes for them, would be ignored.

The switch is honoured again on every reconnect, as the look would be. Because the switch on its own is a configuration (L-Connect hands the LEDs over whether or not a look was ever applied), a Uni controller with the switch on and no saved look still gets its sync register. The log line for a synced controller reads `lighting left to the motherboard for <token> (N writes)`.

This is the same report the **ARGB build** sends every Uni controller at startup. The two builds agree: the ARGB build hands every Uni controller to the motherboard unconditionally and needs no L-Connect configuration, while the Lighting build hands over only the controllers whose L-Connect switch is on and replays the saved look on the rest. Both re-assert it after a reconnect. The ARGB build does not read the switch, and the Lighting build never asserts the register for a controller whose switch is off.

### Galahad II Vision and HydroShift LCD

These two coolers share the Galahad II Trinity's 0x0416 command packets for their lighting, and the plugin replays them from the same `FanLEDLighting` setting the Trinity uses, with one difference in the saved file: the Vision's `FanLightingSetting` carries its four colours as `Color1`..`Color4` rather than a `Colors` array, and the reader accepts either.

- **HydroShift LCD (`0x7398`, `0x7399`, `0x739A`):** its radiator fans are its only LEDs. L-Connect's `HydroShiftLCDController.ApplyAll` ends in one `SetFanLighting` (0x85, a 20-byte payload: mode, brightness, speed, four R,G,B colours, direction, not disabled, source MCU, sync-to-pump, LED count), and that is the one packet the plugin writes. Its modes (Rainbow 1 to Bounce 16) are the wire byte directly.
- **Galahad II Vision (`0x7391`, `0x7395`):** the fan light as above, then the ring of twelve LEDs around its screen, which L-Connect saves as a `ScreenLEDLighting` setting and writes through the Trinity's pump-light command (0x83, 19 bytes) with the scope byte at Inner (0), the ring mode as saved, and the static look's brightness and speed sliders (0 to 100 in steps of 25, saved as `int.MinValue` when unset) each divided by 25, exactly as `setScreenLEDLighting` maps them. The screen itself (the LCD's image) is out of scope.

A `ScreenLEDLighting` whose `IsDynamicMode` is true is one L-Connect recolours every second from a CPU load, CPU or GPU temperature, pump RPM or coolant temperature reading. The plugin has no such reading to colour it from, so it leaves the ring as it finds it, writes the fan light alone, and logs `screen ring left as found for <token>: its saved look follows a live sensor in L-Connect`.

Either half of a Vision look replays on its own: a Vision with only a fan light saved gets its fan light, one with only a static ring saved gets its ring. L-Connect itself would write its built-in default (Rainbow) to a half that was never saved, and the plugin does not, since it replays only what the user saved.

## Keeping a look

There is no capture or import step. Design your lighting in L-Connect and apply it (so L-Connect writes its config), then **stop L-Connect** - fully exit it and stop its background service/process so it stops driving the controller. Install the Lighting build and it reads L-Connect's saved config and re-applies the look on every start.

L-Connect's config lives under `C:\ProgramData\Lian-Li\L-Connect 3`; the plugin needs it to remain on disk. You can stop L-Connect from running, but do not delete its configuration - that is where the look is stored.

## Fail-safe behaviour

Lighting is driven only when it can be done exactly. `LConnectConfigurationReader`, the per-family lighting encoders, and the plugin together guarantee:

- **No L-Connect configuration** (the directory is absent, e.g. L-Connect was never installed) -> no lighting is driven; the build behaves like the standard build. This is the opt-out path and the default.
- **A located controller with no matching saved look** -> that controller is left untouched.
- **A controller of an unsupported family** (anything not listed under [Status](#status)) -> skipped and logged; the plugin never drives unverified bytes.
- **A Galahad II Vision screen ring that follows a live sensor in L-Connect** -> the ring is left as found (the plugin has no sensor reading to drive it from) and the fan light is still written.
- **A port whose mode L-Connect itself does not apply** -> that port is left alone while the others still apply, as L-Connect does.
- **A controller whose L-Connect "sync to motherboard" switch is on** -> its look is not driven; the controller is handed to the motherboard's ARGB header the way L-Connect hands it over (see above).
- **An unreadable/corrupt configuration** -> logged, and lighting is disabled instead of applying a partial look. **Fan control is never affected** in any of these cases.

The plugin logs what it did at startup (`Lighting: read N L-Connect controller look(s)` and `lighting applied for <token> (N writes)`), so the log file shows which controllers got a look.

## Limitations

- **Distinct plugin name re-keys your controls.** The Lighting build advertises itself to FanControl as `Lian Li Uni (Lighting)` (so you can tell which build is loaded). Because FanControl folds the plugin name into each control's binding key, switching to or from this build re-keys the controls and your fan-curve bindings must be re-pointed. If you are migrating an existing config and want to keep the bindings, remap the identifier prefix in `userConfig.json` from `Lian Li Uni/` to `Lian Li Uni (Lighting)/` (back the file up first).
- **Do not run another lighting tool at the same time.** The Lighting build owns the LEDs at startup; running L-Connect, OpenRGB, SignalRGB, etc. alongside it re-introduces the two-writers conflict. If you use one of those, use the **standard** build instead, which never touches lighting.
- **One DLL at a time.** Never leave more than one `FanControl.LianLi*.dll` in the Plugins folder.

## Status

- **Uni SL-Infinity (`0xA102`)** - supported and tested end to end on real hardware.
- **Uni SL (`0xA100`), AL (`0xA101`), SL v2 (`0xA103`/`0xA105`), AL v2 (`0xA104`), Redragon (`0xA106`)** - supported via `UniFanLightingEncoder`, a parameterised encoder driven by a per-family profile (apply order, quantity register/packing, mode->wire table, colour-expansion model). The tables were extracted from L-Connect's own controllers and are byte-tested and adversarially cross-checked against the decompile, but not yet confirmed on that exact hardware.
- **Strimer Plus (`0xA200`), Uni Fan TL (`0x7372`), Galahad II Trinity (`0x7371`/`0x7373`)** - supported by their own encoders; reproduced from L-Connect and awaiting community confirmation on hardware.
- **Galahad II Vision (`0x7391`/`0x7395`) and HydroShift LCD (`0x7398`-`0x739A`)** - the fan light on both and the Vision's screen ring in its static modes (see [above](#galahad-ii-vision-and-hydroshift-lcd)); reproduced from L-Connect and awaiting community confirmation on hardware. A Vision ring effect that follows a sensor is left alone.
- Anything not listed is left untouched (the plugin never sends unverified lighting).

## Layering

The lighting code is gated behind the `ENABLE_LIGHTING` compile symbol (the standard and ARGB builds contain none of it) and is in:

- `Transport/IDeviceTransport.SetFeature` and `Transport/HidTransport` - the feature-report write capability (HID `SetFeature` / `HidD_SetFeature`). This interface method is the only piece that is not gated (a harmless unused capability in the other builds).
- `Protocol/RgbColor`, `Protocol/LightingPortState`, `Protocol/LightingTransfer` - the value types the encoder consumes and produces.
- `Protocol/SlInfinityLightingEncoder` - the pure encoder: saved per-port look in, exact HID transfers out. Byte-tested.
- `Protocol/UniFanLightingEncoder` and `Protocol/UniFanLightingProfiles` - the same for the other Uni families, one profile per family carrying its registers, tables and merge sequence; `Protocol/StrimerPlusLightingEncoder`, `Protocol/TlFanLightingEncoder` and `Protocol/Galahad2LightingEncoder` (with `Galahad2ScreenLightingState` for the Vision's ring) for the rest. All byte-tested.
- `Devices/JsonValue` - a tiny dependency-free JSON reader (the plugin ships a single DLL and cannot take a JSON NuGet dependency on netstandard2.0).
- `Devices/LConnectConfigurationReader` and `Devices/LConnectControllerConfiguration` - read L-Connect's config directory and group it per controller, including the per-controller `MotherboardARGBSync` switch and `MergeOrder`.
- `Devices/LightingReplay` - writes the encoded transfers in order, paced like L-Connect.
- `Plugin/LianLiPlugin` - reads the config and applies a matching look during `Initialize`, before fan setup, and registers the same apply as each controller's reconnect replay so a controller that was re-enumerated (and possibly reset) across sleep or hibernate gets its look back on the next tick.

## The wireless devices

The wireless range works the same way in spirit and differently in every detail. L-Connect does not send a wireless device an effect by name: it renders the effect to frames on the PC, compresses them, and streams the bytes over the radio. That rendering is tens of thousands of lines and a native compressor, and reimplementing it is neither possible nor necessary, because L-Connect saves the finished result to disk - so the Lighting build reads the saved effect and streams it back exactly as L-Connect would, including asking the devices to commit it to flash and keeping the once-a-second clock pulse that holds an effect in step across several devices.

The consequence is the same as for the wired controllers, only more so: the plugin replays a look, it cannot compose one. Change it in L-Connect, then close L-Connect again. The wire formats are in [`wireless.md`](wireless.md). The per-device "sync to motherboard" switch exists for the wireless devices too (2.1.11 had none), saved under the device's RF address; the Lighting build reads it and hands such a device's lighting to the motherboard with the RF command L-Connect uses, and still streams it its saved effect whenever it does not report running it, as L-Connect's `SyncRgbData` does whatever the switch says (see [`wireless.md`](wireless.md), "Lighting").

## Saved numbers that L-Connect renumbers

L-Connect 2.1.29 inserted `Turbo` at 8 in its `RPMMode` enum, so `FixRPM`, `PWM`, `PWMQuiet` and `PWMPerformance` each moved up by one in every profile it saves from then on, and it migrates nothing. The plugin reads no `RPMMode` as an absolute number: the start/stop reader matches a group's `RPMSetting.Mode` against the `Mode` of each entry in the same file's `Profiles`, so both numbers come from the same L-Connect version and the match holds under either numbering.

The lighting enums the plugin does compare to fixed numbers (`LightingMode`, `LightingBrightness`, `LightingSpeed`, `LightingDirection` for the Uni families, the TL's `LightingMode`, the Galahad II's fan and pump modes and scope, and the Strimer Plus modes) are unchanged between 2.1.11 and 2.1.29, as are every family's mode-to-wire table and every device class. Anything that later reads an `RPMMode` must key on the `Profiles` entry name, which is stable, never on the number.

It respects the same rules as the rest of the plugin (see [`.claude/rules/`](../.claude/rules/) and [`architecture.md`](architecture.md)): the encoder is pure, native USB calls stay confined to `Transport/`'s adapters, and the feature is invisible in the standard and ARGB builds.
