# The L-Wireless SYNC controller

This document describes the Lian Li **L-Wireless SYNC** controller end to end: how Windows sees it, how L-Connect talks to it, every wire format the plugin reproduces, what the plugin does each second and why, and what we haven't supported. It is the reference for `Transport/WinUsbTransport`, `Protocol/Wireless*` and `Devices/Wireless*`, and it is the place to correct if hardware ever disagrees with what is written here.

Everything below was read out of Lian Li's own binaries (L-Connect 3, assembly version 2.1.11): `slv3.dll` (`MasterDevice`, `RfDevice`, `RFController`, `WinUsb`, `RgbEffect`, `Util`), `slv3.models.dll` (`DevTypes`, `AioParams`), `L-Connect.Core.dll` (`LWirelessDevice`, `NumberHelper`, `LWirelessPumpConfig`), `L-Connect-Service.exe` (`LWirelessController`), and `LibUsbDotNet.LibUsbDotNet.dll` (`WinUsbRegistry`, `WinUsbDevice`), decompiled with `ilspycmd`. Only protocol facts were taken; no code was copied. Citations are `Class.Method`. **None of it has been confirmed against wireless hardware yet** - the maintainer owns no wireless kit - so every section is a description of what L-Connect does, not of what a fan was observed doing.

## The shape of the system

A wireless UNI FAN group is not on a USB controller at all. The fans carry their own radio; a group of one to four fans is a single RF device with a 6-byte address, and it is **bound** (paired) to a master. The master is the L-Wireless SYNC controller, which plugs into USB as **two separate devices**:

| Role        | VID:PID       | Also accepted | L-Connect name |
| ----------- | ------------- | ------------- | -------------- |
| Transmitter | `0416`:`8040` | `1A86`:`E304` | `SLV3TX`       |
| Receiver    | `0416`:`8041` | `1A86`:`E305` | `SLV3RX`       |

The split is strict and it drives the plugin's design:

- **The transmitter carries every command out.** Fan speeds, pump parameters, lighting, the save-to-flash broadcast, the clock pulse: all of it is written to the transmitter, which puts it on the air.
- **The receiver is the only way anything comes back.** It keeps a table of every device it can hear - including other people's masters - and hands the whole table over when asked. There is no per-device query and no unsolicited notification; polling that list is the only telemetry there is.

So one wireless controller in the plugin owns **both** dongles, and neither is useful alone. L-Connect drives exactly one pair - the first transmitter and the first receiver it finds (`WinUsb.GetRFSender`, `GetRFReciver`) - and the plugin drives one pair too: the first transmitter and receiver Windows puts in the same physical device (their container), so two kits plugged in are never crossed, and only when no two share one, the first of each in device-path order (L-Connect takes the first of each Windows enumerates). It logs any further dongle it leaves alone.

Bound devices are not limited to fan groups. The same master carries Strimer light strips, wireless water blocks (HydroShift II), the Lancool 217 Infinity case fans and the V150, and the plugin reads every one of them out of the same device list.

## Opening the dongles: WinUSB, not HID

Neither dongle is a HID device. Windows binds them to **WinUSB**, so a HID enumeration cannot see them at all - which is exactly why the plugin used to report `scan located 0 HID interface(s)` on a machine full of wireless fans.

Reaching a WinUSB device is not the same as reaching a HID one, and getting this wrong is the single easiest way to break wireless support:

1. **The generic USB device interface is not the door L-Connect uses.** Every USB device registers `GUID_DEVINTERFACE_USB_DEVICE` through the hub driver. For a device whose function driver is WinUSB a handle opened on that path may well reach WinUSB too (libusb opens devices that way), but it is not what L-Connect relies on, so the plugin does not either.
2. **The right door is the interface GUID the device's own driver registered.** A WinUSB INF writes it into the device's hardware key (`...\Device Parameters\DeviceInterfaceGUIDs`, a `REG_MULTI_SZ`, or `DeviceInterfaceGuid` as a single string on older packages). LibUsbDotNet - which is what L-Connect uses - reads exactly that value, enumerates interfaces of that GUID, and opens the resulting symbolic link.

The plugin does the same, through `cfgmgr32`:

1. Enumerate present device instances under the `USB` enumerator whose instance id carries a dongle's vendor and product id.
2. Open each instance's hardware key and read `DeviceInterfaceGUIDs` (falling back to `DeviceInterfaceGuid`).
3. For each GUID, list that interface class **filtered to this device instance**, which yields the path to open.
4. `CreateFile` on that path with overlapped access - WinUSB requires it even for synchronous calls - then `WinUsb_Initialize`.

A device whose hardware key holds no interface GUID is not WinUSB-bound and is skipped: that is the case where Lian Li's driver was never installed, and the honest outcome is "no wireless controller found" rather than a failed open.

### Transfers

Each dongle has one interrupt endpoint pair, OUT `0x01` and IN `0x81`, and every packet in both directions is exactly **64 bytes**. There is no report id and no HID framing.

