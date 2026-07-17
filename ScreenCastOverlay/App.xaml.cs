using ModernWpf;
using System.Diagnostics;
using System.Windows;

namespace KeyClickOverlay
{
    /// <summary>
    /// WPF application bootstrap: command line handling, theme wiring, and remote toggle.
    /// </summary>
    public partial class App : Application
    {
        // === Constants ===
        private const string ToggleArg = "/toggle";
        private const int WM_TOGGLE_CLICKTHROUGH = 0x8001; // must match MainWindow WndProc

        // === App Startup ===

        /// <summary>Handles command-line args and wires theme before creating MainWindow.</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            // Remote toggle path: signal existing window and exit early.
            if (e.Args.Length > 0 && string.Equals(e.Args[0], ToggleArg, StringComparison.OrdinalIgnoreCase))
            {
                ToggleClickThroughExistingWindow();
                Shutdown();
                return;
            }

            // Follow OS Light/Dark + Accent (null => follow system).
            ThemeManager.Current.ApplicationTheme = null;
            ThemeManager.Current.AccentColor = null;

            base.OnStartup(e); // Let WPF create MainWindow from App.xaml
        }

        // === Remote Toggle (single-instance helper) ===

        /// <summary>Finds the first running instance with a window and posts the toggle message.</summary>
        private static void ToggleClickThroughExistingWindow()
        {
            // Use the current process name so this still works if the exe is renamed.
            string name = Process.GetCurrentProcess().ProcessName;

            foreach (var proc in Process.GetProcessesByName(name))
            {
                // Skip if it has no main window (e.g., this helper instance).
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero)
                    continue;

                // Post the custom message and stop after the first valid window.
                _ = NativeMethods.PostMessage(hwnd, WM_TOGGLE_CLICKTHROUGH, IntPtr.Zero, IntPtr.Zero);
                break;
            }
        }
    }
}
