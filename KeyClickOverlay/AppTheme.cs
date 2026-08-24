using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace KeyClickOverlay
{
    /// <summary>
    /// Central source for KeyClickOverlay's Windows light/dark theme state,
    /// shared application theme colors, and Windows theme-change notifications.
    /// </summary>
    internal static class AppTheme
    {
        private static bool _isListening;

        /// <summary>
        /// Raised when Windows changes its application light/dark theme.
        /// </summary>
        public static event EventHandler? Changed;

        /// <summary>
        /// Gets whether Windows is currently using the light app theme.
        /// </summary>
        public static bool IsLight
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

                    object? value = key?.GetValue("AppsUseLightTheme");

                    if (value is int useLight)
                        return useLight != 0;
                }
                catch
                {
                    // Fall back to light theme if Windows theme detection fails.
                }

                return true;
            }
        }

        /// <summary>
        /// Gets whether Windows is currently using the dark app theme.
        /// </summary>
        public static bool IsDark => !IsLight;

        // === Main Window Chrome ===

        public static Color WindowOutlineColor =>
            ParseColor(IsLight ? "#1A000000" : "#26FFFFFF");

        public static Color ChromeButtonForegroundColor =>
            ParseColor(IsLight ? "#505050" : "#B0B0B0");

        public static Color ChromeButtonHoverColor =>
            ParseColor(IsLight ? "#1A000000" : "#1AFFFFFF");

        public static Color ChromeButtonPressedColor =>
            ParseColor(IsLight ? "#33000000" : "#33FFFFFF");

        // === Context Menu ===

        public static Color MenuBackgroundColor =>
            ParseColor(IsLight ? "#F9F9F9" : "#2B2B2B");

        public static Color MenuForegroundColor =>
            ParseColor(IsLight ? "#111111" : "#EEEEEE");

        public static Color MenuHoverColor =>
            ParseColor(IsLight ? "#EDEDED" : "#3A3A3A");

        // === Theme Change Monitoring ===

        /// <summary>
        /// Starts listening for Windows theme changes.
        /// Safe to call more than once.
        /// </summary>
        public static void Start()
        {
            if (_isListening)
                return;

            SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
            _isListening = true;
        }

        /// <summary>
        /// Stops listening for Windows theme changes.
        /// </summary>
        public static void Stop()
        {
            if (!_isListening)
                return;

            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            _isListening = false;
        }

        private static void SystemEvents_UserPreferenceChanged(
            object sender,
            UserPreferenceChangedEventArgs e)
        {
            void RaiseChanged()
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }

            if (Application.Current?.Dispatcher is { } dispatcher &&
                !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(RaiseChanged);
            }
            else
            {
                RaiseChanged();
            }
        }

        // === Helpers ===

        private static Color ParseColor(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex)!;
        }
    }
}