- A **write** is one OUT transfer of one packet.
- A **read** is however many IN packets the dongle has queued. The end of a reply is simply the first transfer that times out, so the transport gathers packets until a timeout, as `WinUsb.ReadAll` does: it drains the reply to its end even past the length it asked for, keeps that length, and zero-pads a reply that fell short. Anything beyond the length is discarded and logged, once per run of such reads. The drain stops after eight packets past the length asked for - more than a whole 434-byte list page - so a dongle that never goes quiet cannot hold a read open.
- The IN pipe is flushed **before a request is written**, not before the reply is read, so nothing is dropped from the reply about to arrive. The flush only discards what WinUSB has already taken off the pipe; packets still waiting in the dongle's own endpoint are untouched, and it is the draining read that keeps those out of the next exchange. A flush that fails is logged, once per run of failures, and the write goes ahead.

A failed or short write faults the handle, which the transport reopens on its backoff, bumping its `Generation` - as `WinUsb.RfSend` closes and reopens the device on any failed write. A read does not: a reply that does not come within the pipe timeout fails that read and nothing more, as `WinUsb.ReadAll` hands back zeros and `MasterDevice` ignores them. Only a read whose native call hangs past its own bound faults the handle. The details are the transport's and are in [architecture.md](architecture.md). The one recovery step that belongs to the wireless layer is the **cross-reset** (`Devices/WirelessDonglePair`): `WinUsb.RfSend` counts failed writes per dongle, and after five in a row resets that dongle through the _other_ one - `RFController.ResetTx` sends the reset through the receiver, `ResetRx` through the transmitter (`MasterDevice.ResetTx`, `ResetRx`). The count restarts after the reset and on any successful write, and the reset is itself a write to the partner, counted like any other. A reset that fails is logged; the write that triggered it still fails to its caller.

## Layer 1: the dongle packet

The first byte of a 64-byte packet is the command.

| Command | Direction | Meaning |
| --- | --- | --- |
| `0x10` | to TX | Carry one 60-byte chunk of an RF payload. Bytes 1-3 are chunk index, channel, receiver slot. |
| `0x10` | to RX | Give me your device list. Byte 1 is how many pages of ten records to send. |
| `0x11` | to TX | Report your own RF address and clock, and work on the channel in byte 1. |
| `0x15` | to either | Reset. L-Connect writes it to the receiver to reset the transmitter (`ResetTx`) and to the transmitter to reset the receiver (`ResetRx`), after five failed writes to the one being reset (see above). |
| `0x16` | to either | Close. `MasterDevice.CloseRecUsb` and `CloseSenderUsb` exist but nothing calls them; the plugin never sends it. |

**Master query** (`0x11`, to the transmitter, `MasterDevice.QuerryMasterMac`). The reply is one packet:

| Bytes | Meaning                                           |
| ----- | ------------------------------------------------- |
| 0     | `0x11` echoed                                     |
| 1-6   | the master's 6-byte RF address                    |
| 7-10  | the master's clock, big-endian, in 0.625 ms ticks |
| 11-12 | the transmitter's firmware version, big-endian    |

L-Connect ignores a reply that does not echo `0x11`. From one that does it always takes the clock (the ticks summed as an `int`, times 0.625, truncated); a clock of zero means the transmitter has not started, and the reply's address is not taken. Otherwise it copies the address over the one it knew, so an all-zero address makes the master **unknown** until the next good reply - and with no master, `RefreshList` reads nothing. The plugin reproduces all three outcomes. Byte 1 is not a parameter of the query but the channel the transmitter is to work on, so this query - which L-Connect sends every second - is also what keeps the transmitter on the master's channel.

**Device list** (`0x10`, to the receiver, `MasterDevice.GetDev` and `RefreshList`). The reply is 434 bytes per requested page:

| Bytes | Meaning |
| --- | --- |
| 0 | `0x10` echoed |
| 1 | how many devices the receiver can hear, which may exceed what the requested pages carried |
| 2-3 | if bit 7 of byte 2 is set: the receiver's firmware version in the low 15 bits. Otherwise a measurement of the motherboard fan header's PWM: `255 * b3 / (b2 & 0x7F + b3)` |
| 4 onwards | one 42-byte record per device, ten per page |

At most the requested pages' worth of records is read, however many byte 1 reports, and a record whose last byte is not `0x1C` is skipped. The next request asks for exactly `ceil(count / 10)` pages - so the request grows when devices appear and shrinks when they go - and the first request asks for one. Bytes 2-3 of the request stay zero: L-Connect fills them only while it forwards the motherboard header's speed, which we haven't supported.

## Layer 2: the RF payload

Everything addressed to an actual fan, pump or light is a **240-byte RF payload**, handed to the transmitter as four `0x10` packets carrying 60 bytes each (`MasterDevice.SendRfData`). The packet header's channel and receiver slot are the ones the device reports **now**; a broadcast goes on the master's channel to slot `0xFF`. Payloads share a common front:

| Bytes | Meaning                                                                    |
| ----- | -------------------------------------------------------------------------- |
| 0     | `0x12`, always                                                             |
| 1     | the RF command                                                             |
| 2-7   | the address of the device being commanded (`FF FF FF FF FF FF` broadcasts) |
| 8-13  | the master's address                                                       |
| 14    | the receiver slot the device was bound under                               |
| 15    | the master's channel                                                       |

The RF commands L-Connect uses:

