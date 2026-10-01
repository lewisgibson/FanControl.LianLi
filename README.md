# FanControl.LianLi

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE) [![Latest release](https://img.shields.io/github/v/release/lewisgibson/FanControl.LianLi?sort=semver)](https://github.com/lewisgibson/FanControl.LianLi/releases/latest)

A [FanControl](https://getfancontrol.com/) plugin for Lian Li fans, AIO coolers and STRIMER cables. Put your fans and pumps on a temperature curve, see their RPM, and keep your L-Connect lighting without L-Connect running.

This is an unofficial, community plugin. It is not affiliated with, authorised by, or endorsed by Lian Li or by the FanControl project.

## Requirements

Windows and FanControl V243 or newer.

## Supported devices

| Device                   | Speed Control | RPM Sensor | Lighting |
| ------------------------ | :-----------: | :--------: | :------: |
| GALAHAD II LCD           |      ✅       |     ✅     |    ✅    |
| GALAHAD II Trinity       |      ✅       |     ✅     |    ✅    |
| GALAHAD II Vision        |      ✅       |     ✅     |    ✅    |
| HydroShift II            |      ✅       |     ✅     |    ✅    |
| HydroShift II OLED Curve |      ✅       |     ✅     |          |
| HydroShift LCD           |      ✅       |     ✅     |    ✅    |
| LANCOOL 217 INFINITY     |      ✅       |     ✅     |    ✅    |
| STRIMER Plus             |               |            |    ✅    |
| STRIMER Plus V2          |               |            |    ✅    |
| STRIMER Wireless         |               |            |    ✅    |
| UNI FAN AL               |      ✅       |     ✅     |    ✅    |
| UNI FAN AL V2            |      ✅       |     ✅     |    ✅    |
| UNI FAN CL               |      ✅       |     ✅     |    ✅    |
| UNI FAN CL FLEX          |      ✅       |     ✅     |    ✅    |
| UNI FAN P28 V2           |      ✅       |     ✅     |    ✅    |
| UNI FAN SL               |      ✅       |     ✅     |    ✅    |
| UNI FAN SL FLEX          |      ✅       |     ✅     |    ✅    |
| UNI FAN SL Redragon      |      ✅       |     ✅     |    ✅    |
| UNI FAN SL V2            |      ✅       |     ✅     |    ✅    |
| UNI FAN SL V3            |      ✅       |     ✅     |    ✅    |
| UNI FAN SL-INF FLEX      |      ✅       |     ✅     |    ✅    |
| UNI FAN SL-INF FLEX LCD  |      ✅       |     ✅     |    ✅    |
| UNI FAN SL-INF Wireless  |      ✅       |     ✅     |    ✅    |
| UNI FAN SL-Infinity      |      ✅       |     ✅     |    ✅    |
| UNI FAN TL               |      ✅       |     ✅     |    ✅    |
| UNI FAN TL FLEX          |      ✅       |     ✅     |    ✅    |
| UNI FAN TL FLEX LCD      |      ✅       |     ✅     |    ✅    |
| UNI FAN TL V2            |      ✅       |     ✅     |    ✅    |
| V150                     |      ✅       |     ✅     |    ✅    |

The FLEX and P28 V2 lighting is kept when they're paired to an L-Wireless SYNC controller, not on their own USB receiver. More detail on each device is in [docs/devices.md](docs/devices.md).

## Which download?

Install only one of these.

| Build            | Download                                | Use It If                        |
| ---------------- | --------------------------------------- | -------------------------------- |
| 🎨&nbsp;Lighting | `FanControl.LianLi-Lighting-vX.Y.Z.zip` | You use L-Connect (most people)  |
| 🌈&nbsp;ARGB     | `FanControl.LianLi-Argb-vX.Y.Z.zip`     | Your motherboard runs your RGB   |
| 🌀&nbsp;Standard | `FanControl.LianLi-vX.Y.Z.zip`          | OpenRGB or similar runs your RGB |

## Install

1. Download your zip from the [latest release](https://github.com/lewisgibson/FanControl.LianLi/releases/latest) and extract the `.dll`.
2. Right-click the `.dll`, choose Properties, tick Unblock and click OK.
3. In FanControl, open the menu, click Install plugin and pick the `.dll`.

To upgrade, do the same with the new version. Your curves stay as they are.

## Using it with L-Connect

L-Connect and the plugin can't run at the same time, so set everything up in L-Connect first and then switch it off.

1. Install [L-Connect 3](https://lian-li.com/l-connect3/), pair your fans and set up your lighting.
2. Exit L-Connect from its tray icon.
3. Open PowerShell as administrator and paste this in:

   ```powershell
   Disable-ScheduledTask -TaskPath "\LianLi\" -TaskName "L-Connect 3" -ErrorAction SilentlyContinue
   Stop-Service -Name LConnectServiceWatcher -ErrorAction SilentlyContinue
   Set-Service -Name LConnectServiceWatcher -StartupType Disabled -ErrorAction SilentlyContinue
   Stop-Service -Name LConnectService
   Set-Service -Name LConnectService -StartupType Disabled
   Restart-Service -Name FanControl.Service
   ```

4. Open FanControl.

To change something in L-Connect later, exit FanControl and run this, then open L-Connect:

```powershell
Stop-Service -Name FanControl.Service
Set-Service -Name LConnectService -StartupType Manual
Start-Service -Name LConnectService
```

When you're done, close L-Connect and repeat steps 3 and 4.

## Troubleshooting

See [docs/troubleshooting.md](docs/troubleshooting.md).

## Contributing

Contributions are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) explains how to build and test the plugin.

## Acknowledgements

Most of the device support is now taken directly from Lian Li's own L-Connect 3 by decompiling it and reimplementing what it sends. No Lian Li code is shipped, only the protocol facts. The original UNI FAN protocol work came from prior open-source projects, and this plugin reuses their protocol facts (report ids, byte offsets, duty formulas) , not their source code:

- [uni-sync](https://github.com/EightB1ts/uni-sync) by Cameron Halter, the Rust sync tool the protocol facts were first taken from (MIT).
- [FanControl.LianLi](https://github.com/EightB1ts/FanControl.LianLi) by Cameron Halter, the original FanControl plugin that inspired this one (LGPL-2.1).
- [liquidctl](https://github.com/liquidctl/liquidctl), used as a protocol reference (GPL-3.0-or-later).

## License

MIT, see [LICENSE](LICENSE). The plugin ships no third-party components; see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
