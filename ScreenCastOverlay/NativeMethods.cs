using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KeyClickOverlay
{
    /// <summary>
    /// Contains platform interop calls to native Windows APIs using [LibraryImport] (source-generated P/Invoke) for modern, efficient marshalling.
    /// </summary>
    public static partial class NativeMethods
    {
        // === User32: constants ===
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_SIZE = 0xF000;

        // Virtual keys used by eyedropper helpers
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;

        // MessageBeep types (same set used by MessageBox)
        internal const uint MB_ICONHAND = 0x00000010;  // Error
        internal const uint MB_ICONQUESTION = 0x00000020;  // Question
        internal const uint MB_ICONEXCLAMATION = 0x00000030;  // Warning
        internal const uint MB_ICONASTERISK = 0x00000040;  // Information

        // Index for Get/SetWindowLong on extended styles + the style flags we care about
        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;    // hides from taskbar
        internal const int WS_EX_APPWINDOW = 0x00040000;     // shows on taskbar
        internal const int WS_EX_TRANSPARENT = 0x00000020;   // click-through hit testing
        internal const int WS_EX_LAYERED = 0x00080000;       // required companion style for transparent overlays


        // === User32: P/Invoke ===

        /// <summary>Sends a message to a window (synchronous, Unicode).</summary>
        [LibraryImport("user32.dll", EntryPoint = "SendMessageW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

        /// <summary>Posts a message to a window queue (asynchronous, Unicode).</summary>
        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint hWnd, int msg, nint wParam, nint lParam);

        /// <summary>Returns async key state (high bit set = key down).</summary>
        [SuppressGCTransition]
        [LibraryImport("user32.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial short GetAsyncKeyState(int vKey);

        /// <summary>Plays a simple system beep.</summary>
        [LibraryImport("user32.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int MessageBeep(uint uType);


        // === Eyedropper: screen DC & pixel ===

        [LibraryImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint GetDC(nint hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int ReleaseDC(nint hWnd, nint hDC);

        [LibraryImport("gdi32.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial uint GetPixel(nint hdc, int nXPos, int nYPos);

        /// <summary>Reads the screen color at (x,y) in screen coordinates.</summary>
        public static Color GetColorAtScreenPixel(int x, int y)
        {
            const nint HWND_DESKTOP = 0;
            nint hdc = GetDC(HWND_DESKTOP);
            try
            {
                // COLORREF = 0x00BBGGRR
                uint c = GetPixel(hdc, x, y);
                return Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
            }
            finally
            {
                _ = ReleaseDC(HWND_DESKTOP, hdc);
            }
        }


        // === Input helpers (eyedropper) ===

        /// <summary>Returns true while left mouse button is down.</summary>
        public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        /// <summary>Returns true while right mouse button is down.</summary>
        public static bool IsRightButtonDown() => (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;


        // === DWM (Desktop Window Manager) ===

        [LibraryImport("dwmapi.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

        // DWM attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // some older builds use 19
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // Win11+

        // Corner preferences
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        /// <summary>Applies Win11 rounded corners (no-op if unsupported).</summary>
        public static void TryApplyWin11RoundedCorners(Window window, bool small = false)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == 0) return;

                int pref = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* ignore on unsupported systems */ }
        }

        /// <summary>Applies immersive dark title bar if available.</summary>
        public static void TryApplyImmersiveDarkTitleBar(Window window, bool dark)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == 0) return;

                int useDark = dark ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
            catch { /* ignore on unsupported systems */ }
        }

        // === Window styles (Get/SetWindowLong) ===
        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int GetWindowLong(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

        // === Z-Order / Topmost ===

        [LibraryImport("user32.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetWindowPos(
            nint hWnd, nint hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        // SetWindowPos flags
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private const uint SWP_NOSENDCHANGING = 0x0400;

        // Special handles
        private const nint HWND_TOPMOST = -1;

        /// <summary>Ensure the window shows in the taskbar (APPWINDOW) and is not a tool window.</summary>
        public static void EnsureAppWindow(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0) return;

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex &= ~WS_EX_TOOLWINDOW;   // clear toolwindow bit (keeps you off the taskbar)
            ex |= WS_EX_APPWINDOW;    // force appwindow bit (gives you the taskbar icon)
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);
        }

        /// <summary>Re-asserts TopMost without stealing focus or moving the window.</summary>
        public static void EnsureTopMost(Window window)
        {
            if (window is null) return;
            if (window.WindowState == WindowState.Minimized) return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0) return;

            _ = SetWindowPos(
                hwnd,
                HWND_TOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING);
        }

        // === Convenience ===

        /// <summary>Triggers native resize via SC_SIZE (direction is HT edge/corner offset 0..8).</summary>
        public static void NativeResizeWindow(nint handle, int direction)
        {
            if (handle == 0) return;
            SendMessage(handle, WM_SYSCOMMAND, (nint)(SC_SIZE + direction), 0);
        }

        /// <summary>
        /// Enables or disables true native click-through on the window.
        /// When enabled, Windows passes mouse hit-testing through this HWND to whatever is underneath.
        /// </summary>
        public static void SetWindowClickThrough(Window window, bool enabled)
        {
            if (window is null) return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0) return;

            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);

            if (enabled)
            {
                ex |= WS_EX_LAYERED;
                ex |= WS_EX_TRANSPARENT;
            }
            else
            {
                ex &= ~WS_EX_TRANSPARENT;
            }

            _ = SetWindowLong(hwnd, GWL_EXSTYLE, ex);

            _ = SetWindowPos(
                hwnd,
                0,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
}