# Protocol

This document is the byte-level contract for the Lian Li Uni controllers the plugin drives. All controllers share vendor id `0x0CF2`. Every report begins with byte `224` (`0xE0`), which is the HID report id. The pure encoder strategies in the Protocol layer implement exactly what is described here; the unit tests assert these bytes directly. The byte-level facts match Lian Li L-Connect 3, verified against its decompiled device classes (`LConnectCore.Products.Ene6K77Fan`) and per-product controllers.

## Report types

Every fan-control and config write is a **feature report** (`SET_REPORT(Feature)` / `HidD_SetFeature`): set-speed, manual-mode, the RPM primer, and ARGB sync. Only lighting **colour** data is an output report (`Write`). This matches L-Connect, which sends all of the above through `sendFeatureReport` and only colours through `sendOutputReport`. The byte sequences below are the meaningful prefix; the transport pads each feature report up to the device's feature-report length (which `HidD_SetFeature` requires) and waits 20ms after each write (L-Connect's `writeDelayTime`).

## Device matrix

| Family | PID(s) | Set-speed report | Duty->byte | Manual-mode report | RPM offset |
| --- | --- | --- | --- | --- | --- |
| Uni Hub 0x7750 (SL) | 0x7750 | {224, 32+ch, 0, B} | raw | {224, 16, 49, 0x10<<ch, 0, 0} | 1 |
| Uni SL 0xA100 (SL) | 0xA100 | {224, 32+ch, 0, B} | raw | {224, 16, 49, 0x10<<ch, 0, 0} | 1 |
| Uni AL 0xA101 (AL) | 0xA101 | {224, 32+ch, 0, B} | raw | {224, 16, 66, 0x10<<ch, 0, 0} | 1 |
| Uni SL Redragon 0xA106 (SL) | 0xA106 | {224, 32+ch, 0, B} | raw | {224, 16, 49, 0x10<<ch, 0, 0} | 1 |
| Uni SL-Infinity 0xA102 (SLI) | 0xA102 | {224, 32+ch, 0, B} | floored | {224, 16, 98, 0x10<<ch, 0, 0} | 1 |
| Uni SL v2 0xA103/0xA105 (SLV2) | 0xA103, 0xA105 | {224, 32+ch, 0, B} | floored | {224, 16, 98, 0x10<<ch, 0, 0} | 2 |
| Uni AL v2 0xA104 (ALV2) | 0xA104 | {224, 32+ch, 0, B} | floored | {224, 16, 98, 0x10<<ch, 0, 0} | 2 |

Notation: `ch` is the zero-based channel index, `d` is the requested duty (0 to 100), and `B` is the duty byte. `<<` is a left shift. "raw" and "floored" are the two duty mappings defined below.

## Set-speed report

Every family uses the same four-byte set-speed prefix (a feature report; the transport pads it):

```
{ 224, 32 + ch, 0, B }
```

The third byte is always `0`. Only the duty byte `B` differs by family. The duty `d` is clamped to 0..100.

## Duty-to-byte mapping

L-Connect computes the duty byte in its per-product controller and sends it straight to `SetFanSpeed`. There are two mappings, and they differ by family - do not unify them:

- **Raw (v1: SL, AL, Redragon).** `B = clamp(d, 0, 100)`. The percent is sent as-is with no spin floor; `d = 0` sends byte `0`. This matches `SLFanController`/`ALFanController`, which send `CalculateSpeed(...)` (a `Math.Max(speed, 0)` value) directly to `SetFanSpeed`.
- **Floored (v2/SL-Infinity: SLI, SLV2, ALV2).** `B = (d == 0) ? 1 : max(10, d)`. So `0 -> 1`, `1..9 -> 10`, `10..100 -> d`. This matches `SLInfinityController`/`SLV2FanController`/`ALV2FanController`, which compute `num == 0 ? 1 : Math.Max(10, round(rpm / MaxSpeed * 100))`; since FanControl already supplies a duty percent, that reduces to `max(10, d)` with `1` for off.

Whether the fan actually reaches 0 rpm is up to the controller firmware. On the **SL-Infinity** (`0xA102`) the firmware clamps a sub-floor byte up to its ~210 rpm minimum and does not truly stop under host duty control (verified on hardware); L-Connect has the same limitation on this family, because host duty control is software-driven, not a firmware start-stop mode.

