# Changelog

All notable changes to Vigil are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Geist Mono, a fifth clock font. Every digit is as wide as the others, so the time and the readings don't shift
  sideways as they change. A "°" sits next to its number, not a digit's width away.

### Changed
- Dark Glass is the default theme and comes first in the list, in Vigil's own graphite and silver: its accent is
  silver instead of AuraTime's gold. The themes that came from AuraTime are still there. A theme you chose before
  stays as it is.
- Vigil takes about 25 times less CPU to read the sensors. An NVIDIA card is read straight through the driver's NVML
  for just what the screen shows: the library read much more on every update (PCIe traffic, every Direct3D
  engine...), about 30 ms of CPU every two seconds on an RTX 3070 Ti. Only the sensors the chosen design shows are
  read (the drives' and the motherboard's only when a design shows them), the drives every 30 seconds instead of
  every two, and nothing at all while the PC is locked and the screen shows only the time or is off.

### Fixed
- A crash or a power cut while Vigil saved its settings could leave the file cut off, and Vigil would then start
  over from the defaults. The settings are now written to a file of their own first and only then put in place, so
  the old ones stay whole until the new ones are complete.
- In administrator mode, installed for all users, the settings window stayed black and Edge said it couldn't read
  and write to its data directory. Its browser runs with your ordinary rights even then, so it couldn't use the
  cache in the install folder; the cache is now in your own folder (`%LocalAppData%\Vigil\WebView2`), as in every
  other case.

## [0.3.0] - 2026-11-17

### Changed
- Vigil uses about 5 MB less memory in the tray: it clears its short-lived garbage every 30 seconds instead of
  letting about 11 MB pile up first, and reuses the buffer it sends each part of a picture to the screen in.

### Added
- The settings window, the tray menu and its notifications in Portuguese, Spanish, French, German and Italian, as
  well as English, following Windows' language like the screen. The texts are read when the window opens and let go
  when it closes, so they take no memory while Vigil sits in the tray.
- `--memory-test`, for developers: the memory the app takes over time, as it runs in the tray.
- `tools/check-i18n.mjs`, for developers: checks that every language has every text, with the same placeholders.

## [0.2.0] - 2026-11-17

### Added
- A banner at the top of the settings window when a newer version is available: what changed in it (the release's
  own notes, shown as plain text), a link to the release on GitHub, and an *Update* button that does what the tray's
  does. *Later* hides it until the window is opened again.

## [0.1.0] - 2026-11-17

