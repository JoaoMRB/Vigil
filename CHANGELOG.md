# Changelog

All notable changes to Vigil are listed here. Versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- The Windows app of [AuraTime](https://github.com/JoaoMRB/AuraTime) is now **Vigil**, with a repository of its own.
- Vigil installs as a separate app: the AuraTime app can be uninstalled afterwards.
- "Start with Windows" is now listed under Startup apps in Task Manager in administrator mode too, and can be
  switched on and off there. The app shows it as off when Windows has it switched off.

### Added
- The first time it runs, Vigil copies the settings from `%AppData%\AuraTime`, so you don't have to set it up again.
- Vigil has its own icon: a screen with a lined eye in it. The Dashboard design shows it in place of AuraTime's clock,
  and the settings window next to the name.
- The screen follows the PC: it dims to just the time while the PC is locked (or turns off, or keeps going, as set
  under System), turns off before the PC sleeps, and comes back by itself on wake-up, without waiting for an error.
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

## AuraTime app 0.1.0

The last version under the AuraTime name. It added:
- the clock, system, weather and world clock views, which can take turns on the screen
- six system designs, each with colours you can change
- CPU temperature in administrator mode, with the PawnIO driver
- the settings window, shown in WebView2
- the installer
