# Devices

What the plugin does for each Lian Li device it supports, which of them have been confirmed on real hardware, and how the wireless range and the FLEX fans fit in. The README has the [short table](../README.md#supported-devices); this page has the detail. The byte-level protocols are in [protocol.md](protocol.md), [wireless.md](wireless.md) and [lighting.md](lighting.md).

## What has been tested on hardware

- **UNI FAN SL-Infinity**: verified end to end on real hardware, fan control, RPM and lighting replay included. It is the maintainer's own kit.
- **GALAHAD II Trinity**: the fan and pump commands and the RPM readback were confirmed on a real cooler at the wire level by a community member in [#30](https://github.com/lewisgibson/FanControl.LianLi/issues/30).
- **UNI FAN SL, AL, SL V2, AL V2, Redragon edition**: fan control is long-standing and well proven in the field; the lighting replay for these families is reproduced from L-Connect and not yet confirmed on that exact hardware.
- **Everything else**: the UNI FAN TL, the GALAHAD II Vision, the HydroShift LCD, the HydroShift II OLED Curve pump, the STRIMER Plus lighting, the FLEX receivers over USB and the whole wireless range are built to match what Lian Li's own L-Connect 3 (2.1.29) sends, to the byte, by decompiling it, and are tested against that. None of them has been confirmed on that hardware yet. If you have one, it should just work; please [open an issue](https://github.com/lewisgibson/FanControl.LianLi/issues) with your plugin log if anything looks off.

## UNI FAN hubs

The UNI FAN SL, AL, SL-Infinity, SL V2, AL V2, SL Redragon edition and the older Uni Hub are four-channel USB controllers. Each channel is a control you can put on a curve, and each reports its RPM.

- **Only the channels with a fan plugged in are shown.** The plugin probes each channel at startup and hides the empty ones, so you get one control per real fan , not four slots with three dead ones. A fan that is present but stopped at that moment may be hidden until it next spins. If detection is inconclusive the plugin shows all four rather than hide anything.
- **Start/stop (zero RPM).** If you turned on L-Connect's start/stop switch for a fan group, the plugin honours it and the fans that support it stop at 0%. Without it, a 0% curve point sends the controller's own minimum: the SL-Infinity floors at about 210 rpm, while the SL V2 does stop.
- **Identity.** Every UNI FAN unit reports the same USB serial, so the plugin tells controllers apart by the Windows device they are plugged into, never by serial. Each controller keeps its index across reboots, sleep and hibernate, so your bindings stay with the same physical port.

## UNI FAN TL

The TL hub speaks a different command protocol from the other UNI FAN hubs (it shares it with the GALAHAD II). The plugin drives its fans and reports their RPM, and the Lighting build replays the saved look. A TL fan that answers after the others is added once the hub has reported it three polls in a row, as L-Connect waits for. The TL LCD's screen is left alone.

## AIO coolers

- **GALAHAD II Trinity**: the fan and the pump are each a control, both report RPM, and the Lighting build replays the fan and pump lighting. The pump control's bottom end is about 2600 rpm, a margin above the pump's real minimum, not a hardware limit. A 0% curve point on the fan sends duty 0, which spins the radiator fans down to roughly 250 to 350 rpm rather than stopping them. Commanded speeds persist in the controller across a full power cut, and the cooler has no thermal failsafe of its own, which is why the plugin keeps re-asserting the speed every 15 seconds.
- **GALAHAD II Vision**: fans and pump as the Trinity. The Lighting build replays the fan lighting and the ring of lights around the screen in its fixed-colour modes; a ring effect that follows a CPU, GPU, pump or coolant reading in L-Connect is left as it is, because the plugin has no such reading to colour it from. The screen itself is left alone.
- **HydroShift LCD**: fans and pump as the Trinity. The fans are its only lights and they are replayed. The screen is left alone.
- **HydroShift II OLED Curve**: its fans plug into a motherboard header, not the cooler, so the plugin drives the pump only. 0% is 1600 rpm and 100% is 2400 rpm, the range L-Connect's own pump curves use. It reports the pump speed and the coolant temperature, which you can use as a curve source. Its screen, screen motor and lighting are left alone: L-Connect streams that lighting from the PC frame by frame, so there is no saved look to replay. The pump rides on the cooler's lighting device, which Windows binds to WinUSB like the wireless dongles.

## STRIMER Plus and Plus V2

Lighting only. The Lighting build replays the saved look; the other builds leave the cables alone.

## The wireless range

The wireless fans are not on a USB controller at all. They pair over radio with the **L-Wireless SYNC** controller, whose two dongles (a transmitter and a receiver) Windows sees as plain USB devices. The plugin talks to them the same way L-Connect 3 (2.1.29) does, so everything you paired in L-Connect shows up:

- **Fan groups** (UNI FAN SL V3, TL V2, SL-Infinity Wireless, CL, and the FLEX range with the P28 V2): one control per group, since L-Connect drives a group at one speed too, with an RPM reading for every fan in it.
- **HydroShift II**: a control for its fans and one for its pump, RPM for each fan and the pump, and the coolant temperature.
- **Lancool 217 Infinity case fans**: the front pair and the rear fan as two controls, each appearing once a fan is fitted, with RPM per fan.
- **V150 case fans**: the front pair as the control it always had, so an existing curve keeps driving it, and the rear fan as a second control once one is fitted.
- **STRIMER**: lighting only, replayed by the Lighting build.

Things to know:

- **Pair in L-Connect first, then switch L-Connect off.** The plugin never pairs or unpairs anything and only drives what is already paired to your dongle. It keeps the radio channel L-Connect chose and moves off it only the way L-Connect does, when another L-Wireless controller nearby is on the same one. L-Connect and the plugin can't both have the dongles open at once, so stop FanControl before you pair anything, pair with L-Connect's service running, then switch it off again and start FanControl (see [Using it with L-Connect](../README.md#using-it-with-l-connect)).
- **Fans that pair up late appear by themselves.** A wireless fan that checks in a few seconds after a boot or a wake is added a few seconds later; the plugin asks FanControl to refresh. The very first time you run the plugin with a new wireless controller, before anything has checked in, you may see a temperature called "Lian Li: waiting for devices" with no reading. It goes away as soon as the fans appear. The same happens on a first run if a wired controller is slow to open or a TL hub has not heard from its fans yet.
- **A fan group that goes quiet** reads 0 rpm and is not sent anything until it is heard again, as in L-Connect; its controls and bindings stay. Sensors nothing has reported for 30 days are let go.
- **The screens.** On a wireless cooler the screen keeps your colours, brightness, rotation and theme, but stops showing the CPU and GPU figures. Those numbers have to be sent to the cooler along with the pump speed, and the plugin has none to send, so it hides them rather than leave wrong ones frozen on the screen. The screens on the TL FLEX LCD and SL-INF FLEX LCD fans are the same: they keep the theme, brightness and rotation you saved in L-Connect and get the date and time, but no CPU or GPU figures.
- **Lighting.** The Lighting build replays the look you saved for each wireless device (L-Connect renders the effect on the PC and saves the result, and the plugin streams that back as L-Connect would). A device you switched to your motherboard's ARGB header in L-Connect is handed over to it instead, and the TL FLEX LCD and SL-INF FLEX LCD screens are switched back onto the theme you saved if one was left showing streamed content.
- **Only one pair of dongles** is driven, as L-Connect drives one. Any further dongle is logged and left alone.

Everything in [wireless.md](wireless.md) was read out of L-Connect's own binaries and the tests check every byte against them, but it has not been confirmed on real wireless hardware yet. If you have some, an issue with your plugin log is very welcome.

## The FLEX fans: USB, L-Wireless or the motherboard

A FLEX chain hangs off a small receiver that has both a radio and a USB port, so it can be driven three ways, and the plugin follows L-Connect 3 (2.1.29) in each.

- **Plugged into USB**, the TL FLEX, TL FLEX LCD, SL-INF FLEX LCD and P28 V2 receivers are driven directly: one control for the chain (L-Connect sets a chain to one speed too) with an RPM reading for every fan on it. The receiver's own minimums apply: a TL FLEX never goes below 11%, an SL-INF FLEX LCD below 10%, a P28 V2 below 8%, and 0% is sent as 5% (1% on the P28 V2), exactly as L-Connect sends them. There is no stop. The Lighting build does not replay a FLEX look over USB, because L-Connect renders those on the PC and saves only the settings.
- **Paired to an L-Wireless SYNC dongle**, the same chain is driven over the radio like any other wireless fan, with its lighting replayed as above.
- **Either way it has the same control and fan ids**, because both are keyed on the receiver's radio address, so a chain you move between its USB cable and the dongles keeps its curves. If a receiver is on USB and paired to your dongle at the same time, the plugin does what L-Connect does: the dongle drives it and nothing is sent over USB (the log says `left to the radio`). Unpair it in L-Connect to drive it over USB. A chain that changes hands while FanControl is running keeps its curve either way, and FanControl is asked to refresh so the control shows which path drives it now.
- **Set to follow the motherboard's fan header** in L-Connect, a receiver is taken off it by the plugin's first speed write, as L-Connect's next write would.
- The plain **SL-INF FLEX, SL FLEX and CL FLEX** receivers are not driven over USB, because L-Connect 2.1.29 gives them no fan control there either; they are wireless only, and their receivers are left alone.
- The screens on the LCD variants are separate devices and are never touched.

## What is left alone

- Every LCD and OLED screen: the TL LCD, the GALAHAD II Vision, the HydroShift LCD, the HydroShift II OLED Curve (its screen, screen motor and streamed lighting), the Lancool 207 display, the TL FLEX LCD and SL-INF FLEX LCD screens, and the Universal 8.8-inch panel. On the coolers the plugin drives the fans, pump and saved lighting it can reach and leaves the screen as it is.
- Pairing, unpairing and moving a wireless device between radio channels. That stays L-Connect's job.
- Anything the plugin does not recognise. An unknown device gets no writes at all, and the Lighting build never sends a guessed look.