| Command | Name | What it does |
| --- | --- | --- |
| `0x10` | bind and speed | Sets the four slot PWMs, and restates the binding. **The plugin sends this.** |
| `0x12` | select | Highlights a device in L-Connect's UI. |
| `0x13` | print | A diagnostic. |
| `0x14` | clock | Broadcast clock pulse that keeps every device's clock on the master's. **The plugin sends this.** |
| `0x15` | save config | Broadcast: commit binding and effect to flash. **The plugin sends this.** |
| `0x16` | reboot LCD | For the fans with screens. |
| `0x19` | switch wireless theme | Moves an AIO screen from PC-streamed content to its own theme. **The plugin sends this.** |
| `0x20` | effect data | One chunk of a rendered lighting effect. **The plugin sends this (Lighting build).** |
| `0x21` | AIO parameters | A water block's pump and screen settings. **The plugin sends this.** |
| `0x22` | picture data | An image for an AIO screen. |
| `0x23` | close wifi | Lancool 217 radio off. |
| `0x24` | motherboard sync | Lancool 217 / V150 motherboard PWM follow. |
| `0x26` | light switch | Lighting off. |

### Bind and speed (`0x10`)

`MasterDevice.SyncPwm` builds it, and the plugin builds it the same way:

| Bytes | Meaning                                                                                    |
| ----- | ------------------------------------------------------------------------------------------ |
| 8-13  | the master's address (`target_master_mac_addr`)                                            |
| 14    | `target_rx_type`: the receiver slot the device reported when it was first taken as bound   |
| 15    | `MasterChannel`: the master's channel                                                      |
| 16    | the **bind index**: the device's 1-based position among the bound devices. `0` unbinds it. |
| 17-20 | the four slot PWMs, `0`-`255`                                                              |

This is the same command that binds a device, which is why it restates all of that: a speed write is also a statement of which master the device belongs to and where. That is why the plugin never sends it to a device whose latest record names another master, or none - it would bind the device back.

The **bind index** is computed exactly as `SyncPwm` computes it: walk the device table in first-heard order (see below), skip a device that is not bound, is changing effect, or whose record names no master, and number the rest from 1. A device that has moved to another master is still bound in L-Connect's sense and keeps its number, though it is sent nothing. `SyncPwm` pauses 5 ms after each numbered device, sent or not, and so does the plugin.

### When a speed is sent

Once a second, `SyncPwm` sends a device its target PWMs whenever `NeedSyncPwm` says so: whenever any slot the device **reports** differs from its target by more than 5. A speed packet lost on the air is therefore corrected the next second, and a device that already runs its target is left alone. Two special cases, both kept:

- A device reporting no fans is never sent a speed, except the Lancool 217 and the V150.
- A V150 whose target is at or below 10 on any slot is sent it every second regardless.

The plugin adds one condition L-Connect does not need, because in L-Connect a curve always owns every device: only the slots a FanControl control is driving are compared. A device nobody has set in FanControl is sent nothing at all.

A target starts as whatever the device reported when it was taken as bound (`RefreshList`: `fans_pwm.CopyTo(target_fans_pwm)`), and a control overwrites the slots it drives. A slot no set control drives carries what the device reports for it and is left out of the comparison: the packet always goes out whole, so a speed sent for a Lancool 217's front pair must not restore a rear fan FanControl has let go of. A drifted clock freezes the driven slots' targets, as the `RFList` getter does, but never a release. When FanControl lets a control go, or closes, the device keeps running the last speed it was sent, as it does when L-Connect exits.

`MasterDevice.SyncControlInfo` also sends the same payload, whatever the speed, to any bound device heard on another channel or receiver slot than the one it should be on - that is what brings it back onto the master's channel, where the clock broadcast reaches it - and so does the plugin, each cycle.

## The device table

`Devices/WirelessDeviceTable` keeps every device the receiver has reported, in the order each was first heard, exactly as `MasterDevice.RefreshList` keeps its `rfList`. Each list read:

- **A device heard for the first time is appended.** If its record names this master it is taken as bound (`bind_to_master`): its receiver slot is remembered as `target_rx_type` and its targets start from what it reports. A master dongle's own record (type `0xFF`) goes to L-Connect's separate master list and is never on the table.
- **Every device has a countdown**, `RfDevice.max_live_time` = 30. It restarts whenever the device is heard (`FindDev`), and it counts down on every read - failed reads included, since `RefreshList` counts down before it sends the request - for a device that is not bound (the Lancool 217 excepted) and for a V150. After a read that carried devices, the first device whose countdown has reached zero is dropped. `RefreshList` then returns at once, so only one device goes per read. A bound fan group, water block or Lancool 217 is never dropped.
- **Receiver slot conflicts** are counted per device: each read, a bound device is checked against every device of this master on the same receiver slot, as the table stands when its record is reached. Past four, a pair whose addresses differ only in a first byte of `1` is a **ghost**: the `0x01...` address is dropped for good and never added again (`CheckJustStartDiff`, `ErrMacLst`). Any other conflict L-Connect settles by unbinding; the plugin logs it and leaves the binding alone. Without a conflict anywhere in a read, every count eases by one.
- **The Lancool 217 and the V150** have their clock compared with the master's (`sys_offset`). One more than two seconds off is left out of L-Connect's bound-device list (the `RFList` getter), so its targets are not updated until the clock pulse brings it back; the last targets keep being resent.

