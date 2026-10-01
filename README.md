# SimpleMacFan

A small fan control utility for Intel Macs running Windows through Boot Camp.

It shows fan speeds and temperatures and lets you control each fan: raise its minimum speed,
make it follow a temperature sensor, or force it to an exact speed. It talks to the Mac's
System Management Controller (SMC) through Apple's own Boot Camp driver, so no extra
kernel driver is installed.

Developed and tested on an **iMac12,1 (21.5", Mid 2011)** running Windows 10 22H2.

## Features

- Live fan speeds (rpm) and temperatures from all known SMC sensors (CPU, GPU, chipset,
  memory, power supply, optical drive, LCD, ambient).
- Per-fan control modes:

  | Mode | What it does |
  |---|---|
  | **Auto (Apple default)** | The Mac controls the fan as usual. |
  | **Fixed minimum** | The fan never runs slower than the chosen rpm. The Mac can still speed it up. |
  | **Follow sensor** | The minimum speed rises linearly from Apple's minimum at *From °C* to the fan's maximum at *To °C*. |
  | **Forced speed** | The fan runs at exactly the chosen rpm, even below what the Mac would choose. |
  | **Forced, follow sensor** | Like *Follow sensor*, but the speed can also go below what the Mac would choose. |

- Sensor choice per fan, including a virtual "CPU or GPU (whichever is hotter)" sensor.
- Fans slow down smoothly (at most 150 rpm every 2 seconds) and speed up immediately.
- Tray icon shows the CPU temperature: white normally, orange from 70 °C, red from 85 °C.
- Optional start with Windows (in the tray, without a UAC prompt).
- Settings are saved automatically.

## Safety

- In the normal modes (*Auto*, *Fixed minimum*, *Follow sensor*), the app only **raises**
  the fan's minimum speed. It never goes below Apple's default minimum or above the fan's
  maximum, and the Mac's own thermal protection stays fully active.
- In the **forced** modes, the Mac no longer manages that fan. To make up for this, the app
  runs a safety cut-out that watches these sensors:

  | Sensor | Cut-out at | Forced control resumes below |
  |---|---|---|
  | GPU | 90 °C | 80 °C |
  | CPU, platform controller hub, memory, power supply | 85 °C | 75 °C |

  If any sensor reaches its limit, or a sensor can't be read, all forced fans go back to the
  Mac's control, which then speeds them up as needed. Forced control resumes only when every
  watched sensor has cooled to 10 °C below its limit. A tray notification and a red message
  in the window show when the cut-out starts and ends.
- Tip: for gaming or other heavy loads, use **Forced, follow sensor** with a range that
  ends below the cut-out (for example GPU 50 → 78 °C). A fan forced to a fixed low speed
  can't react to load, so it will reach the cut-out quickly.
- In *Follow sensor* modes, if the chosen sensor can't be read, the fan goes to full speed.
- On exit, Windows logoff or shutdown, the app turns off forced mode and restores Apple's
  default minimum speeds.

> **Warning:** Always quit through the tray menu (**Exit**). If the app is killed (for
> example from Task Manager) or crashes while a fan is forced, that fan stays at the
> forced speed without protection until the SMC is reset (normally at restart).

## Requirements

| Dependency | Notes |
|---|---|
| Intel Mac with Windows installed through Boot Camp | Tested on iMac12,1. Other Intel Macs with the same Boot Camp driver will probably work, but are untested. Macs with a T2 chip handle fans differently and may not work. |
| Windows 10 or 11, 64-bit | Tested on Windows 10 22H2 (build 19045). |
| Apple Boot Camp drivers (Boot Camp 6.x) | Provides `MacHALDriver.sys` (the *Mac HAL* service), which gives access to the SMC. Tested with driver version 6.200.1.7. |
| .NET Framework 4.5 or newer | Already included in Windows 10 and 11 (4.8). |
| Administrator rights | Needed to write to the SMC. The app asks for elevation when it starts. |

No third-party libraries or drivers are used.

## Usage

1. Run `SimpleMacFan.exe` and accept the UAC prompt.
2. Pick a mode for each fan and adjust the rpm or the temperature range.
   Changes apply within 2 seconds and are saved automatically.
3. Closing the window keeps the app running in the tray. Double-click the tray icon to open
   it again, or right-click it and choose **Exit (restore Apple defaults)** to quit.
4. Tick **Start with Windows (in the tray)** to start the app automatically when you log in.

### Command-line options

| Option | Effect |
|---|---|
| *(none)* | Opens the window. If the app is already running, brings its window to the front. |
| `/tray` | Starts hidden in the tray (used by the startup task). |
| `/exit` | Tells the running instance to restore Apple's defaults and quit. |

### Settings

Settings are stored in `%APPDATA%\SimpleMacFan\settings.ini`, one `fanN.setting=value`
line per setting:

| Key | Meaning |
|---|---|
| `fanN.applemin` | Apple's default minimum rpm, recorded the first time the app runs. Delete this line only while the app isn't running and the Mac's fans are at their defaults. |
| `fanN.mode` | `Auto`, `Fixed`, `Sensor`, `Forced` or `ForcedSensor` |
| `fanN.fixed` | rpm for *Fixed minimum* and *Forced speed* |
| `fanN.sensor` | SMC sensor key (for example `TCXc`, `TG0D`) or `CPU+GPU` |
| `fanN.from`, `fanN.to` | temperature range in °C for the sensor modes |

### Start with Windows

The checkbox creates a Task Scheduler task named **SimpleMacFan** that runs at logon with
highest privileges, so no UAC prompt appears. The task has no time limit and runs on battery.

## Building

No Visual Studio is needed. The C# compiler that comes with the .NET Framework is enough:

```
build.cmd
```

This runs `csc.exe` from `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` and produces
`SimpleMacFan.exe`. Close the running app before rebuilding.

### Files

| File | Purpose |
|---|---|
| `SimpleMacFan.cs` | All the source code (SMC access, fan logic, user interface). |
| `app.manifest` | Requests administrator rights and declares Windows 10 compatibility. |
| `build.cmd` | Build script. |
| `LICENSE` | GNU General Public License v3.0. |
| `SimpleMacFan.exe` | The compiled application. |

## How it works

Apple's Boot Camp driver `MacHALDriver.sys` creates the device `\\.\MacHALDriver` and
passes requests to the SMC through I/O ports `0x300` (data) and `0x304` (command).
The app uses these driver requests (IOCTLs), all buffered:

| IOCTL | Operation | Input | Output |
|---|---|---|---|
| `0x9C402454` | Read key | 4-byte key | key data (as many bytes as requested) |
| `0x9C402458` | Write key | 4-byte key, 1 length byte, data | — |
| `0x9C40245C` | Key at index | 32-bit index | 4-byte key, status byte |
| `0x9C402460` | Key info | 4-byte key, 1 byte | `[0]` size, `[4..7]` type, `[8]` attributes |

SMC keys used:

| Key | Type | Meaning |
|---|---|---|
| `FNum` | ui8 | number of fans |
| `FnID` | struct | fan description; the name starts at byte 4 |
| `FnAc` / `FnMn` / `FnMx` / `FnTg` | fpe2 | actual, minimum, maximum and target rpm of fan *n* |
| `FS! ` | ui16 | forced-mode bitmask (bit *n* set = fan *n* is forced) |
| `T***` | sp78 | temperatures in °C, for example `TCXc` (hottest CPU core), `TG0D` (GPU die) |

`fpe2` is an unsigned 14.2 fixed-point number (raw ÷ 4 = rpm). `sp78` is a signed 8.8
fixed-point number (raw ÷ 256 = °C). Both are big-endian.

## Troubleshooting

- **"Cannot open Apple's Boot Camp driver (MacHALDriver)"**: install the Boot Camp drivers
  for your Mac model (for example with Apple's Boot Camp Assistant or the Brigadier tool), then
  check that the *Mac HAL* driver is running: `sc query MacHALDriver`.
- **A fan stays very fast in Auto mode** (for example the HDD fan after replacing the hard
  drive with an SSD): the SMC can't read a drive temperature and runs that fan at high speed
  to be safe. Use a forced mode, or fit a drive temperature sensor cable such as the OWC
  In-line Digital Thermal Sensor.
- **Red "Safety" message or notification**: a watched sensor reached its limit (90 °C for the
  GPU, 85 °C for the others), so forced fans are back under the Mac's control until everything
  has cooled 10 °C below its limit. If this happens often, use a *follow sensor* mode whose
  range ends a few degrees below the limit.

## Uninstalling

1. Right-click the tray icon and choose **Exit (restore Apple defaults)**.
2. Remove the startup task: untick *Start with Windows* before exiting, or run
   `schtasks /Delete /TN SimpleMacFan /F` from an administrator command prompt.
3. Delete the program folder and `%APPDATA%\SimpleMacFan`.

## License

Copyright © 2026 ozaretskyi (ozaretskyi@proton.me)

SimpleMacFan is free software: you can redistribute it and/or modify it under the terms
of the **GNU General Public License version 3** or (at your option) any later version, as
published by the Free Software Foundation. See the [LICENSE](LICENSE) file for the full text.

In short: you may use, study, modify and share this program, including commercially. If you
distribute it or a modified version, you must make the source code available under the same
license and keep the copyright and license notices.

## Disclaimer

This program is distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**;
without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
Controlling fan speeds, especially in the forced modes, can cause overheating and hardware
damage if misused. Use it at your own risk.

Apple, Mac, iMac and Boot Camp are trademarks of Apple Inc., registered in the U.S. and
other countries. SimpleMacFan is an independent project and is not affiliated with,
endorsed by or sponsored by Apple Inc. or CrystalIDEA (Macs Fan Control).