## Manual-mode report (take a channel off motherboard sync)

To put a channel under host (software) control instead of motherboard PWM sync, write the feature report:

```
{ 224, 16, reg, 0x10 << ch, 0, 0 }
```

This is L-Connect's `SetFanMotherboardSync(ch, isSync: false)`: byte 3 selects the channel in the high nibble (`1 << (ch+4)`, equivalently `0x10 << ch`) and leaves the sync bit clear. The per-family register byte `reg`:

- SL / Redragon: `49`
- AL: `66`
- SLI / SLV2 / ALV2: `98`

## RPM primer

The Uni controllers are request-response: a `HidD_GetInputReport` returns a stale idle buffer until the device is asked to refresh it. Before every RPM read, send the feature report:

```
{ 224, 80, 0 }
```

`0x50` (80) is the device's "prepare an input report" command (`0x00` selects RPM; `0x01` selects firmware version). L-Connect's `GetFanSpeed` sends this before every read for the whole family. Some SL-Infinity revisions return live RPM without it; others return 0/garbage until primed, so it is always sent.

## ARGB-sync report (ARGB build only)

ARGB sync is compiled in only when the plugin is built with the `ENABLE_ARGB` symbol (`-p:EnableArgb=true`), which ships as the separate `FanControl.LianLi.Argb.dll`. The standard build never emits it. When present, the controller is told once at startup to take its LED lighting from the motherboard's ARGB header. It is asserted with the feature report:

```
{ 224, 16, argbReg, 1, 0, 0, 0 }
```

with the per-family ARGB register byte `argbReg`:

- SL: `48`
- AL: `65`
- SLI / SLV2 / ALV2: `97`

Caveat: on controllers that do not persist lighting to hardware (e.g. UNI FAN SL-Infinity 120 V1), asserting this at every startup resets their lighting to factory defaults. That is why it is a separate, opt-in build rather than always on.

## RPM decode

RPM telemetry arrives in an input report with id `224` and length `65`. Each channel's tachometer is a 16-bit big-endian value:

```
rpm[ch] = (buf[off + ch*2] << 8) | buf[off + ch*2 + 1]
```

The base offset `off` into the buffer is family-dependent:

- SL / AL / SLI: `off = 1`
- SLV2 / ALV2: `off = 2`

## RPM validation

A decoded RPM is trusted only when it is `0..6000`. After a hibernate/power cycle the SL-Infinity returns its idle-state input buffer, which decodes to ~50000 rpm, and a read that races USB re-enumeration can return a partial buffer; the Uni fans top out around 2100 rpm (SL-Infinity) and no family exceeds ~3000, so any larger value is garbage. An implausible read is **ignored**: the previous good value for that channel is kept, and the onset is logged once (and recovery once), so a persistent garbage read is visible without spamming the log. The bound lives in the pure `ChannelReadDecision` so it is testable in isolation.

## Channel byte

The manual-mode channel selector byte is `0x10 << ch`, so:

- ch0 -> `0x10`
- ch1 -> `0x20`
- ch2 -> `0x40`
- ch3 -> `0x80`

## The 0x0416 coolers: Galahad II Trinity

The Galahad II Trinity (vendor `0x0416`, pid `0x7371` Performance / `0x7373` Regular) does not speak the `0xE0` feature-report protocol above. It uses the 64-byte command packet shared with the Uni Fan TL (report id `0x01`, on the interface whose usage page is `0xFF1B`), written as an output report and answered on interrupt-IN. The commands the plugin uses, verified against L-Connect's `Galahad2TrinityDevice` (decompiled) and confirmed on a real `0x7373` over raw hidraw by a community member in [issue #30](https://github.com/lewisgibson/FanControl.LianLi/issues/30):

- **Handshake `0x81`**: the reply's 4-byte payload is the fan RPM (big-endian, bytes 0-1) then the pump RPM (bytes 2-3).
- **Set fan `0x8B [sync, duty]`** and **set pump `0x8A [sync, duty]`**: `duty` is the percent 1:1 (L-Connect clamps the top at 100 and nothing else); `sync` is L-Connect's `mbSync` flag. The plugin always sends `sync = 0` and drives the duty itself.

