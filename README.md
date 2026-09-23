# FanControl.LianLi

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Latest release](https://img.shields.io/github/v/release/lewisgibson/FanControl.LianLi?sort=semver)](https://github.com/lewisgibson/FanControl.LianLi/releases/latest)

A plugin for [FanControl](https://getfancontrol.com/) that drives Lian Li UNI FAN controllers, STRIMER RGB cables, and GALAHAD II / HydroShift liquid coolers. It turns every fan (and cooler pump) into a control you can put on a temperature curve, reports their RPM, and can keep your L-Connect lighting without running L-Connect. See [Supported devices](#supported-devices).

This is an unofficial, community plugin. It is not affiliated with, authorized by, or endorsed by Lian Li or by the FanControl project.

## Requirements

**FanControl V243 or newer.** The plugin asks FanControl to refresh itself when a device appears late - a wireless fan that pairs back up a few seconds after a wake, for example - and the way a plugin does that arrived in FanControl V243 (October 2025).

On an older FanControl, installing this plugin makes FanControl lose **every** sensor, not only the Lian Li ones: FanControl can't read the plugin, and the error stops it loading everything else too. Your fans fall back to their default speeds (the BIOS for motherboard headers, the controller's own default for Lian Li hubs) until it's fixed, so nothing is at risk, but FanControl will look empty. Please update FanControl first, from [getfancontrol.com](https://getfancontrol.com/) or its [releases page](https://github.com/Rem0o/FanControl.Releases/releases). If you've already hit this, delete `FanControl.LianLi*.dll` from FanControl's `Plugins` folder, update FanControl, then install the plugin again.

## Supported devices

The plugin finds your Lian Li gear automatically - you don't need to know any model numbers. Here's what it does for each:

- **Speed control** - every fan (and, on the coolers, the pump) shows up in FanControl as a control you can put on a temperature curve.
- **RPM** - it reports each fan's and pump's live speed.
- **Lighting** - the **Lighting build** re-applies the colours you set up in L-Connect, so your lighting survives a reboot without L-Connect running (see [Lighting](#lighting-keep-your-l-connect-look-without-l-connect) below). The standard and ARGB builds don't touch lighting.

| Device                                                                        | Speed control | RPM | Lighting |
| ----------------------------------------------------------------------------- | :-----------: | :-: | :------: |
| UNI FAN SL (SL120 / SL140)                                                    |      ✅       | ✅  |    ✅    |
| UNI FAN AL                                                                    |      ✅       | ✅  |    ✅    |
| UNI FAN SL-Infinity                                                           |      ✅       | ✅  |    ✅    |
| UNI FAN SL V2                                                                 |      ✅       | ✅  |    ✅    |
| UNI FAN AL V2                                                                 |      ✅       | ✅  |    ✅    |
| UNI FAN SL (Redragon edition)                                                 |      ✅       | ✅  |    ✅    |
| UNI FAN TL                                                                    |      ✅       | ✅  |    ✅    |
| STRIMER Plus / Plus V2 (RGB PSU cables)                                       |      n/a      | n/a |    ✅    |
| GALAHAD II Trinity (AIO cooler)                                               | ✅ fan + pump | ✅  |    ✅    |
| GALAHAD II Vision / LCD (AIO cooler)                                          | ✅ fan + pump | ✅  |    ✅    |
| HydroShift LCD (AIO cooler)                                                   | ✅ fan + pump | ✅  | ✅ fans  |
| UNI FAN SL V3 / TL V2 / SL-INF Wireless / CL (via the L-Wireless SYNC dongle) |      ✅       | ✅  |    ✅    |
| HydroShift II (wireless AIO cooler)                                           | ✅ fan + pump | ✅  |    ✅    |
| Lancool 217 Infinity case fans (wireless)                                     |      ✅       | ✅  |    ✅    |

Extra touches: if you turned on L-Connect's **start/stop (zero-RPM)** switch, the plugin honours it - the fans that support it stop at 0%. On the LCD coolers the plugin drives the fans, pump, and RGB; it does **not** touch the screen. On the UNI FAN controllers the plugin **only shows the channels that actually have a fan plugged in** - it checks each channel at startup and hides the empty ones, so you get one control per real fan instead of four slots with three dead ones. (If a channel is genuinely in use but happens to be stopped at that moment, it may be hidden until it next spins; if detection is inconclusive the plugin shows all four rather than hide anything.)

### The wireless range

The **wireless** fans are not on a USB controller at all: they pair over radio with the L-Wireless SYNC controller, whose two dongles Windows sees as plain USB devices rather than fan controllers. The plugin talks to them the same way L-Connect does, so everything you paired shows up: one control per fan group (L-Connect drives a group at one speed too) with its own RPM reading for every fan in it, a pump control and a coolant temperature for a wireless AIO, and the front pair and rear fan of a Lancool 217 as two controls (each once a fan is fitted there). A wireless fan that pairs back up a little after a boot or a wake shows up on its own a few seconds later. The very first time you run the plugin with a new wireless controller, before anything has checked in, you may see a temperature called **Lian Li: waiting for devices** with no reading; it goes away by itself as soon as the fans appear. (The same happens on a first run if a wired controller is slow to open, or a TL hub has not heard from its fans yet.) The Lighting build also replays the look you saved for each of them.

Two things to know:

- **Pair the fans in L-Connect first**, then close it. The plugin never pairs or unpairs anything, and it only ever drives what is already paired to your dongle. It keeps the radio channel L-Connect chose, and moves off it only the way L-Connect itself does, when another L-Wireless controller nearby is on the same one. L-Connect and the plugin can't both have the dongles open at once, so stop FanControl before you pair anything, pair with L-Connect's services running, then switch them off again and start FanControl (see [Do not run L-Connect at the same time](#do-not-run-l-connect-at-the-same-time)).
- **On a wireless AIO the screen keeps your colours, brightness, rotation and theme, but stops showing the CPU and GPU figures.** Those numbers have to be sent to the cooler along with the pump speed, and the plugin has none to send - so it hides them rather than leave wrong ones frozen on your screen.

Wireless support is built from what L-Connect itself sends and is tested against that byte for byte, but has not been confirmed on real wireless hardware yet. If you have some, an issue with your plugin log is very welcome.

> **Tested on hardware:** the **UNI FAN SL-Infinity** is verified on real hardware, and the **GALAHAD II Trinity** fan and pump commands and RPM readback have been confirmed on a real cooler at the wire level by a community member ([#30](https://github.com/lewisgibson/FanControl.LianLi/issues/30)). Fan control for the other UNI FAN families is long-standing and well-proven; the newer additions - lighting for the non-Infinity UNI FANs, the TL / GALAHAD II Vision / HydroShift coolers, and the wireless fans - are built to match Lian Li's own L-Connect software byte-for-byte but haven't yet been confirmed on that exact hardware. If you have one, it should just work - please [open an issue](https://github.com/lewisgibson/FanControl.LianLi/issues) if anything looks off.

## Three builds: standard, ARGB, and Lighting

There are three builds. Install **one, not all three**:

### 🌀 Standard - `FanControl.LianLi.dll`

Controls fan speed and reads RPM, and **never touches lighting**. If you drive your fan LEDs with OpenRGB (or any other tool that talks to the controller directly over USB), use this build - it leaves your LEDs entirely to that tool.

### 🌈 ARGB - `FanControl.LianLi.Argb.dll`

Everything the standard build does, and also syncs the fans' lighting to the motherboard's ARGB header at startup (so your motherboard's RGB software drives the fan LEDs). Read [Standard vs ARGB](#standard-vs-argb) before picking this one.

### 🎨 Lighting - `FanControl.LianLi.Lighting.dll`

Everything the standard build does, and also **re-applies a lighting look you designed in L-Connect**, so you can set your colours/effects up once in L-Connect, stop L-Connect, and keep the look - driven by this plugin reading L-Connect's own saved config, with no Lian Li software running. Read [Lighting](#lighting-keep-your-l-connect-look-without-l-connect) before picking this one. It only touches lighting if L-Connect has a saved look (see below); with none it behaves exactly like the standard build.

The standard and ARGB builds advertise different names in FanControl ("Lian Li Uni" vs "Lian Li Uni (ARGB)"), as does the Lighting build ("Lian Li Uni (Lighting)"). Because that name is part of how FanControl identifies the controls, **switching builds re-keys your controls and you will need to re-point your fan curves** (this also makes it obvious which build is loaded). Never leave more than one of these DLLs in the Plugins folder at a time.

## Do not run L-Connect at the same time

Lian Li's own **L-Connect** software drives the same controllers as this plugin, so the two fight each other, and the wireless dongles can only be open in one program at a time. Closing the L-Connect window is not enough: its background service keeps running, and a second service restarts it if it stops. Keep L-Connect installed - the Lighting build and the wireless fans read the looks, radio channel and screen settings you saved in it - but switch its services off:

1. Press the Windows key and R together, type `services.msc`, and press Enter.
2. Find **L-Connect Service Watcher**, double-click it, set **Startup type** to **Disabled**, click **Stop**, then **OK**.
3. Do the same for **L-Connect Service**.
4. Restart FanControl.

To pair something new or change a look later, first stop FanControl: exit it from its tray icon, and in `services.msc` stop **FanControl Service** too, since that is where the plugin runs and it keeps the wireless dongles open. Then set both L-Connect services back to **Manual**, start them, open L-Connect and make the change, close it and disable both L-Connect services again, and start **FanControl Service** and FanControl.

Leaving L-Connect running is the most common cause of erratic fan speeds or lighting with this plugin.

## Install

1. **Download** the zip for the build you want from the [latest release](https://github.com/lewisgibson/FanControl.LianLi/releases/latest) (`FanControl.LianLi-vX.Y.Z.zip` for standard, `FanControl.LianLi-Argb-vX.Y.Z.zip` for ARGB, or `FanControl.LianLi-Lighting-vX.Y.Z.zip` for Lighting) and extract the `.dll`.

2. **Unblock the DLL.** Right-click the extracted `.dll`, choose **Properties**, tick **Unblock** at the bottom, then **OK**. Windows marks files downloaded from the internet as blocked, and FanControl silently ignores a blocked plugin.

3. **Install it in FanControl.** Open the menu and click **Install plugin**, then pick the `.dll`. It loads immediately - no restart needed.

That single DLL is all you need - it talks to the controllers through Windows' own USB libraries and needs nothing else installed beside it. Your Lian Li channels now appear as controls (assign each to a fan curve) and as RPM sensors.

**Upgrading later:** download the newer zip, unblock the `.dll`, and install it through FanControl the same way. Your fan-curve bindings are preserved.

## Standard vs ARGB

Pick **standard** unless you specifically want this plugin to drive ARGB sync.

The ARGB build asserts LED ARGB-header sync at startup, handing the fans' lighting to the motherboard's ARGB header. Controllers that store their lighting in their own memory handle this fine. But some controllers do **not** persist lighting to hardware (for example the **UNI FAN SL-Infinity 120 V1**); on those, asserting this at startup makes the lighting **revert to factory defaults every time the plugin starts** (and whenever it reconnects to the controller after sleep or hibernate). If your lighting keeps resetting, switch to the standard build.

### Troubleshooting

- **The controls do not show up.** Make sure you unblocked the file (step 2) before installing it. Make sure **L-Connect is not running** (see above) - it is the most common conflict; OpenRGB and other tools that open the same controller can clash too.
- **Lighting resets to factory on every boot.** You are on the ARGB build with a controller that does not persist lighting; use the standard build.
- **The fans stopped responding after sleep or hibernate.** Windows re-plugs the controller on the way back, and the plugin now reconnects to it by itself within a few seconds (the log shows `reopened ... after N faulted transfer(s)`). If a version older than this still freezes for you, update; if the current version does not recover, open an issue with the plugin log.
- **FanControl sat on a blank window after a wake.** A controller can come back from sleep in a state where Windows never completes an open on it, and older versions waited on it without a deadline, from the very thread FanControl uses to refresh its sensors after a resume. Every wait on a controller is now bounded, so FanControl stays responsive and the plugin keeps retrying the controller in the background (the log shows what timed out). If you still see it, open an issue with the plugin log.
- **The wireless fans do not show up.** The log shows the dongles being found (`controller wireless master=...`) or why not. If the log says a dongle `has no WinUSB interface registered`, Windows has not bound it to its WinUSB driver (in Device Manager it should sit under Universal Serial Bus devices); the plugin reaches the dongles exactly the way L-Connect does, so if L-Connect can see them, the plugin should too. If the log says `open failed` for them, L-Connect's service is still running and has them open - switch it off as described above. If they open but show `master=not answering yet` or `devices=0`, the dongle hasn't finished starting or nothing is paired to it yet; the plugin keeps listening and adds the fans as soon as they check in.
- **The plugin keeps a small file of its own**, `remembered-controllers.json`, beside its log. It records which controllers and sensors it has seen, so after a reboot your curves stay bound even if a controller is slow to answer. It is safe to delete: the plugin simply relearns your devices on the next start.
- **Submitting a bug?** Include your controller's Name, VID, and PID from Windows Device Manager. The bug-report template walks you through it.

## Lighting: keep your L-Connect look without L-Connect

The **Lighting** build (`FanControl.LianLi.Lighting.dll`) lets you design your fan lighting once in Lian Li's L-Connect, then **stop L-Connect and keep the look** - re-applied for you by this plugin every time it starts, and again after the PC comes back from sleep or hibernate.

This is useful because the Uni controllers do **not** store their lighting in their own memory: the look survives while the PC stays powered, but a full power-off or cold boot resets it to the factory rainbow. Normally you would keep L-Connect running just to re-apply your colours after a reboot - but L-Connect fights this plugin (see [above](#do-not-run-l-connect-at-the-same-time)). The Lighting build removes that trade-off: it reads L-Connect's own saved configuration and re-applies the look itself, so nothing from Lian Li needs to be running.

### How to set it up

1. **Design your lighting in L-Connect** as you normally would (colours, effects, per-fan, inner/outer rings - whatever you like). Apply it so you can see it on your fans; this saves it to L-Connect's configuration.
2. **Stop L-Connect.** Fully exit it and stop its background service/process so it stops driving the controller. Leave its configuration on disk (under `C:\ProgramData\Lian-Li\L-Connect 3`) - that is where your look is stored. There is no import step and no extra file to manage.
3. **Install the Lighting build.** Put `FanControl.LianLi.Lighting.dll` in FanControl's `Plugins` folder and load the plugin.

From then on, the plugin reads L-Connect's saved look and re-applies it every time it starts - including after every reboot - with no Lian Li software running. Change the look in L-Connect (then stop it again) any time; the plugin picks up the new look on its next start.

### Opt-in and fail-safe

The Lighting build only drives lighting when it can do so **exactly**:

- **No L-Connect configuration?** (L-Connect was never installed, or its config is gone.) It behaves exactly like the standard build - fan control only, lighting left untouched. The lighting feature is entirely opt-in.
- **A device it doesn't recognise?** Anything not in the [supported list](#supported-devices) is **left untouched** - the plugin never sends a guess, so an unknown or unsupported device keeps whatever lighting it had rather than getting wrong lighting.
- **A corrupt configuration?** It is logged and the lighting feature is disabled; **fan control is never affected**.

### Which build for which lighting setup

- **You design lighting in L-Connect and want to keep it without running L-Connect** -> **Lighting** build (this feature).
- **You use OpenRGB, SignalRGB, or another tool that drives the fan LEDs directly over USB** -> **standard** build. The standard build never touches lighting, so it stays out of the way of whatever you use. Do **not** use the Lighting build alongside another lighting tool - both would try to own the LEDs and fight, exactly like running L-Connect.
- **You want the motherboard's ARGB header to drive the fan LEDs** -> **ARGB** build.

Lighting replay is implemented for every device in the [supported list](#supported-devices). The **UNI FAN SL-Infinity** is verified on real hardware; the other families and the AIO coolers are reproduced byte-for-byte from L-Connect's own configuration and are being confirmed by the community. Anything not on that list is left untouched. See [docs/lighting.md](docs/lighting.md) for the wire protocol and how it works.

## Build from source

You need the .NET 9 SDK on Windows. The plugin targets `netstandard2.0`.

```
dotnet build -c Release
```

The built DLL is at `src/FanControl.LianLi/bin/Release/netstandard2.0/FanControl.LianLi.dll`. To build a variant instead:

- **ARGB:** add `-p:EnableArgb=true -p:AssemblyName=FanControl.LianLi.Argb`, which produces `FanControl.LianLi.Argb.dll`.
- **Lighting:** add `-p:EnableLighting=true -p:AssemblyName=FanControl.LianLi.Lighting`, which produces `FanControl.LianLi.Lighting.dll`.

The variants are mutually exclusive - never combine `EnableArgb` and `EnableLighting`.

See [CONTRIBUTING.md](CONTRIBUTING.md) for the full build/test loop and [docs/](docs/) for the architecture, the device protocol, and deployment notes.

## Contributing

Contributions are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT - see [LICENSE](LICENSE). The plugin ships no third-party components; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Acknowledgements

The Lian Li Uni wire protocol was learned from prior open-source work. This is a clean-room reimplementation that reuses only the protocol facts (report ids, byte offsets, duty formulas), not any third-party source code:

- [uni-sync](https://github.com/EightB1ts/uni-sync) by Cameron Halter - the Rust sync tool the protocol facts were taken from (MIT).
- [FanControl.LianLi](https://github.com/EightB1ts/FanControl.LianLi) by Cameron Halter - the original FanControl plugin that inspired this one (LGPL-2.1).
- [liquidctl](https://github.com/liquidctl/liquidctl) - used as a protocol reference (GPL-3.0-or-later).