On top of L-Connect's table the plugin counts, for every device, the list reads in a row that did not carry it. A driven device unheard for **30 reads** - L-Connect's own count - is **lost**: its fans read 0 rpm and its coolant temperature reads nothing until it is heard again. It is still driven, as L-Connect drives it: a receiver that stops answering says nothing about whether the device still hears the transmitter, and a curve that asks for more cooling meanwhile must still reach it. L-Connect keeps showing a bound device's last readings forever (it only drops unbound devices and the V150); the plugin reports a stopped fan instead so that FanControl's fan-failure detection sees it and a curve on the coolant temperature does not follow a frozen value. The sensors stay, so the user's bindings do too. The plugin reads the list once a second, so "lost" takes about thirty seconds.

Where L-Connect would unbind a device the plugin does not, because we haven't supported unbinding: a device that was first heard unbound and later names this master (`RefreshList`'s "reunbind") is taken as bound and driven; a device beyond this master's eleventh bound (`RefreshList` unbinds at twelve) is not driven.

## What the plugin drives

A device is **driven** when it is bound in the table's sense, its latest record names this master, and it is not lost. What each kind gets, following the fan configuration L-Connect's service gives it (`LWirelessController.addSettingDevice`):

| Type byte | L-Connect `DevTypes` | FanControl sensors |
| --- | --- | --- |
| `0` | a fan group | one control for the group, one RPM reading per fan |
| `1`-`9` | `Strimer` | none (lighting only) |
| `10`, `11` | `WaterBlock`, `WaterBlock2` (HydroShift II) | one control for its fans, the pump's control and RPM, one RPM reading per fan (at most three), the coolant temperature |
| `65` | `LC217` (Lancool 217 Infinity) | a front control (slots 0-1) once a front fan is fitted and a rear control (slot 2) once a rear fan is (L-Connect offers the front curve only when `fans_type[0]` or `[1] == 1`, and creates the rear curve only when `fans_type[2] == 1`), one RPM reading per fitted fan (type code `1` in its slot) |
| `66` | `V150` | one control, one RPM reading per fan |
| `0xFF` | another master dongle | nothing |
| anything else | `ALL` | driven as a fan device: one control, one RPM reading per fan |

**One control per group** is L-Connect's own granularity: the service computes one duty per device and `LWirelessDevice.SetFanSpeed` repeats it across all four slots (`Enumerable.Repeat(duty, 4)`). A device without fans gets its control only once it reports some, since `NeedSyncPwm` would never write it (the V150 excepted). A water block has at most three fans because its fourth slot carries the pump's RPM and, in the type byte, the coolant temperature (`RFController.GetAioTemp` reads `fans_type[3]`).

Within a run, sensors are only ever added: a device heard later, or a group reporting more fans, adds its sensors at the end, and the controller raises `TopologyChanged` so the plugin can ask FanControl to refresh (`IPlugin3.RefreshRequested`). Every id is keyed on the device's RF address (twelve lowercase hex digits), so a device appearing or disappearing never re-keys another's saved bindings:

| Sensor                | Id                               |
| --------------------- | -------------------------------- |
| group or V150 control | `LianLi/w<address>/ctl`          |
| water block pump      | `LianLi/w<address>/pump/ctl`     |
| Lancool 217 front     | `LianLi/w<address>/front/ctl`    |
| Lancool 217 rear      | `LianLi/w<address>/rear/ctl`     |
| fan RPM, slot _n_     | `LianLi/w<address>/f<n>/fan`     |
| pump RPM              | `LianLi/w<address>/pump/fan`     |
| coolant temperature   | `LianLi/w<address>/coolant/temp` |

## Fan speed

The wire value is a PWM byte on a **0-255** scale. L-Connect's service maps a duty percent onto it with `NumberHelper.Map(duty, 0, 100, 0, 255)`, constrained and truncated (`startWriteFanSpeed`, `LWirelessDevice.SetCaseSpeed`). In front of that map sit the service's rules, which the plugin applies to FanControl's duty:

1. **A duty of 0 is sent as 5%**, with no floor: `getTemperatureDuty` (fans) and `calculateDuty` (the Lancool 217) return 5 when the curve asks for no speed. 5% is PWM 12. A wireless fan is never sent 0.
2. **Anything else is raised to a floor** and capped at 100%. The floor is picked per device the way `convertFanType` picks the fan type - from the first slot's family and size, and whether any slot is an LCD fan - and `startWriteFanSpeed` maps each type to its floor (table below).
3. **A CL group steps off its reserved values**: `NeedSyncPwm` turns 153 and 154 into 152, and 155 into 156, for a group whose first slot is a CL fan.
4. **`6` is reserved** for "follow the motherboard header", and `RFController.SetFansRPM` scrubs it to 0 from an ordinary request. Every duty the rules above can produce maps to 12 or more, so it cannot arise.

| Device | `LWirelessFanType` | Floor |
| --- | --- | --- |
| a group whose first fan is SL V3 (`20`-`26`) | `SLV3Fan120LED` ... `SLV3Fan140LCD` | 14% |
| a group whose first fan is TL V2 (`27`-`35`), with no LCD fan | `TLV2Fan120LED`, `TLV2FanReverse120LED`, `TLV2Fan140LED` | 11% |
| a group whose first fan is TL V2, with any LCD fan (`23`-`27`, `32`-`35`) | `TLV2Fan120LCD`, `TLV2Fan140LCD` | 10% |
| a group whose first fan is SL-Infinity (`36`-`39`) | `SLINFWFan120LED`, `SLINFWFan140LED` | 11% |
| a group whose first fan is CL (`41`-`42`), RL120 (`40`) or empty | `None` | 10% |
| a water block's fans | `CLFan120LED` (fixed by `addSettingDevice`) | 10% |
| the V150, or an unrecognised type | `None` | 10% |
| the Lancool 217, front and rear | (`startWriteCaseSpeed`) | 11% |

