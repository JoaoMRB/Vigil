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
