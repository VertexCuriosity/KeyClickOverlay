# Changelog

All notable changes to this project will be documented in this file.

The format is inspired by [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

<br>

## [1.1.1] - 2026-09-01

### Changed

- Improved window positioning and sizing across different display scaling settings.
- Improved window position restoration when switching between monitors with different DPI scaling.
- Improved window positioning when overlapping the Windows taskbar.
- Improved preset window geometry handling across different monitors and DPI settings.
- Presets created with earlier versions should be saved again to store the updated monitor and DPI information.

### Fixed

- Fixed window position and size drifting when changing display scaling.
- Fixed incorrect window positioning after switching between different DPI scaling levels.
- Fixed taskbar overlap not being preserved correctly after DPI changes.
- Fixed window geometry entered through the context menu not being preserved correctly across DPI changes.

<br>

## [1.1.0] - 2026-08-28

### Added

- Added the ability to pause and resume the input display.
- Added Pause/Play controls to the taskbar thumbnail menu.

### Changed

- Updated KeyClickOverlay from .NET 8 to .NET 10.
- Improved the overall user interface and layout.
- Improved the color picker.
- Modernized application dialogs.
- Replaced ModernWPF with WPF-UI.
- Various smaller improvements and cleanup.

<br>

## [1.0.0] - 2026-07-17

### Added

- Initial public release of KeyClickOverlay.
- Configurable keyboard and mouse input overlay.
- Multiple customizable overlay presets.
- Global hotkey support.
- Customizable appearance and behavior.
- Windows 11 compatible.
- Open-source release under GPL-3.0-or-later.
