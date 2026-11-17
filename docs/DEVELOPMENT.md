# Developing Vigil

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). The installer also needs Inno Setup 6
(`winget install JRSoftware.InnoSetup`).

```powershell
dotnet run --project Vigil          # run the app
.\build-installer.ps1               # writes dist\Vigil-Setup-<version>.exe and dist\Vigil-Setup.exe
```

`build-installer.ps1` publishes the app self-contained to `publish\`, so the .NET runtime ships with it, and then
builds the installer from `installer/Vigil.iss`. Close a running Vigil first: Windows locks the files it uses.

## Project layout

```
Vigil/
  Display/          the screen: serial protocol, connection, a simulated link, USB device announcements
  Render/           the drawing (SkiaSharp): views, system designs, themes, the upright layouts
  Ui/               the settings window, a page shown in WebView2
  i18n/             the texts drawn on the screen, in en, pt, es, fr, de and it
  Sensors.cs        PC sensors (LibreHardwareMonitor) and the volume (NAudio)
  Media.cs          what is playing, from Windows' media controls
  GameDetector.cs   whether a game fills the screen
  Updater.cs        the daily update check and the silent install
  Settings.cs       the settings file and the start-up entry
  Simulator.cs      --simulate and --simulate-test
  Log.cs            the log in %LocalAppData%\Vigil\logs
installer/          Inno Setup script
site/               the website: static HTML and CSS (see site/README.md)
docs/brand/         the icon and the mark, as SVG
docs/images/        the README's pictures (see below)
```

## Checking a change without a screen

```powershell
Vigil.exe --png out.png --view system --design dashboard --sample   # draw one screen to a file
Vigil.exe --sensors sensors.txt                                     # list every sensor found on this PC
Vigil.exe --window-png out.png [--tall] [--script "js"]             # capture the settings window
Vigil.exe --simulate                                                # a pretend display in a window
Vigil.exe --simulate-test report.txt                                # the link logic, PASS or FAIL per case
Vigil.exe --update-test report.txt [--update-feed release.json]     # look for an update, download and verify it
Vigil.exe --elevation-test report.txt                               # as administrator: the user's files stay the user's
```

**`--elevation-test`** must run with administrator rights. It checks that, in administrator mode, the settings,
the log and the update's download are written with the user's ordinary rights (`AsUser.cs`): it links a folder in
the temp folder to one only administrators can write to, and checks that writing, replacing and reading through the
link are refused, and that the same write without `AsUser` would have gone through. It also registers a task named
`Vigil-selftest` the way the start-up task is registered, without a file, and removes it. It leaves nothing behind.

**`--png`** takes `--view` (clock, system, weather, world or music), `--design`, `--theme`, `--orientation`
(landscape, landscape-flipped, portrait-right or portrait-left), `--tiles` for the Custom design's cards
(`cpu-temp,gpu-fan,...`) and `--condition` to force a weather icon. With `--sample`, it uses made-up readings
instead of this PC's, and `--sample-names "CPU,GPU"` names their CPU and GPU.

**`--simulate`** opens a pretend display, with buttons to pull the cable, stall the link, refuse connections, lock
the PC, put it to sleep and start a full-screen game. **`--simulate-test`** runs those cases by itself in about 35
seconds, along with checks of the night dimming, the game profile, the music view's turn and the turned pictures. It
exits with 0 when every case passes. Neither touches the real display, the start-up entry or the settings file.

**`--update-test`** does everything an update does except run the installer. With `--update-feed`, it reads the
release from a local JSON file shaped like GitHub's, so the check can be tried before anything is published:

```json
{ "tag_name": "v9.9.9",
  "assets": [{ "name": "Vigil-Setup.exe", "browser_download_url": "Vigil-Setup.exe", "digest": "sha256:<hex>" }] }
```

**`--settings-file path`** makes any of these use another settings file than yours, and **`--culture en-US`**
draws in another language than Windows'.

## The README's pictures

Every screen picture in `docs/images` comes from one command. Each one uses made-up readings, a fixed time and
English, so none of them show anything of the PC they were made on:

```powershell
Vigil.exe --window-png shot.png --settings-file sample.json --culture en-US
Vigil.exe --readme-images docs\images --window shot.png [--photo desk.png]
```

`--window` turns the settings window's capture into `app-window.webp`. `--photo` turns a photo into `hero.webp`,
1600 px wide. The photo at the top of the README is a real desk with a picture drawn by `--png --sample` put onto the
screen. Whatever photo goes there, check it first for hardware names, readings or anything else personal.

## Releasing a version

1. Raise `<Version>` in `Vigil/Vigil.csproj`. The installer and the update check both read it.
2. Move the *Unreleased* entries in `CHANGELOG.md` under the new version.
3. Run `.\build-installer.ps1`.
4. Create a GitHub release tagged `v<version>` (for example `v0.2.0`) that is **not** marked as a pre-release, and
   attach `dist\Vigil-Setup.exe` under that exact name.

The README's download link and the app's update check both look for the latest release, and GitHub skips
pre-releases when it picks that one. The update check only offers a release whose `Vigil-Setup.exe` has a SHA-256
listed by GitHub, and GitHub lists one for every uploaded file.

## Conventions

- Text files use LF line endings, except where `.gitattributes` says otherwise.
- The app writes nothing personal to its log or its diagnostics: no city, no user name, no hardware names, no
  readings. `Log.Scrub` takes the user's name out of paths.
- In administrator mode, any file work in the user's folders (`%AppData%`, `%LocalAppData%`, the temp folder)
  goes through `AsUser.Run`, so it runs with the user's ordinary rights. Anything new that reads or writes there
  must do the same.