What the hardware confirmation added, and what the plugin does with it:

- The `sync` byte on the **fan** command is inert on the `0x7373`: the fans stay on the commanded duty whatever the motherboard PWM does. On the **pump** it works, handing the pump to the CPU_FAN header's PWM. The plugin never sets it on either channel, so this changes nothing today - it is recorded so nobody later exposes the fan channel as a motherboard-curve mode.
- The pump's real range on the Regular is about 2200-3200 rpm (L-Connect's own `PumpRPMMinRegular`/`PumpRPMMaxRegular`; the Performance is 2200-4200). The firmware accepts duty down to 0 (L-Connect's `PumpPWMMin`) and floors the speed at its minimum, so the plugin's `PumpDutyFloor = 50` (about 2600 rpm) is deliberately conservative rather than a hardware limit.
- L-Connect's fan slider floors at 10% (`FanPWMMin`), and the plugin does not: a 0% curve point sends duty 0, which spins the radiator fans down to roughly 250-350 rpm rather than stopping them.
- Commanded fan and pump duty **persist in the controller across a full power cut**, and there is no autonomous thermal failsafe: with nothing driving it, the cooler holds whatever duty it was last given, however hot the CPU gets. That is why the plugin keeps re-asserting the duty on the keepalive cadence rather than writing once.

## Recorded upstream bugs we deliberately avoid

These are real defects observed in upstream and forked implementations. They are listed here so the encoders are never "simplified" back into them.

1. **Channel byte must be `0x10 << ch`.** Some code computes the channel selector as `(2 * ch) * 16`, which for ch3 yields `0x60` instead of the correct `0x80`. That silently addresses the wrong channel. The selector is a shift of a single bit, not an arithmetic scaling: ch3 is `0x80`.
2. **Fan control is a feature report, not an output report.** The earlier plugin sent set-speed and manual-mode as output reports (`Write`). L-Connect sends them as feature reports, and the device interprets the two report types differently (most starkly at the bottom of the range). Send fan-control commands through `SetFeature`, never `Write`.
3. **Do not invent a duty curve.** Earlier code ran the duty through per-family formulas like `(800 + 11*d)/19`. L-Connect sends the duty raw (v1) or floored at 10 (v2/SLI); it does not scale it. Use the two mappings above, not a formula.

## Sources

The byte-level facts above were learned and cross-checked against Lian Li L-Connect 3 (decompiled, for protocol facts only) and prior open-source implementations of the same protocol. Only the protocol facts (report ids, offsets, duty rules) were reused; no source code was copied.

- L-Connect 3 - Lian Li's own application; decompiled device/controller classes used to verify the byte-level protocol.
- uni-sync (https://github.com/EightB1ts/uni-sync) - Rust, MIT.
- FanControl.LianLi (https://github.com/EightB1ts/FanControl.LianLi) - the original FanControl plugin, LGPL-2.1.
- liquidctl (https://github.com/liquidctl/liquidctl) - GPL-3.0-or-later.

## Out of scope

These Lian Li products are intentionally NOT in this plugin's catalog, confirmed against a full decompile of L-Connect 3. They are unreachable or have no fan/pump/RGB surface over plain USB HID:

- **The entire wireless family** - Uni Fan SL V3, TL V2, SL-Infinity Wireless, CL, Lancool 217 Infinity, and the wireless Galahad/HydroShift II AIOs. Their fans, pump, and lighting are reachable only over RF, MAC-keyed, after pairing through the wireless dongle - not over USB HID.
- **LCD screen render** - the screens on the Uni Fan TL LCD (`0x7393`), the Universal 8.8-inch panel (a WinUSB device, not HID), and the HydroShift II / Lancool 207 displays. On the coolers that also have a screen (Galahad II Vision, HydroShift LCD) the plugin drives the fans, pump, and RGB and simply leaves the screen alone.

Note: the Strimer Plus (`0xA200`) and the 0x0416 fan/pump coolers (Uni Fan TL, Galahad II Trinity/Vision, HydroShift LCD) **are** supported - see [the supported-devices list](../README.md#supported-devices).