Type `27` is a TL V2 140 mm reverse fan with an LCD (`RfDevice.InitAttr` sets `BindLcd` for it), so a group containing one floors at 10%.

The **Lancool 217** is written as L-Connect's `SetCaseSpeed` writes it: the front duty on slots 0 and 1, the rear on slot 2, and slot 3 zero. If only one of the two controls is set in FanControl, the other's slot keeps its last value.

Two things L-Connect does here the plugin does not:

- **Smoothing.** `SetFansRPM` averages a new target with the one before it for a second (see "Where the plugin's timing differs"). FanControl owns the curve and its smoothing, so the plugin writes the duty it was given.
- **Speed curves from temperature.** The service's RPM curves and the TL V2 120 LCD's RPM-to-duty formula (`rpmToDuty`) turn a temperature into a duty; FanControl hands the plugin the duty directly.

## The pump

A wireless water block takes a single 32-byte **parameter block**, sent as RF command `0x21` with the block at payload offset 18, bytes 14-15 as in the speed command (`MasterDevice.SendAioInfo`). It carries the pump speed _and_ everything the AIO's screen shows. `RFController.SetAioParams` lays it out:

| Bytes | Meaning                                                                           | What the plugin sends |
| ----- | --------------------------------------------------------------------------------- | --------------------- |
| 0-3   | CPU temperature, CPU load, GPU temperature, GPU load (each capped at 99)          | zero                  |
| 4-5   | the fan speed figure, big-endian (`FanSpeed`)                                     | saved                 |
| 6     | screen refresh interval (`LoopInterval`)                                          | saved                 |
| 7     | whether the pump temperature is shown (`PumpEnable`, set from `IsPumpTempEnable`) | saved                 |
| 8-11  | whether CPU temperature, CPU load, GPU temperature, GPU load are shown            | zero                  |
| 12    | whether the fan speed figure is shown (`FanSpeedEnable`)                          | saved                 |
| 13-24 | three ARGB colours: the title, the value and the unit                             | saved                 |
| 25    | screen brightness (`LcdBrightness`)                                               | saved                 |
| 26    | always 1                                                                          | 1                     |
| 27    | screen theme (`WirelessTemplateIndex`, 0-12)                                      | saved                 |
| 28-29 | **the pump timer**, big-endian                                                    | from FanControl       |
| 30    | screen rotation                                                                   | saved                 |
| 31    | zero                                                                              | zero                  |

Byte 7 is not a pump switch: the service writes it from the carousel setting `IsPumpTempEnable` (`handleSetPumpCarouselSettings`), next to the other figure switches, and the pump runs from the timer alone. Every saved setting is read from L-Connect's pump settings document and carried through, narrowed to its byte exactly as `SetAioParams` narrows it. With no saved settings the plugin uses the block L-Connect starts a new water block with (`addSettingDevice`): refresh interval 2, pump temperature shown, fan speed 2000 hidden, white text, brightness 100, theme 0. The four live CPU and GPU figures are the one part we haven't supported: the plugin has no readings to fill them with, so it sends them as zero and hidden rather than freezing wrong numbers on the screen. A screen L-Connect has in **advance mode** plays content streamed from the PC and never has the wireless theme applied (`LWirelessController` calls `applyWirelessMode` only when `IsAdvanceMode` is false), and L-Connect's own setting handlers (`handleSetPumpLCDBrightness` and the rest) still write the saved brightness, rotation and colours to it through `SetAioParams`, with the theme byte left at 0. The plugin does the same: a screen saved in advance mode keeps every saved setting, with theme 0.

A screen saved **out** of advance mode is switched to its wireless theme first, as `applyWirelessMode` does at startup and whenever a water block binds, so a screen left on streamed content (a switch cut short by sleep, or L-Connect stopped while it streamed) shows its theme again. `RFController.SwitchAioLcdWirelessMode` raises the device's command sequence and queues ten sends; `MasterDevice.SyncControlInfo` sends `0x19` once a pass, with the device's receiver slot and channel at bytes 14 and 15, at byte 16 how many bound devices before it the pass has counted, and the sequence at byte 17, until the device's list record reports that sequence back at byte 40. The plugin does the same: it picks a sequence the device has not acknowledged (1 to 254, as L-Connect wraps it), sends the switch ahead of the parameter block once a second, and stops when the record carries the sequence or after ten sends, logging which. It does this once per FanControl process (a refresh does not repeat it, and L-Connect does not repeat it on wake either), and again when the device is heard again after being lost or the dongles reconnect. Like the parameter block, it goes only to a water block whose pump FanControl drives.

The pump is commanded by a **timer value, not an RPM**, and a lower timer is a faster pump. L-Connect converts with a piecewise-linear table per generation, clamping the requested speed into range first (`RFController.SetAioPumpSpeed`, `SetH2SAioPumpSpeed`):

