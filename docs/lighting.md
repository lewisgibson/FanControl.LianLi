# Lighting replay (the `Lighting` build)

This document describes the **Lighting** build variant (`FanControl.LianLi.Lighting.dll`): what it does, how it works, the SL-Infinity wire protocol it reproduces, and how it stays fail-safe. For the user-facing summary see the [Lighting section of the README](../README.md#lighting-keep-your-l-connect-look-without-l-connect).

## What it is for

The Lian Li Uni controllers do **not** persist their LED state to onboard flash. The look you set survives while the PC is powered, but a power-off or cold boot reverts the fans to the factory rainbow. The usual way to restore it is to leave L-Connect installed so it re-applies your profile on boot - but L-Connect drives the same USB controller as this plugin and the two fight, which is why the plugin otherwise asks you to remove it.

The Lighting build resolves that tension: it reads the look you already designed in L-Connect and **re-applies it itself at startup**, and again whenever it reconnects to a controller that came back from sleep or hibernate. You design once in L-Connect, stop L-Connect so it is no longer fighting for the controller, and this plugin keeps the look across every reboot.

## How it works

The plugin reads L-Connect's **own saved configuration directly** - there is no import step, no capture, and no intermediary file:

```
C:\ProgramData\Lian-Li\L-Connect 3\device   --plugin (on startup)-->   controllers
(L-Connect's gzipped JSON, read-only)        re-applies the saved look
```

On startup the Lighting build:

1. Reads L-Connect's per-device settings from `C:\ProgramData\Lian-Li\L-Connect 3\device` (each setting is a gzipped JSON file holding `{ DeviceID, Type, Data }`). It only reads; it never writes to L-Connect's files.
2. Groups the `LightingPort*` and `FanQuantity` settings by the USB instance token in each `DeviceID`.
3. For each controller FanControl locates, matches it to a saved look by that instance token, and - for an SL-Infinity controller - encodes the exact HID transfers L-Connect itself would send and writes them to the device before fan setup.

All of the translation between L-Connect's saved settings and the controller (mode and colour encoding, fan-quantity, frame latch, apply order) lives in a single pure C# encoder, `Protocol/SlInfinityLightingEncoder.cs`, with the test suite asserting its output. Nothing about it is offline or external: build the DLL, drop it in, done.

## Keeping a look

There is no capture or import step. Design your lighting in L-Connect and apply it (so L-Connect writes its config), then **stop L-Connect** - fully exit it and stop its background service/process so it stops driving the controller. Install the Lighting build and it reads L-Connect's saved config and re-applies the look on every start.

L-Connect's config lives under `C:\ProgramData\Lian-Li\L-Connect 3`; the plugin needs it to remain on disk. You can stop L-Connect from running, but do not delete its configuration - that is where the look is stored.

## Fail-safe behaviour

Lighting is driven only when it can be done exactly. `LConnectConfigReader`, the per-family lighting encoders, and the plugin together guarantee:

- **No L-Connect configuration** (the directory is absent, e.g. L-Connect was never installed) -> no lighting is driven; the build behaves exactly like the standard build. This is the opt-out path and the default.
- **A located controller with no matching saved look** -> that controller is left untouched.
- **A controller of an unsupported family** (anything other than SL-Infinity, PID 0xA102) -> skipped and logged; the plugin never drives unverified bytes (see [Status](#status)).
- **A port whose mode L-Connect itself does not apply** -> that port is left alone while the others still apply, exactly as L-Connect behaves.
- **An unreadable/corrupt configuration** -> logged, and lighting is disabled rather than apply a partial look. **Fan control is never affected** in any of these cases.

The plugin logs what it did at startup (`Lighting: read N L-Connect controller look(s)` and `lighting applied for <token> (N writes)`), so the log file shows exactly which controllers got a look.

## Important caveats

- **Distinct plugin name re-keys your controls.** The Lighting build advertises itself to FanControl as `Lian Li Uni (Lighting)` (so you can tell which build is loaded). Because FanControl folds the plugin name into each control's binding key, switching to or from this build re-keys the controls and your fan-curve bindings must be re-pointed. If you are migrating an existing config and want to keep the bindings, remap the identifier prefix in `userConfig.json` from `Lian Li Uni/` to `Lian Li Uni (Lighting)/` (back the file up first).
- **Do not run another lighting tool at the same time.** The Lighting build owns the LEDs at startup; running L-Connect, OpenRGB, SignalRGB, etc. alongside it re-introduces the two-writers conflict. If you use one of those, use the **standard** build instead, which never touches lighting.
- **One DLL at a time.** Never leave more than one `FanControl.LianLi*.dll` in the Plugins folder.

## Status

- **Uni SL-Infinity (`0xA102`)** - supported and tested end to end on real hardware.
- **Uni SL (`0xA100`), AL (`0xA101`), SL v2 (`0xA103`/`0xA105`), AL v2 (`0xA104`), Redragon (`0xA106`)** - supported via `UniFanLightingEncoder`, a parameterised encoder driven by a per-family profile (apply order, quantity register/packing, mode->wire table, colour-expansion model). The tables were extracted from L-Connect's own controllers and are byte-tested and adversarially cross-checked against the decompile, but not yet confirmed on that exact hardware.
- **Strimer Plus (`0xA200`), Uni Fan TL (`0x7372`), Galahad II Trinity (`0x7371`/`0x7373`) & Vision (`0x7391`/`0x7395`), HydroShift LCD (`0x7398`-`0x739A`)** - supported by their own encoders; reproduced from L-Connect and awaiting community confirmation on hardware.
- Anything not listed is left untouched (the plugin never sends unverified lighting).

## Layering

The lighting code is gated behind the `ENABLE_LIGHTING` compile symbol (the standard and ARGB builds contain none of it) and lives in:

- `Hid/IHidTransport.SetFeature` + `Hid/HidSharpTransport` - the feature-report write capability (HID `SetFeature` / `HidD_SetFeature`). This seam method is the one piece that is not gated (a harmless unused capability in the other builds).
- `Protocol/RgbColor`, `Protocol/LightingPortState`, `Protocol/LightingTransfer` - the value types the encoder consumes and produces.
- `Protocol/SlInfinityLightingEncoder` - the pure encoder: saved per-port look in, exact HID transfers out. Byte-tested.
- `Devices/JsonValue` - a tiny dependency-free JSON reader (the plugin ships a single DLL and cannot take a JSON NuGet dependency on netstandard2.0).
- `Devices/LConnectConfigReader` + `Devices/LConnectControllerConfig` - read L-Connect's config directory and group it per controller.
- `Devices/LightingReplay` - writes the encoded transfers in order, paced like L-Connect.
- `Plugin/LianLiPlugin` - reads the config and applies a matching look during `Initialize`, before fan setup, and registers the same apply as each controller's reconnect replay so a controller that was re-enumerated (and possibly reset) across sleep or hibernate gets its look back on the next tick.

It respects the same rules as the rest of the plugin (see [`.claude/rules/`](../.claude/rules/) and [`architecture.md`](architecture.md)): the encoder is pure, HidSharp stays confined to `Hid/`, and the feature is invisible in the standard and ARGB builds.
