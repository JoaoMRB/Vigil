# Vigil

Your PC on a small USB screen: clock, CPU and GPU temperatures, memory, network, weather and world clocks.
Runs in the background on Windows. You don't need a browser or an account.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4.svg)
![.NET 9](https://img.shields.io/badge/.NET-9-512bd4.svg)

<p align="center">
  <img src="docs/images/system-dashboard.webp" alt="The Dashboard design, drawn by the app with sample numbers" width="480">
</p>

> Vigil started as the Windows app of [AuraTime](https://github.com/JoaoMRB/AuraTime), a focus clock for the
> browser. It now goes on here on its own. If you used the AuraTime app, Vigil picks up your settings the
> first time it runs.

## Supported screens

Vigil works with the 3.5" USB screens sold as **Turing Smart Screen** or **UsbMonitor** ("rev. A", USB `1A86:5722`,
480×320). Vigil is an independent project and is not affiliated with the makers of these screens.

## Download

**[Vigil-Setup.exe](https://github.com/JoaoMRB/Vigil/releases/latest/download/Vigil-Setup.exe)** ·
Windows 10 and 11, 64-bit · no administrator rights needed to install

To update, run the new installer over the old one. Your settings are kept.

## What it shows

| Clock | System | Weather | World clocks |
| :---: | :---: | :---: | :---: |
| ![Clock view](docs/images/view-clock.webp) | ![System view](docs/images/system-panel.webp) | ![Weather view](docs/images/view-weather.webp) | ![World clocks view](docs/images/view-world.webp) |

- **Views:** clock; system; weather with the quote of the day; and world clocks. The screen can show one
  view, or take turns between several.
- **System:** CPU and GPU temperature and load, memory, disk, network speed and volume. There are six designs
  (Dashboard, Full panel, Gauges, Bars, Graphs and Minimal), and you can change each design's colours.
- **CPU temperature:** Windows only shows this to programs that run as administrator and use a driver. To see
  it, turn on *CPU temperature* in the System settings and install the free [PawnIO](https://pawnio.eu/)
  driver. Without these, Vigil shows the CPU load instead.
- **Start with Windows:** Vigil waits in the notification area and draws on the screen as soon as the screen
  is plugged in.

## Privacy

The only thing Vigil gets from the internet is the weather, from [Open-Meteo](https://open-meteo.com/), for the
city you pick. Before it is sent, your position is rounded to about 1 km. Vigil has no analytics and no
telemetry. Your settings stay in `%AppData%\Vigil\settings.json`.

## Building

You need the .NET 9 SDK. The installer also needs Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet run --project Vigil          # run the app
.\build-installer.ps1               # writes dist\Vigil-Setup-<version>.exe and dist\Vigil-Setup.exe
```

To publish a version, attach `Vigil-Setup.exe` to a GitHub release that is **not** marked as a pre-release. The
download link points at the latest release, and GitHub skips pre-releases when it picks that one.

Tools for checking your work, with no screen attached:

```powershell
Vigil.exe --png out.png --view system --design dashboard --sample   # draw one screen to a file
Vigil.exe --sensors sensors.txt                                     # list every sensor found on this PC
```

## Project structure

```
Vigil/
  Display/     the USB screen's serial protocol and connection
  Render/      the drawing (SkiaSharp): views, system designs and themes
  Ui/          the settings window, a page shown in WebView2
  i18n/        the texts on the screen, in en, pt, es, fr, de and it
  Sensors.cs   PC sensors (LibreHardwareMonitor)
  Settings.cs  the settings file
installer/     Inno Setup script
```

Built with C# on .NET 9, [SkiaSharp](https://github.com/mono/SkiaSharp),
[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
[NAudio](https://github.com/naudio/NAudio) and WebView2.

## License

[MIT](LICENSE). The fonts are under the SIL Open Font License, see [Vigil/Ui/fonts/README.md](Vigil/Ui/fonts/README.md).
