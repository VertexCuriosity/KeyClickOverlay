# Third-Party Notices

KeyClickOverlay is licensed under the **GNU General Public License v3.0 or later (GPL-3.0-or-later)**.

The GPL license applies only to the **original KeyClickOverlay source code**. Third-party libraries used by this project remain the intellectual property of their respective authors and continue to be licensed under their own respective licenses.

KeyClickOverlay makes use of several open-source libraries developed by third parties. Their work is greatly appreciated and has made this project possible.

The original license text for each direct dependency is included in the [`third_party_licenses`](third_party_licenses/) directory of this repository.

---

## Direct Dependencies

| Package | License | License File |
|----------|---------|--------------|
| Microsoft.WindowsAPICodePack.Shell | Microsoft Software License | [`Microsoft-WindowsAPICodePack.txt`](third_party_licenses/Microsoft-WindowsAPICodePack.txt) |
| ModernWpfUI | MIT | [`ModernWpfUI.txt`](third_party_licenses/ModernWpfUI.txt) |
| MouseKeyHook | MIT | [`MouseKeyHook.txt`](third_party_licenses/MouseKeyHook.txt) |
| PixiEditor.ColorPicker | MIT | [`PixiEditor.ColorPicker.txt`](third_party_licenses/PixiEditor.ColorPicker.txt) |
| SharpVectors.Wpf | BSD 3-Clause | [`SharpVectors.Wpf.txt`](third_party_licenses/SharpVectors.Wpf.txt) |

---

## Transitive Dependencies

The following packages are installed automatically through NuGet as dependencies of the libraries listed above and are not referenced directly by KeyClickOverlay.

| Package | Notes |
|----------|-------|
| Microsoft.WindowsAPICodePack.Core | Part of the Windows API Code Pack project. Covered by the Windows API Code Pack license. |
| Microsoft.Xaml.Behaviors.Wpf | Dependency of ModernWpfUI. Licensed under the MIT License. |
| PixiEditor.ColorPicker.Models | Part of the PixiEditor.ColorPicker project. Covered by the PixiEditor.ColorPicker license. |

---

## SharpVectors.Wpf

The SharpVectors project distributes additional third-party components as part of its own distribution, including **Brotli** and **MinIoC**.

The licenses and notices for those bundled components are maintained by the SharpVectors project and are included in its upstream distribution.

This repository preserves the original BSD 3-Clause license for SharpVectors.Wpf in the [`third_party_licenses`](third_party_licenses/) directory.

---

## Acknowledgements

This project would not have been possible without the work of the many developers who contribute to the open-source ecosystem.

Thank you to all the authors and maintainers of the libraries used by KeyClickOverlay.