| Generation | Type | Range | Table |
| --- | --- | --- | --- |
| First (H2) | `10` | 1600-2500 rpm | 1600-1720: `1500 - (r-1600)*1.667`; 1720-1870: `1300 - (r-1720)*2`; 1870-2000: `1000 - (r-1870)*1.23`; 2000-2300: `840 - (r-2000)*2`; 2300-2400: `240 - (r-2300)*1.8`; 2400-2500: `60 - (r-2400)*0.5` |
| Second (H2S) | `11` | 1600-3200 rpm | 1600-1800: `1590 - (r-1600)*0.95`; 1800-2000: `1400 - (r-1800)`; 2000-2200: `1200 - (r-2000)`; 2200-2400: `1000 - (r-2200)`; 2400-2600: `800 - (r-2400)`; 2600-2800: `580 - (r-2600)*1.11`; 2800-3000: `330 - (r-2800)*1.2`; 3000-3200: `90 - (r-3000)*0.45` |

Each bracket is inclusive at both ends and the earlier branch wins on a boundary, so 2000 rpm on the first generation resolves through the 1870-2000 branch to 841, not through the next one to 840. The plugin reproduces the tables exactly, including that.

FanControl gives the plugin a duty percent, so the plugin spans the block's range linearly and then converts: 0% is the slowest speed the firmware accepts, and 100% is the fastest L-Connect ever drives it - 2450 rpm on the first generation, where every pump curve L-Connect offers tops out (`LWirelessRPMSettingManager`, `MaxSpeed = 2450`) short of the table's 2500, and 3200 rpm on the second. There is no way to stop a wireless pump and the plugin does not try.

`SendAioInfo` sends the block to every bound water block every second, and so does the plugin, to every driven water block whose pump FanControl has set; a pump nobody has set is left as it was. Only a change of the pump's duty is logged, not the refresh each second.

## Lighting

L-Connect does not send a wireless device an effect _name_. It **renders** the effect to frames on the PC, compresses them, and streams the bytes to the device, which then plays them back on its own. That rendering is the bulk of `slv3.dll`, and the compressor is a native `tinyuz` entry point (`TuzEnc`). Reimplementing it is out of the question and pointless, because **L-Connect writes the finished result to disk**. Only the Lighting build reads it.

### What is on disk

