# Changelog

All notable changes to this project will be documented in this file.

The format is inspired by [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

<br>

## [1.1.3] - 2026-10-04

### Changed

- Improved taskbar thumbnail controls with clearer icons and dynamic Pause/Resume and Transparent Mode states.
- Improved the shortcut change dialog with a clearer shortcut capture interface and explicit Save and Cancel actions.
- Aligned the shortcut menu order with the taskbar controls for consistency.

### Fixed

- Fully fixed mouse overlay updates during high-frequency touchpad scrolling, which were not completely resolved by the fix in v1.1.2.

<br>

## [1.1.2] - 2026-10-02

### Changed

- Improved overlay Z-ordering to keep the overlay reliably on top.
- Improved mouse overlay efficiency by avoiding unnecessary SVG processing.

### Fixed

- Fixed mouse overlay updates during high-frequency touchpad scrolling.

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
