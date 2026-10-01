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

## ARGB-sync report (ARGB build, and a synced controller on the Lighting build)

ARGB sync is compiled in when the plugin is built with the `ENABLE_ARGB` symbol (`-p:EnableArgb=true`), which ships as the separate `FanControl.LianLi.Argb.dll`, where every Uni controller is told once at startup (and again after a reconnect) to take its LED lighting from the motherboard's ARGB header. The Lighting build sends the same report to a Uni controller whose per-controller L-Connect "sync to motherboard" switch is on, in place of that controller's saved look, as L-Connect 2.1.29 does at its start and after a resume (see [lighting.md](lighting.md#motherboard-argb-sync-the-l-connect-switch)). The standard build never emits it. It is asserted with the feature report:

```
{ 224, 16, argbReg, 1, 0, 0, 0 }
```

with the per-family ARGB register byte `argbReg`:

- SL: `48`
- AL: `65`
- SLI / SLV2 / ALV2: `97`

Caveat: on controllers that do not persist lighting to hardware (e.g. UNI FAN SL-Infinity 120 V1), asserting this at every startup resets their lighting to factory defaults. That is why it is a separate, opt-in build rather than always on, and why the Lighting build sends it only for a controller the user has switched to the motherboard in L-Connect.

## Merge-mode and merge-order reports (Lighting build)

The Lighting build sends these when it replays a saved look (see [lighting.md](lighting.md#merge-effects-on-the-other-uni-families)); the standard and ARGB builds never do. They are feature reports like every other Uni write. Each family's merge effects are distinct effect bytes in its own table; these reports are what surrounds them:

- SL / Redragon `StartMerge`, sent before port 0 in merge mode: `{ 224, 16, 51, 0, 1, 2, 3, 8 }` (8 bytes, `SLFanDevice.StartMerge`). Its `StopMerge` is `{ 224, 16, 52, 0, 0, 0 }` and is never sent at a start or a resume.
- AL merge command, sent before port 0 in merge mode: `{ 224, 16, 67, 1, 0, 0 }` (`ALFanDevice.SendMergeCommand(true)`; `0` in byte 3 leaves merge mode).
- SLI / SLV2 / ALV2 merge order, sent after the look and its frame at every start and reconnect: `{ 224, 16, 99, o0, o1, o2, o3, 8 }` (8 bytes, `SetMergeOrder`), the saved `MergeOrder` when its four entries are each 0 to 4, else `0, 1, 2, 3`. L-Connect's `Init` and `ResumeSuspend` write the default `0, 1, 2, 3` after the fan quantity, and the saved order reaches the register only at its startup, written inside `ApplyAll`'s loop over the saved settings (in the settings files' hash order, with the saved look written once more after the loop), and on a user change; the plugin sends the saved order, where it goes, and why it keeps it on a reconnect where L-Connect's resume resets it, are in [lighting.md](lighting.md#merge-effects-on-the-other-uni-families).
- The effect-only blank the SLI (ports 7 to 1, before port 0) and the ALV2 (ports 1 to 7, after port 0) write in merge mode: `{ 224, 16 + port, rainbow, 0, 0, 8 }`, where `rainbow` is the family's own Rainbow byte (`0x32` on the SLI, `43` on the ALV2) and `8` is brightness Off. No colour report accompanies it.

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
- Lighting (Lighting build only) rides on the same packets: **set fan light `0x85`** with a 20-byte payload (mode, brightness, speed, four R,G,B colours, direction, disabled, ARGB source, sync-to-pump, LED count) and **set pump light `0x83`** with a 19-byte payload (scope, mode, brightness, speed, four colours, direction, disabled, ARGB source), verified against `FanLightingSetting.ToBytes` and `PumpLightingSetting.ToBytes`. The Galahad II Vision (`0x7391`/`0x7395`) and the HydroShift LCD (`0x7398`/`0x7399`/`0x739A`) use the same two commands with the same layout: the HydroShift LCD takes only the fan light, and the Vision's `0x83` drives the ring of LEDs around its screen rather than a pump cap (its scope byte is always 0). What each build writes, and from which saved setting, is in [lighting.md](lighting.md#galahad-ii-vision-and-hydroshift-lcd).
- The pump's real range on the Regular is about 2200-3200 rpm (L-Connect's own `PumpRPMMinRegular`/`PumpRPMMaxRegular`; the Performance is 2200-4200). The firmware accepts duty down to 0 (L-Connect's `PumpPWMMin`) and floors the speed at its minimum, so the plugin's `PumpDutyFloor = 50` (about 2600 rpm) is deliberately conservative rather than a hardware limit.
- L-Connect's fan slider floors at 10% (`FanPWMMin`), and the plugin does not: a 0% curve point sends duty 0, which spins the radiator fans down to roughly 250-350 rpm rather than stopping them.
- Commanded fan and pump duty **persist in the controller across a full power cut**, and there is no autonomous thermal failsafe: with nothing driving it, the cooler holds whatever duty it was last given, however hot the CPU gets. That is why the plugin keeps re-asserting the duty on the keepalive cadence rather than writing once.

## The HydroShift II OLED Curve pump: a WinUSB device

The HydroShift II OLED Curve is a composite product: its OLED screen (`0x1CBE:0xA068`) and display-mode interface (`0x1A86:0xAD23`) are left alone, and its pump rides on the cooler's lighting MCU, vendor `0x0416`, pid `0x8051` (`0x8052` is that MCU's bootloader and is never opened). Windows binds the MCU to WinUSB, so it is located and opened the way the wireless dongles are (see `wireless.md`) and driven through the same transport: an OUT pipe `0x01` and an interrupt-IN pipe `0x81`. Every fact below is from L-Connect 2.1.29's `lianli.lcd207.dll` (`WinUsbHS2` and `HS2Controller`, decompiled) and the service's `HydroShiftIIOLEDCurveController`; none of it has been confirmed on the hardware yet.

Every command is one **eight-byte packet** whose first byte is the command, and the MCU answers each with one interrupt packet that echoes the command in its first byte and carries the value in the next. L-Connect writes the packet with a 200 ms timeout, reads one reply into a 512-byte buffer with a 200 ms timeout (one transfer) and flushes the pipe (`WinUsbHS2.SendAndReadLed`, then `WinUsb.Read`); the plugin opens the MCU with the same timings (`Transport/WinUsbPipePolicy.PumpMcu`: 200 ms for the write, 200 ms for the reply, one 64-byte packet and no drain, where the dongles keep their 100 ms and 50 ms and a reply read to its end) and reads a reply after **every** write, the set command included, so a reply is never left in the pipe to be read as the next request's. The status and tachometer replies are the data, so the plugin checks that each echoes its command (`0x60`, `0x62`), as L-Connect checks every other reply from this MCU (`WinUsbHS2` compares byte 0 of the motor, version and screen replies to their command) though not these two: a reply echoing another command is a stale one and is ignored, once per run in the log, keeping the last reading rather than take a tachometer's bytes for a temperature. The set-pump and sync replies L-Connect discards (`HS2Controller.SetPumpSpeed` and `SetMBSync` never look at them, and `WinUsb.Read` hands back zeros when none comes), so the plugin reads them to keep the pipe clear but a reply that never comes fails nothing: the command went out, the duty counts as written, and the log says so once until a reply comes.

| Purpose | Packet | Reply | Source |
| --- | --- | --- | --- |
| Liquid temperature and sync state | `{ 0x60, 0, 0, 0, 0, 0, 0, 0 }` | byte 1 the liquid temperature in whole degrees C; byte 2 is `0` while the pump follows the motherboard's PWM header | `WinUsbHS2.GetTemperlate`, `HS2Controller.GetPumpTemperture` and `GetIsSyncMBPwm` |
| Set the pump | `{ 0x61, hi, lo, 0, 0, 0, 0, 0 }` | taken and ignored | `WinUsbHS2.SetPumpSpeed` |
| Pump tachometer | `{ 0x62, 0, 0, 0, 0, 0, 0, 0 }` | `raw = (byte1 << 8) \| byte2` | `WinUsbHS2.GetPumpSpeed` |
| Motherboard sync | `{ 0x64, s, 0, 0, 0, 0, 0, 0 }` | taken and ignored | `WinUsbHS2.SetMBSync`: `s` is `0` to follow the header, `1` for software control |

**The pump is commanded by an output value, not an rpm.** `hi`/`lo` in the set command is the big-endian output value `HS2Controller.GetOutputValue` derives from a target rpm through its 22-point table, listed fastest first as (rpm, output): (2735, 2300) (2703, 2200) (2651, 2100) (2592, 2000) (2537, 1900) (2477, 1800) (2420, 1700) (2365, 1600) (2305, 1500) (2248, 1400) (2188, 1300) (2130, 1200) (2073, 1100) (2013, 1000) (1950, 900) (1895, 800) (1838, 700) (1783, 600) (1725, 500) (1663, 400) (1608, 300) (1577, 250). A target at or above 2735 rpm sends 2300, one at or below 1577 sends 250, and anything between two rows is interpolated linearly and rounded with `Math.Round`, halves to even (2723 rpm, which lands on 2262.5, sends 2262). `HydroShiftCurveProtocol.OutputValueForRpm` is that function, and the tests pin every row and the rounding.

**The reported rpm is corrected.** `HS2Controller.GetPumpSpeed` subtracts 40 from a raw count up to 1800, 50 from one up to 2500 and 30 from anything higher, and never reports below 0; that is the figure L-Connect shows and the one the plugin reports (`DecodePumpSpeed`). A corrected count above the plugin's usual 6000 rpm plausibility bound is ignored and the last good value kept, as for every other family.

**Ranges and the duty mapping.** L-Connect's controller drives the pump between `MinPumpSpeedRPM = 1600` and `MaxPumpSpeedRPM = 2400`: every curve it offers and its fixed-speed mode top out at 2400. Its Turbo mode (`PWMTurbo`, `MaxPumpSpeedTurboRPM = 2800`) is a separate opt-in behind a warning dialog. The plugin spans the ordinary range the way it spans the wireless water blocks' (`wireless.md`): 0% asks for 1600 rpm, 100% for 2400 rpm, linearly between (`PumpRpmFromDuty`), and the Turbo ceiling is never asked for, so a curve at 100% cannot hold the pump in Turbo. There is no way to stop the pump and the plugin does not try.

**Cadence and software control.** L-Connect's pump control timer sends the speed every **2 s** (`pumpSpeedControlInterval`), and its pump info timer reads the temperature and tachometer every second. Nothing is known about how long the pump holds an output value with nothing driving it, so the plugin re-sends the speed every 2 s too (rather than the 15 s the other families refresh at) and polls both readings every tick. L-Connect sends the sync command only when its global motherboard-sync setting is on at startup (`Init`, with `0`) or toggled (`setMotherboardRPMSync`, with the new state); the plugin sends `{ 0x64, 1 }` once when it builds the controller, the write L-Connect makes when the setting is switched off, and again after the transport reconnects. L-Connect reads the state byte of the `0x60` reply but never acts on it; the plugin re-asserts software control once whenever that byte reports the pump back on the header (once per onset, logged, so a pump that keeps reverting is visible without a line every second).

**Left alone.** The MCU also carries the pump-head lighting (`HS2Controller.SetEffect` streams RGB frames from the PC, three 64-byte `0x11` packets per frame, for as long as the effect runs) and the screen motor (`0x50`/`0x51`); the plugin sends none of those in any build, so the Lighting build does not replay this cooler's look. The AIO's fans are on a motherboard header, not on this device.

Unverified without the hardware: the size of the MCU's reply packet (the plugin reads one 64-byte packet with L-Connect's 200 ms wait; L-Connect reads once into 512 bytes), whether a set or sync command is answered at all (a reply that does not come is logged once and fails nothing), whether the status and tachometer replies echo their command as the MCU's other replies do (a reply that does not is ignored and logged, so a firmware that does not echo them would show in the log as every status and tachometer reply ignored), and the meaning of the sync byte beyond L-Connect's own `== 0` test.

## The L-Wireless SYNC dongles: wireless UNI FAN

The wireless UNI FAN range (SL V3, TL V2, SL-Infinity Wireless, CL), the wireless water blocks and the Lancool 217 case fans are not on a USB controller and do not speak either protocol above. They pair over radio with the L-Wireless SYNC controller, whose two dongles are WinUSB devices carrying 64-byte packets, and everything about them - how Windows binds them, the packet and RF payload layers, the device list, the duty and pump maths, and the lighting replay - is documented separately in [`wireless.md`](wireless.md).

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

- **The wireless screens and the configuration surface** - an AIO's screen images, themes and carousels, and binding, unbinding or moving a device between RF channels. The wireless fans, pumps, case fans and lighting replay are driven through the dongles (see above); pairing and screen content stay L-Connect's job.
- **LCD screen render** - the screens on the Uni Fan TL LCD (`0x7393`), the Universal 8.8-inch panel (a WinUSB device, not HID), and the HydroShift II / Lancool 207 displays, including the HydroShift II OLED Curve's OLED, its display-mode interface and its screen motor. On the coolers that also have a screen (Galahad II Vision, HydroShift LCD, HydroShift II OLED Curve) the plugin drives the fans and pump it can reach, and the RGB where it is a saved look (the Vision's screen ring included, in its static modes), and simply leaves the screen alone.

Note: the Strimer Plus (`0xA200`) and the 0x0416 fan/pump coolers (Uni Fan TL, Galahad II Trinity/Vision, HydroShift LCD, the HydroShift II OLED Curve's pump) **are** supported - see [the supported-devices list](../README.md#supported-devices).
