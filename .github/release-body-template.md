**FanControl.LianLi v{{VERSION}}**, released {{DATE}}

A [FanControl](https://getfancontrol.com/) plugin for Lian Li fans, AIO coolers and STRIMER cables. It needs FanControl V243 or newer, and the [README](https://github.com/lewisgibson/FanControl.LianLi#readme) lists the supported devices.

## Download and install

Download one of these:

- 🎨 `FanControl.LianLi-Lighting-v{{VERSION}}.zip` if you set up your lighting in L-Connect. Most people want this one.
- 🌈 `FanControl.LianLi-Argb-v{{VERSION}}.zip` if your motherboard's RGB software controls your fan lighting.
- 🌀 `FanControl.LianLi-v{{VERSION}}.zip` if OpenRGB, SignalRGB or another tool controls your lighting.

1. Extract the `.dll` from the zip.
2. Right-click the `.dll`, choose Properties, tick Unblock and click OK.
3. In FanControl, open the menu, click Install plugin and pick the `.dll`.

If you copied an older ARGB or Lighting `.dll` into FanControl's `Plugins` folder by hand, you don't need to remove it. The plugin takes it out of the folder itself and takes over at FanControl's next refresh, or straight away if you restart FanControl. If you copied the Standard `FanControl.LianLi.dll` into the `Plugins` folder itself by hand, close FanControl and delete that copy before installing.

If you use L-Connect, [switch it off first](https://github.com/lewisgibson/FanControl.LianLi#using-it-with-l-connect). If something isn't working, see [troubleshooting](https://github.com/lewisgibson/FanControl.LianLi/blob/main/docs/troubleshooting.md).

## What's changed