When L-Connect applies a look it saves the rendered effect to `%ProgramData%\Lian-Li\L-Connect 3\slv3\config\<address>.effect` (`RfDevice.rgbEffect`'s setter), where `<address>` is the device's RF address as twelve lowercase hex digits with no separators. It is `System.Text.Json` output, so the byte arrays are base64 strings:

| Member            | Meaning                                                       |
| ----------------- | ------------------------------------------------------------- |
| `data`            | the compressed frame data, exactly as it goes on the air      |
| `effect_index`    | the effect's 4-byte identity                                  |
| `total_frame`     | how many frames                                               |
| `total_sub_frame` | how many sub-frames, for effects with an inner layer          |
| `led_num`         | how many LEDs the frames address                              |
| `interval`        | milliseconds between frames, fractional                       |
| `sub_interval`    | milliseconds between sub-frames                               |
| `rgb_data`        | null in the saved copy - the uncompressed frames are not kept |

`isOuterMatchMax` is not serialised, so after a reload L-Connect itself streams it as 0, and so does the plugin. L-Connect's 12288-byte cap on a compressed effect (`MasterDevice.LzoMaxRgbDataLen`) is checked by only one of its effect renderers and never by its replay, so the plugin accepts any saved effect the stream can carry - 254 chunks of 220 bytes, since the header counts the chunks, itself included, in one byte - and treats only a larger one as corrupt.

### Streaming

`effect_index` is a timestamp L-Connect stamps on an effect when it renders it (`RgbEffect.RefreshIndex`). The device reports the identity of the effect it is running at record bytes 20-23, and `MasterDevice.SyncRgbData` streams whenever that differs from the effect it wants - every loop, for as long as it differs. The plugin does the same on every worker call, for every driven device with a saved effect. While it differs the device is **changing effect** (`changingEffect`): the speed resend skips it and does not number it, until it reports the effect. The effect goes out as RF command `0x20`, one payload per chunk:

| Bytes | Meaning                                 |
| ----- | --------------------------------------- |
| 14-17 | the effect identity                     |
| 18    | chunk index; `0` is the descriptor      |
| 19    | the number of data chunks, and one more |

The descriptor chunk carries the shape of what follows:

| Bytes | Meaning                                                      |
| ----- | ------------------------------------------------------------ |
| 20-23 | the compressed length, big-endian 32-bit                     |
| 24    | zero                                                         |
| 25-26 | total frames, big-endian                                     |
| 27    | LED count                                                    |
| 32-33 | the whole-millisecond part of the frame interval, big-endian |
| 34    | the fractional part, as hundredths (truncated)               |
| 35-36 | the sub-frame interval, big-endian                           |
| 37    | an inner/outer matching flag                                 |
| 38-39 | total sub-frames, big-endian                                 |

Data chunks carry 220 bytes each at offset 20. L-Connect sends the descriptor, then repeats it three more times 20 ms apart, then sends the data chunks in order, then waits 10 ms and asks for a save. The plugin does the same, pausing through its injected delay.

**The one place the plugin stops short of L-Connect.** L-Connect keeps streaming, and keeps the device out of the speed resend, for as long as the device does not report the effect, so a device whose firmware never takes it never gets a fan speed. The plugin gives up once the device has gone ten seconds without reporting the effect, over at least three streams tried. A stream the dongle failed to send counts towards that too, so a dongle that keeps dropping a long stream does not cost a stream a second for ever, but only a stream that was sent holds the device out of the speed resend, since a failed one asked the device nothing. It is time rather than a count of streams because every control change brings the next stream forward. It logs that it gave the lighting up and drives the device's fans as usual, and it tries the lighting afresh when a dongle reconnects or the device comes back after being lost. Lighting never costs fan control.

A **Strimer of type 2 or 4** is re-timed first (`MasterDevice.SyncStrimmer_22`): its frame interval becomes the first type 1 or 3 Strimer's interval times that one's frame count over its own, so the two strips run over the same span. The first such Strimer on the table is used whatever it is bound to, and the new interval stays on the effect, as L-Connect keeps it, once that Strimer is gone.

## Saving and the clock

- **Save config** (`0x15`) tells every bound device to write its binding and effect to flash, so the look survives a power cut. It goes out through `MasterDevice.SaveConfig(1)`: one broadcast, then a 200 ms pause. Two rules trigger it, both reproduced exactly (`Devices/WirelessSaveSchedule`): after an effect stream (`RFController.SaveConfig`), ten seconds later if no stream came in the last five seconds, otherwise ten more; and periodically (`MasterDevice.CheckSaveConfig`), one hour after start and then whenever three hours have passed since the last save, never within thirty seconds of a stream. L-Connect also saves three times when the machine suspends (`LWirelessController.Suspend`); FanControl tells a plugin nothing about a suspend, so we haven't supported that save. And L-Connect's service saves when it stops (`MainService`'s stop calls `RFController.SaveCfg`); the plugin's close also runs on every FanControl refresh - every wake, logon and unlock - so it saves on close only when a streamed look is still waiting for its debounced save, rather than write the devices' flash each time.
- **Clock** (`0x14`, `MasterDevice.SyncMasterClock`) is broadcast every second whatever else is happening. It keeps each device's clock on the master's, which is what keeps a multi-device effect in step, and what L-Connect relies on to bring a drifted Lancool 217 or V150 back.

## The RF channel

The master works on channel 8 unless L-Connect saved another. `RFController.Init` queries the master first, then reads `slv3\config\<master address>.config` for **that** master (`GetChannel`): the channel as a decimal number, which 0, an empty file or an unreadable one leaves at the default. The plugin does exactly that when it first learns the master's address, and again if the address changes, so a file left by another master is never used. The channel goes out in every master query - which re-asserts it every second, and at once after a dongle is reopened - in byte 15 of every addressed payload, and in the header of every broadcast. FanControl builds a new controller on every refresh (each wake, logon and unlock), and the first query goes out before the master is known, so the process keeps the channel it last drove the master on and a new controller starts on it: the transmitter is put on channel 8 only on the first start, as L-Connect does it only when its service starts. Once the master answers, a channel L-Connect saved still wins, then the one kept from before the refresh, then 8.

When another L-Wireless controller is in radio range, the receiver hears its master too: every record of type `255` is a master, kept in a list of its own with the same thirty-read countdown as a device (`masterList`). Once a second the plugin does what `MasterDevice.CheckChannelConflict` does. The masters are ordered by address and each place has a channel: 8 for the first, then 12, 16 and so on. If this master reports an even channel other than its own place's, it moves there for this run (`SwitchChannel`), and the ordinary re-homing brings its devices after it. The even channels are the ones L-Connect hands out itself. A channel the user chose in L-Connect is odd (`ChangeChannel` refuses an even one) and is never moved. Like L-Connect, the plugin does not save the move, and makes it once rather than on every read until the master's record shows it.

### The locked list

L-Connect lets the user lock the device list (`RFController.LockDevice`), which saves the table - every device naming this master, in order - to `slv3\config\savedDevices.config`, a JSON list of its `RfDevice` objects with the byte arrays as base64. At startup `RFController.CheckLockAndInitData` restores it when the first entry names the master just queried, and while it is locked `RefreshList` takes no newly heard device, keeps each device's saved product type and fan types (a water block's and a case's slots are still read, since they carry what is fitted and the coolant temperature), checks no receiver slot, and drops nothing. The table's order is the bind index every speed command carries, so the plugin restores it exactly: the first time it learns the master it reads the file, and if the list is locked for that master the table becomes that list, every device bound at the receiver slot and speeds it was saved with, and it keeps those rules for as long as the list is locked. A device paired after the list was locked is left alone until it is unlocked, as L-Connect leaves it. Unlike L-Connect, the plugin sends a device on the list nothing - no speed, whose command restates a binding - until the receiver has reported it at least once, since a saved pairing is no evidence the device is still paired: the plugin never binds or re-pairs anything. When no device on the list names this master any more, L-Connect unlocks and deletes the file; the plugin stops keeping the lock too, but leaves L-Connect's file where it is.

## The one-second cycle

`Devices/WirelessController` is `MasterDevice.Run` on the plugin's worker. Each call to `PollRpm` is one `RefreshList` (one list read, nothing without a master). Each call to `ApplyPending` runs, when a second has passed since the last, the one-second block of `Run` in its order - the speed resend (`SyncPwm`), the periodic save check (`CheckSaveConfig`), the master query (`QuerryMasterMac`), the water blocks' parameter blocks (`SendAioInfo`) and the clock (`SyncMasterClock`) - and then, on every call, the effect streams (`SyncRgbData`) and the debounced save. Time comes from the injected clock and every pause from the injected delay.

Each device's work, and each broadcast, is isolated: a failure is logged when it starts and when it ends - not every second - and costs that device that second, never the others. A dongle the transport reopened (its `Generation` moved on) re-runs the plugin's registered replay and the one-second block at once; everything else a reset device lost - its speed, its effect - is resent by the ordinary comparison against what it reports.

**Construction** asks for the master up to three times, then reads the list until two reads in a row agree, half a second apart and at most four reads: two and a half seconds of waiting at most, bounded by count rather than by the clock. L-Connect does not wait - `MasterInite` ignores the query's result and `Run` simply keeps going - so the wait is the plugin's, there so that FanControl's first load has the devices that are already on the air rather than one refresh each. A master that does not answer leaves a controller with no sensors that keeps asking every second, and the devices appear, with a refresh, once it answers. Construction never throws for a silent or failing dongle.

### Where the plugin's timing differs

L-Connect's loop runs about every 300 ms. It reads the device list on every pass once half a second has gone since its one-second block, and again at the end of `SendAioInfo`, so three or four times in each cycle of about 1.3 s; it sends the channel and slot corrections and the screen-mode switch (`SyncControlInfo`) on every pass; and it keeps the one-second block to once a second. The plugin runs everything on the one-second worker tick, and reads the list once a second however often FanControl wakes the worker, so a list read, a correction or a screen switch goes out once a second rather than about three times. Every rule that counts list reads keeps its count but not L-Connect's time: an unbound device, a V150 or another master is dropped after about thirty seconds rather than ten, and a receiver slot conflict is logged after about five seconds rather than one and a half. None of that changes what is driven: every correction is resent until the device reports it, and the screen switch is acknowledged by the device.

L-Connect also smooths a new speed for one second (`SetFansRPM` puts each target into `fans_pwm_list` and `SetFansPwmFromAvg` sends the average of the last two). The plugin sends FanControl's value as it is, because FanControl already applies the response time and step settings the user chose, and a second layer of smoothing would change what those settings do.

## L-Connect's files

| Path (under `%ProgramData%\Lian-Li\L-Connect 3\`) | What it holds | Used by the plugin |
| --- | --- | --- |
| `slv3\config\<master address>.config` | the RF channel, as a decimal number, when the user changed it | yes |
| `slv3\config\savedDevices.config` | the locked device list, when the user locked it | yes |
| `slv3\config\<device address>.effect` | a rendered lighting effect | Lighting build |
| `slv3\config\BindingSet.Config` | the user's ordering of bound devices | no |
| `device\137b3f244d3c5d1568121556b5b076e5\cf82720db122ae41719df5b05503b749.0` | the wireless **pump** settings, gzipped JSON | yes |
| `device\137b3f244d3c5d1568121556b5b076e5\50bd8c21bfafa6e4e962f6a948b1ef92.0` | the wireless **fan** curves, gzipped JSON | no |
| `device\137b3f244d3c5d1568121556b5b076e5\cd14c323902024e72c850aa828d634a7.0` | the wireless **case fan** curves, gzipped JSON | no |

The device-settings filenames are lowercase MD5 hashes with the extension `0`: the directory is `md5("lwireless-controller")` and the file is `md5("pump")`, `md5("fan")` or `md5("case")`. This is the same scheme the plugin already reads for the wired controllers' start/stop profiles.

Every file is optional to fan control. A missing file is the normal case. One that is locked, unreadable, oversized, not gzip or not the JSON L-Connect writes is logged with its path and the reason, and costs only its own feature: the saved channel (the master stays on channel 8), the saved look, or the saved screen (that water block gets L-Connect's default screen). One water block whose saved settings are incomplete falls back alone; the others keep theirs. The service's own `loadSettings` catches and logs the same way. The curve files are not read: FanControl owns the curves.

## What we haven't supported

- **Binding, unbinding, re-pairing, or moving a device to another master.** Pairing stays L-Connect's job. The plugin never sends a bind index of 0, and never sends anything to a device whose record names another master or none. Where L-Connect would unbind a device - one first heard unbound that later names this master, one past the eleventh bound, one sharing a receiver slot - the plugin leaves the binding alone.
- **Motherboard sync.** The reserved PWM `6`, the `0x24` command and the forwarding of the motherboard header's speed (`FgSync`, `SyncFgPwm`) are not used: the plugin keeps every device under software control.
- **Live CPU and GPU figures on the AIO screen**, and screen images, themes switched from the plugin, or carousels. The screen keeps every saved setting and hides only the live figures.
- **Rendering new effects.** The plugin replays what L-Connect saved; it cannot compose a look.
- **The suspend-time save**, since FanControl does not tell a plugin the machine is suspending.
- **The screen, radio and light switches** (`0x16` reboot LCD, `0x23` close wifi, `0x26` light switch), firmware updates, and more than one TX/RX pair.

## Sources

- Lian Li L-Connect 3, decompiled for protocol facts only: `slv3.dll`, `slv3.models.dll`, `L-Connect.Core.dll`, `L-Connect-Service.exe`.
- LibUsbDotNet (MIT), as shipped with L-Connect, for how a WinUSB device is located and opened.
