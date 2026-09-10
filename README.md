# FanControl.LianLi

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Latest release](https://img.shields.io/github/v/release/lewisgibson/FanControl.LianLi?sort=semver)](https://github.com/lewisgibson/FanControl.LianLi/releases/latest)

A plugin for [FanControl](https://getfancontrol.com/) that drives Lian Li UNI FAN controllers, STRIMER RGB cables, and GALAHAD II / HydroShift liquid coolers. It turns every fan (and cooler pump) into a control you can put on a temperature curve, reports their RPM, and can keep your L-Connect lighting without running L-Connect. See [Supported devices](#supported-devices).

This is an unofficial, community plugin. It is not affiliated with, authorized by, or endorsed by Lian Li or by the FanControl project.

## Supported devices

The plugin finds your Lian Li gear automatically - you don't need to know any model numbers. Here's what it does for each:

- **Speed control** - every fan (and, on the coolers, the pump) shows up in FanControl as a control you can put on a temperature curve.
- **RPM** - it reports each fan's and pump's live speed.
- **Lighting** - the **Lighting build** re-applies the colours you set up in L-Connect, so your lighting survives a reboot without L-Connect running (see [Lighting](#lighting-keep-your-l-connect-look-without-l-connect) below). The standard and ARGB builds don't touch lighting.

| Device                                  | Speed control | RPM | Lighting |
| --------------------------------------- | :-----------: | :-: | :------: |
| UNI FAN SL (SL120 / SL140)              |      ✅       | ✅  |    ✅    |
| UNI FAN AL                              |      ✅       | ✅  |    ✅    |
| UNI FAN SL-Infinity                     |      ✅       | ✅  |    ✅    |
| UNI FAN SL V2                           |      ✅       | ✅  |    ✅    |
| UNI FAN AL V2                           |      ✅       | ✅  |    ✅    |
| UNI FAN SL (Redragon edition)           |      ✅       | ✅  |    ✅    |
| UNI FAN TL                              |      ✅       | ✅  |    ✅    |
| STRIMER Plus / Plus V2 (RGB PSU cables) |      n/a      | n/a |    ✅    |
| GALAHAD II Trinity (AIO cooler)         | ✅ fan + pump | ✅  |    ✅    |
| GALAHAD II Vision / LCD (AIO cooler)    | ✅ fan + pump | ✅  |    ✅    |
| HydroShift LCD (AIO cooler)             | ✅ fan + pump | ✅  | ✅ fans  |

Extra touches: if you turned on L-Connect's **start/stop (zero-RPM)** switch, the plugin honours it - the fans that support it stop at 0%. On the LCD coolers the plugin drives the fans, pump, and RGB; it does **not** touch the screen. On the UNI FAN controllers the plugin **only shows the channels that actually have a fan plugged in** - it checks each channel at startup and hides the empty ones, so you get one control per real fan instead of four slots with three dead ones. (If a channel is genuinely in use but happens to be stopped at that moment, it may be hidden until it next spins; if detection is inconclusive the plugin shows all four rather than hide anything.)

> **Tested on hardware:** the **UNI FAN SL-Infinity** is verified on real hardware, and the **GALAHAD II Trinity** fan and pump commands and RPM readback have been confirmed on a real cooler at the wire level by a community member ([#30](https://github.com/lewisgibson/FanControl.LianLi/issues/30)). Fan control for the other UNI FAN families is long-standing and well-proven; the newer additions - lighting for the non-Infinity UNI FANs, and the TL / GALAHAD II Vision / HydroShift coolers - are built to match Lian Li's own L-Connect software byte-for-byte but haven't yet been confirmed on that exact hardware. If you have one, it should just work - please [open an issue](https://github.com/lewisgibson/FanControl.LianLi/issues) if anything looks off.

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

Lian Li's own **L-Connect** software drives the same USB controller as this plugin and writes to it aggressively, so the two fight each other. Before using this plugin, **uninstall L-Connect**, or at least fully exit it and stop its background process:

1. Open Task Manager (Ctrl+Shift+Esc).
2. End any task named **L-Connect** or **L ConnectSystem** - the exact name varies by version, so end anything from Lian Li.

Leaving L-Connect running is the most common cause of erratic fan speeds or lighting with this plugin.

## Install

1. **Download** the zip for the build you want from the [latest release](https://github.com/lewisgibson/FanControl.LianLi/releases/latest) (`FanControl.LianLi-vX.Y.Z.zip` for standard, `FanControl.LianLi-Argb-vX.Y.Z.zip` for ARGB, or `FanControl.LianLi-Lighting-vX.Y.Z.zip` for Lighting) and extract the `.dll`.

2. **Unblock the DLL.** Right-click the extracted `.dll`, choose **Properties**, tick **Unblock** at the bottom, then **OK**. Windows marks files downloaded from the internet as blocked, and FanControl silently ignores a blocked plugin.

3. **Install it in FanControl.** Open the menu and click **Install plugin**, then pick the `.dll`. It loads immediately - no restart needed.

That single DLL is all you need - HidSharp ships with FanControl already. Your Lian Li channels now appear as controls (assign each to a fan curve) and as RPM sensors.

**Upgrading later:** download the newer zip, unblock the `.dll`, and install it through FanControl the same way. Your fan-curve bindings are preserved.

## Standard vs ARGB

Pick **standard** unless you specifically want this plugin to drive ARGB sync.

The ARGB build asserts LED ARGB-header sync at startup, handing the fans' lighting to the motherboard's ARGB header. Controllers that store their lighting in their own memory handle this fine. But some controllers do **not** persist lighting to hardware (for example the **UNI FAN SL-Infinity 120 V1**); on those, asserting this at startup makes the lighting **revert to factory defaults every time the plugin starts** (and whenever it reconnects to the controller after sleep or hibernate). If your lighting keeps resetting, switch to the standard build.

### Troubleshooting

- **The controls do not show up.** Make sure you unblocked the file (step 2) before installing it. Make sure **L-Connect is not running** (see above) - it is the most common conflict; OpenRGB and other tools that open the same controller can clash too.
- **Lighting resets to factory on every boot.** You are on the ARGB build with a controller that does not persist lighting; use the standard build.
- **The fans stopped responding after sleep or hibernate.** Windows re-plugs the controller on the way back, and the plugin now reconnects to it by itself within a few seconds (the log shows `reopened ... after N faulted transfer(s)`). If a version older than this still freezes for you, update; if the current version does not recover, open an issue with the plugin log.
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

MIT - see [LICENSE](LICENSE). The plugin uses [HidSharp](https://www.nuget.org/packages/HidSharp/) (Apache-2.0) at runtime, provided by the FanControl host; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Acknowledgements

The Lian Li Uni wire protocol was learned from prior open-source work. This is a clean-room reimplementation that reuses only the protocol facts (report ids, byte offsets, duty formulas), not any third-party source code:

- [uni-sync](https://github.com/EightB1ts/uni-sync) by Cameron Halter - the Rust sync tool the protocol facts were taken from (MIT).
- [FanControl.LianLi](https://github.com/EightB1ts/FanControl.LianLi) by Cameron Halter - the original FanControl plugin that inspired this one (LGPL-2.1).
- [liquidctl](https://github.com/liquidctl/liquidctl) - used as a protocol reference (GPL-3.0-or-later).