### Changed
- The Windows app of [AuraTime](https://github.com/JoaoMRB/AuraTime) is now **Vigil**, with a repository of its own.
- Vigil installs as a separate app: the AuraTime app can be uninstalled afterwards.
- "Start with Windows" is now listed under Startup apps in Task Manager in administrator mode too, and can be
  switched on and off there. The app shows it as off when Windows has it switched off.
- The tray icon's menu is dark, in the colours of the settings window, instead of the system's.
- The installer asks whether to install for you only or for all users. Administrator mode starts without a Windows
  prompt only when Vigil is installed for all users, under Program Files. Installed for you only, Windows asks each
  time Vigil starts: any program could change those files, so a copy started with administrator rights without a
  prompt could have been replaced. A start-up task left by an earlier version for such a copy is removed and no
  longer used. Running the installer again for all users removes the copy installed for you only.
- An update checks the downloaded installer once more just before it runs, and keeps it locked until then, so it
  can't be swapped after the check.
- In administrator mode, Vigil writes its settings, its log and the update's download with your ordinary rights.
  A program that swapped one of those folders for a link to a folder of Windows can no longer get Vigil to change
  Windows' files. The settings window's browser keeps its cache in the install folder, and the start-up task is
  registered without a temporary file. *Get PawnIO* opens the browser as you, not as administrator.

### Added
- Quick controls under the preview: keep one view on the screen instead of the views taking turns (with the next
  view and when it comes), the brightness (moved there from Appearance, with a note while the night's is lower), and
  a switch to turn the screen off for now. None of these is saved: Vigil starts with the views taking turns and the
  screen on.
- The first time it runs, Vigil copies the settings from `%AppData%\AuraTime`, so you don't have to set it up again.
- Vigil has its own icon: a screen with a lined eye in it. The Dashboard design shows it in place of AuraTime's clock,
  and the settings window next to the name.
- The screen follows the PC: it dims to just the time while the PC is locked (or turns off, or keeps going, as set
  under System), turns off before the PC sleeps, and comes back by itself on wake-up, without waiting for an error.
- A guide to the CPU temperature in the settings window. It shows when a view needs the temperature and doesn't get
  it, says what is missing (the mode, administrator rights or the PawnIO driver), and has a button for each step and
  a tick as each one is done. *Check again* finds a newly installed driver without restarting. The Custom design's
  cards say what is missing too.
- *Approximate CPU temperature* under System, off by default: without the driver, the motherboard's ACPI thermal
  zone stands in, marked `~`. A zone that stays put for five minutes while the CPU load swings is dropped.
- *How the screen stands* under Appearance: landscape, upside down, or upright turned either way. Upright, the
  clock stacks the hours over the minutes, and the weather, world clocks and now playing views have layouts of their
  own. The preview in the window stands the same way. So do the seven system designs: the CPU over the GPU, the
  meters and charts as wide as the screen, and the Custom design's cards two across.
- Updates: once a day Vigil asks GitHub for the latest release. A newer one shows in the tray, with a notification.
  Choosing it downloads the installer, checks it against the SHA-256 GitHub lists, and installs it silently, and then
  Vigil starts again. You can switch the check off with *Check for updates* under System.
- A *Custom* system design: six cards, and you pick what each one shows. Besides the readings the other designs
  use, it can show the CPU and GPU clocks, their power, and their fans (the CPU fan in administrator mode).
- A *Now playing* view: the cover, title, artist, album and progress of what plays, from Windows' media controls
  (Spotify, browsers, the Media Player and others). It takes its turn only while something plays. Titles in other
  scripts, such as Japanese or Cyrillic, are drawn with a Windows font that has them.
- Vigil now needs Windows 10 version 2004 or later, for the media API.
- *During a full-screen game* under Views: after 5 seconds of a game or video filling the PC's screen, the display
  shows the system view (or the clock), and goes back to your views 5 seconds after you leave it.
- *Dim at night* under Appearance: the backlight goes down between two times, such as 22:00 to 07:00. While the PC is
  locked, the lower of the two brightness levels applies.
- The display is found the moment its cable goes in, and a pulled cable is noticed at once instead of after a
  failed write, and shown as not found rather than as an error, even in the moment Windows still lists its port.
  While the PC sleeps the status reads "Paused while the PC sleeps". When connecting keeps failing, Vigil tries again after 1, 2, 5, 10 and then every 30 seconds, rather
  than every 5. Every 5 minutes the whole picture is sent again, so a byte lost on the way cannot leave a damaged
  band on the screen for good.
- *Diagnostics → Copy* in the System settings: a report for bug reports, with the versions, the display's state, the
  settings and the end of the log. No city, user name, hardware names or readings.
- `--simulate` and `--simulate-test`: a pretend display, to try the cable, a stalled link, locking and sleeping
  without the hardware, by hand or as an automatic check.
- A log of what the app did and the errors it met, in `%LocalAppData%\Vigil\logs`. It stays on the PC, holds no
  personal data, and keeps itself small.
- A website for the app in English, Portuguese, Spanish and French, in `site/`.

### Fixed
- Vigil no longer logs an error each time it quits.

## AuraTime app 0.1.0

The last version under the AuraTime name. It added:
- the clock, system, weather and world clock views, which can take turns on the screen
- six system designs, each with colours you can change
- CPU temperature in administrator mode, with the PawnIO driver
- the settings window, shown in WebView2
- the installer
