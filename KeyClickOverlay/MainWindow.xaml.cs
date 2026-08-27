using ColorPicker;
using Gma.System.MouseKeyHook;
using Microsoft.WindowsAPICodePack.Taskbar;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using System.Collections;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Wpf.Ui.Markup;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using Icon = System.Drawing.Icon;
using IOPath = System.IO.Path;
using Keys = System.Windows.Forms.Keys;
using MouseButtons = System.Windows.Forms.MouseButtons;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;
using WinForms = System.Windows.Forms;
using WpfTitleBar = Wpf.Ui.Controls.TitleBar;



namespace KeyClickOverlay
{
    /// <summary>
    /// Main application window for the KeyClickOverlay.
    /// Handles initialization, UI setup, input visualization, and window modes.
    /// </summary>
    public partial class MainWindow : Window
    {
        // === Constants ===
        private const int WindowWidth = 1645;
        private const int WindowHeight = 75;
        private const double KeyReleasePopScale = 1.35;       // scale factor for key-up "pop" animation
        private const double SquarePerScale = 0.6103515625;   // matches GetSquareSize() factor
        private const int ScrollHideMs = 150;                 // hide mouse scroll image after N ms
        private const int KeyHangMs = 500;                    // how long a released key stays visible (milliseconds)
        private const int ResizeHitEdgePx = 4;                // resize grip margin in pixels
        private const int KeyWatchTickMs = 8;                 // watchdog tick interval
        private const int ShiftDebounceMs = 80;               // delay before processing Shift-up when guarded
        private const int NumpadGuardMs = 120;                // guard window after NumPad activity
        private const byte WindowAlpha = 1;                   // opacity of window
        private const double FrameStroke = 1;                 // visible outline in normal mode
        private const double FrameTotalInset = 2;             // keep child inset stable across modes
        private const double StandardGapFactor = 0.30;        // left gap between all keys
        private const double MainMenuWidth = 285.0;           // fixed width of the main context menu
        private const int ToolTipDelay = 800;                 // delay before a tooltip appears
        private const int ToolTipDuration = 20000;            // how long a tooltip remains visible


        // === State ===
        private readonly Brush _fullyTransparentBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        private bool _uiBuilt;                        // ensure SetupOverlayUI() runs only once
        private bool _transparentToMouse = false;
        private MenuItem? _transparentMenuItem;       // "Toggle Transparent-mode" menu item
        private ThumbnailToolBarButton? _transparentThumbButton;
        private ThumbnailToolBarButton? _clearThumbButton;
        private bool _overlayPaused = false;
        private bool _mouseInitialized = false;
        private bool _mouseEnabled = true;            // master switch for mouse visibility + input
        private bool _syncingMenu = false;            // suppress menu handlers while syncing IsChecked
        private double _mouseAspectRatio = 1.0;       // width / height at pressed baseline
        private bool _mouseAspectLocked = false;      // true after first measure

        private bool _backgroundEnabled = true;
        private Brush? _stripBackgroundBrushOn;
        private ContextMenu? _globalContextMenu;
        private DateTime _lastContextMenuOpenUtc = DateTime.MinValue;

        private Window? _topmostTarget = null;                                      // null = main window; otherwise, a dialog to keep above
        private bool _suspendTopmostForMenu = false;                                // true while the context menu (or a submenu) is open
        private readonly DispatcherTimer _topmostPulse = new();                     // heartbeat that re-asserts TopMost
        private Color _backgroundColorRgb = Color.FromRgb(63, 63, 63);              // background base color #3F3F3F (no alpha)
        private double _backgroundOpacity = 112.0 / 255.0;                          // default background opacity (~44%)
        private Color _mouseColorRgb = Color.FromRgb(0xE5, 0xE5, 0xE5);              // default mouse color (#e5e5e5)
        private Color _fontColorRgb = Color.FromRgb(0xE5, 0xE5, 0xE5);              // default key/text/icon color (#e5e5e5)
        private Color _keyFillRgb = Color.FromRgb(0x28, 0x28, 0x28);                // default key tile color (#282828)
        private readonly SolidColorBrush _keyFillBrush = new(Color.FromRgb(0x28, 0x28, 0x28)); // mutable; DO NOT Freeze
        private readonly SolidColorBrush _keyTextBrush = new(Color.FromRgb(0xE5, 0xE5, 0xE5)); // mutable; DO NOT Freeze
        private static readonly string[] MouseSvgNames =
        [
            "mouse_idle.svg",
            "mouse_leftclick.svg",
            "mouse_middleclick.svg",
            "mouse_rightclick.svg",
            "mouse_scrolldown.svg",
            "mouse_scrollup.svg"
        ];
        private static Style? _pixiColorPickerStyle;
        private bool _isMouseOverWindow = false;      // chrome highlight on hover
        private bool _relayoutQueued = false;   // coalesce full layout recomputes to once per frame
        private double _lastWindowHeight = -1;  // guard: only rerun when height actually changed
        private const double DefaultPillPadFactor = 0.70;     // startup default
        private const double DefaultPillCornerFactor = 0.60;  // startup default
        private double _lastScaleFactor = -1;
        private double _lastPadFactor = double.NaN;
        private double _lastCornerFactor = double.NaN;


        // === UI Elements ===
        private StackPanel? _horizontalContainer;
        private SvgViewbox? _mouseSvgDisplay;
        private Border? _stripBackground;
        private Border? _mouseBorder;
        private const string SpaceKeyTag = "SPACE_KEY";
        private const string PauseOverlayKeyId = "__PAUSE_OVERLAY__";
        private ScaleTransform? _pauseIndicatorScale;               // scale transform used by the persistent Pause indicator
        private bool _pauseShortcutHeld = false;
        private StackPanel? _keysOutsideContainer;
        private StackPanel? _lineHost;
        private bool _previewFontKeysActive = false;
        private readonly List<FrameworkElement> _previewFontKeyHosts = [];
        private bool _mouseOnlyBackground = false;


        // === Input Tracking ===
        private DispatcherTimer? _scrollTimer;
        private bool _pillLayoutRefreshQueued = false;          // coalesced layout refresh (prevents racing on fast typing)
        private IKeyboardMouseEvents? globalHook;
        private string _currentMouseImage = "mouse_idle.svg";
        private string? _lastMouseTintPath;
        private readonly Dictionary<string, (FrameworkElement element, ScaleTransform scale, DateTime? deadlineUtc)> _activeKeyBoxes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Keys> _downKeys = [];
        private readonly Dictionary<string, DateTime> _pendingRegularUps = new(StringComparer.OrdinalIgnoreCase);
        private double _contentScale = 1.0;                     // scales content down when many keys are shown
        private readonly List<string> _pillOrder = [];          // keys in left→right order (IDs match _activeKeyBoxes keys)
        private readonly Dictionary<string, double> _keyUnitsAtPressed = new(StringComparer.OrdinalIgnoreCase); // baseline widths


        // === Modifier / NumPad Guard ===
        private readonly HashSet<string> _pendingModUps = [];   // "ShiftKey", "CtrlKey", "AltKey"
        private readonly Dictionary<string, DateTime> _pendingModSince = [];
        private readonly DispatcherTimer _keyStateWatch = new() { Interval = TimeSpan.FromMilliseconds(KeyWatchTickMs) };
        private bool _cullSubscribed = false;                   // render-loop culling attached?
        private DateTime _numpadGuardUntil = DateTime.MinValue;


        // === Background Pill Config ===
        private double _pillPadFactor = DefaultPillPadFactor;                   // padding as fraction of key "square" size
        private double _pillCornerFactor = DefaultPillCornerFactor;            // corner radius as fraction of key "square" size


        // === Preferences (persisted to %AppData%\KeyClickOverlay\prefs.json) ===

        /// <summary>Lightweight user preferences stored in prefs.json (AppData).</summary>
        private sealed class UserPrefs
        {
            [JsonPropertyName("hideTransparentInfo")]
            public bool HideTransparentInfo { get; set; } // hide the transparent-to-mouse mode info popup when true 

            [JsonPropertyName("privacyNoticeShown")]
            public bool PrivacyNoticeShown { get; set; } // hide the Privacy Notification info popup when true 

            [JsonPropertyName("lastPresetPath")]
            public string? LastPresetPath { get; set; } // absolute path to the last used preset JSON file, or null if none

            [JsonPropertyName("previousPresetPath")]
            public string? PreviousPresetPath { get; set; } // absolute path to the preset used before the last active preset, or null if none

            // Which key is used to toggle transparent-mode
            [JsonPropertyName("transparentHotkeyKey")]
            public Keys TransparentHotkeyKey { get; set; } = Keys.F12;

            // Which modifiers (Ctrl/Shift/Alt) belong to that hotkey
            [JsonPropertyName("transparentHotkeyModifiers")]
            public ModifierKeys TransparentHotkeyModifiers { get; set; }
                = ModifierKeys.Control | ModifierKeys.Shift;

            // Whether the preset-switch shortcut is enabled
            [JsonPropertyName("presetToggleHotkeyEnabled")]
            public bool PresetToggleHotkeyEnabled { get; set; } = true;

            // Preset switch shortcut: default Ctrl+Space
            [JsonPropertyName("presetSwitchHotkeyKey")]
            public Keys PresetSwitchHotkeyKey { get; set; } = Keys.Space;

            [JsonPropertyName("presetSwitchHotkeyModifiers")]
            public ModifierKeys PresetSwitchHotkeyModifiers { get; set; }
                = ModifierKeys.Control;

            // Preset-switch toggle shortcut: default Ctrl+Alt+Shift+F12
            [JsonPropertyName("presetSwitchToggleHotkeyKey")]
            public Keys PresetSwitchToggleHotkeyKey { get; set; } = Keys.F12;

            [JsonPropertyName("presetSwitchToggleHotkeyModifiers")]
            public ModifierKeys PresetSwitchToggleHotkeyModifiers { get; set; }
                = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

            // Clear KeyClickOverlay shortcut: default Ctrl+Alt+Shift+F11
            [JsonPropertyName("clearOverlayHotkeyKey")]
            public Keys ClearOverlayHotkeyKey { get; set; } = Keys.F11;

            [JsonPropertyName("clearOverlayHotkeyModifiers")]
            public ModifierKeys ClearOverlayHotkeyModifiers { get; set; }
                = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

            // Pause/resume overlay shortcut: default Ctrl+Shift+F11
            [JsonPropertyName("pauseOverlayHotkeyKey")]
            public Keys PauseOverlayHotkeyKey { get; set; } = Keys.F11;

            [JsonPropertyName("pauseOverlayHotkeyModifiers")]
            public ModifierKeys PauseOverlayHotkeyModifiers { get; set; }
                = ModifierKeys.Control | ModifierKeys.Shift;
        }

        /// <summary>JSON options for prefs/presets</summary>
        private static readonly JsonSerializerOptions PrefsJsonOptions = new()
        {
            WriteIndented = true
        };

        private UserPrefs _prefs = new();

        /// <summary>Returns the currently configured Transparent-mode hotkey (modifiers + key), with safe defaults.</summary>
        private (ModifierKeys Modifiers, Keys Key) GetTransparentHotkey()
        {
            var key = _prefs.TransparentHotkeyKey;
            var mods = _prefs.TransparentHotkeyModifiers;

            if (key == Keys.None)
                key = Keys.T;

            if (mods == ModifierKeys.None)
                mods = ModifierKeys.Control;

            return (mods, key);
        }

        /// <summary>Human-readable label for a shortcut, e.g. "Ctrl+Alt+R".</summary>
        private static string FormatShortcutLabel(ModifierKeys mods, Keys key)
        {
            var parts = new List<string>();

            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");

            string text = key.ToString();

            if (key >= Keys.D0 && key <= Keys.D9)
                text = (key - Keys.D0).ToString();

            parts.Add(text);

            return string.Join("+", parts);
        }

        /// <summary>Human-readable label for the Transparent-mode shortcut.</summary>
        private string GetTransparentHotkeyLabel()
        {
            var (mods, key) = GetTransparentHotkey();
            return FormatShortcutLabel(mods, key);
        }

        /// <summary>Human-readable label for the preset-switch shortcut.</summary>
        private string GetPresetSwitchHotkeyLabel()
        {
            return FormatShortcutLabel(_prefs.PresetSwitchHotkeyModifiers, _prefs.PresetSwitchHotkeyKey);
        }

        /// <summary>Human-readable label for the preset-switch toggle shortcut.</summary>
        private string GetPresetSwitchToggleHotkeyLabel()
        {
            return FormatShortcutLabel(_prefs.PresetSwitchToggleHotkeyModifiers, _prefs.PresetSwitchToggleHotkeyKey);
        }

        /// <summary>Human-readable label for the Clear KeyClickOverlay shortcut.</summary>
        private string GetClearOverlayHotkeyLabel()
        {
            return FormatShortcutLabel(_prefs.ClearOverlayHotkeyModifiers, _prefs.ClearOverlayHotkeyKey);
        }

        /// <summary>Human-readable label for the pause/resume overlay shortcut.</summary>
        private string GetPauseOverlayHotkeyLabel()
        {
            return FormatShortcutLabel(_prefs.PauseOverlayHotkeyModifiers, _prefs.PauseOverlayHotkeyKey);
        }

        /// <summary>
        /// Returns true when the current global key event matches the configured transparent-mode hotkey.
        /// Uses tracked held-modifier state instead of trusting event flags alone.
        /// </summary>
        private bool MatchesTransparentHotkey(System.Windows.Forms.KeyEventArgs e)
        {
            var (hotMods, hotKey) = GetTransparentHotkey();
            var currentMods = GetHeldModifiersFromState();

            return currentMods == hotMods && e.KeyCode == hotKey;
        }

        /// <summary>
        /// Builds the currently held Ctrl/Shift/Alt state from our own tracked key state,
        /// instead of relying on e.Control / e.Shift / e.Alt from a single event.
        /// </summary>
        private ModifierKeys GetHeldModifiersFromState()
        {
            ModifierKeys mods = ModifierKeys.None;

            if (_downKeys.Contains(Keys.LControlKey) || _downKeys.Contains(Keys.RControlKey) || _downKeys.Contains(Keys.ControlKey))
                mods |= ModifierKeys.Control;

            if (_downKeys.Contains(Keys.LShiftKey) || _downKeys.Contains(Keys.RShiftKey) || _downKeys.Contains(Keys.ShiftKey))
                mods |= ModifierKeys.Shift;

            if (_downKeys.Contains(Keys.LMenu) || _downKeys.Contains(Keys.RMenu) || _downKeys.Contains(Keys.Menu))
                mods |= ModifierKeys.Alt;

            return mods;
        }

        /// <summary>Update all UI text that shows the transparent shortcut (tooltips, etc.)</summary>
        private void RefreshTaskbarHotkeyUiLabels()
        {
            string transparentLabel = GetTransparentHotkeyLabel();

            if (_transparentMenuItem != null)
            {
                _transparentMenuItem.ToolTip =
                    $"Disables mouse interaction with KeyClickOverlay. Exit with {transparentLabel} or via the taskbar hover menu (hover its icon).";
            }

            if (_transparentThumbButton != null)
            {
                _transparentThumbButton.Tooltip = $"Transparent-mode ({transparentLabel})";
            }

            if (_clearThumbButton != null)
            {
                _clearThumbButton.Tooltip = $"Clear keys ({GetClearOverlayHotkeyLabel()})";
            }
        }

        /// <summary>Full path to prefs.json in AppData</summary>
        private static string PrefsPath =>
            IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "KeyClickOverlay", "prefs.json");

        /// <summary>Load lightweight user preferences from disk (fails silently).</summary>
        private void LoadPrefs()
        {
            try
            {
                var dir = IOPath.GetDirectoryName(PrefsPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(PrefsPath))
                {
                    string json = File.ReadAllText(PrefsPath);
                    _prefs = JsonSerializer.Deserialize<UserPrefs>(json, PrefsJsonOptions) ?? new UserPrefs();
                }
            }
            catch
            {
                _prefs = new UserPrefs();
            }
        }

        /// <summary>Save lightweight user preferences to disk (fails silently).</summary>
        private void SavePrefs()
        {
            try
            {
                var dir = IOPath.GetDirectoryName(PrefsPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_prefs, PrefsJsonOptions);
                File.WriteAllText(PrefsPath, json);
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Shows the privacy notice once, when KeyClickOverlay is opened for the first time. </summary>
        private void ShowFirstRunPrivacyNotice()
        {
            if (_prefs.PrivacyNoticeShown)
                return;

            bool acknowledged = ShowModernAcknowledgement(
                title: "Privacy Notice",
                message:
                    "KeyClickOverlay displays all keyboard input it detects, " +
                    "including text entered into login and password fields.\n\n" +

                    "Disable or close the overlay before entering passwords or other " +
                    "sensitive information, especially while recording or sharing your screen.\n\n" +

                    "KeyClickOverlay does not record, store, or transmit your input.",
                acknowledgeText: "I understand",
                icon: DialogIcon.Warning
            );

            if (!acknowledged)
                return;

            _prefs.PrivacyNoticeShown = true;
            SavePrefs();
        }


        // === Presets (named JSON files under %AppData%\KeyClickOverlay\presets) ===

        /// <summary>On startup: if a last-used preset exists, apply it silently.</summary>
        private void TryApplyLastPresetOnStartup()
        {
            try
            {
                var path = _prefs.LastPresetPath;
                if (string.IsNullOrWhiteSpace(path)) return;

                if (!File.Exists(path))
                {
                    // File gone → clear pointer and persist
                    _prefs.LastPresetPath = null;
                    SavePrefs();
                    return;
                }

                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<PresetData>(json, PrefsJsonOptions);
                if (data is null) return;

                ApplyPresetFromData(data); // apply without prompts
            }
            catch
            {
                // Fail silently; don't block startup on corrupt preset
            }
        }

        private const int MaxPresets = 10;                // UI cap for quick-access list
        private const string ActivePresetTag = "ACTIVE_PRESET"; // row Tag marker in menu
        private const string RenamingPresetTag = "RENAMING_PRESET";

        // The preset that was active immediately before the current one.
        private string? _previousPresetPath;

        /// <summary>Serializable snapshot of the current UI/state saved as a preset JSON.</summary>
        private sealed class PresetData
        {
            // --- Window geometry & position ---
            public double WindowWidth { get; set; }   // px
            public double WindowHeight { get; set; }  // px
            public double WindowLeft { get; set; }    // screen X (px)
            public double WindowTop { get; set; }     // screen Y (px)

            // --- Modes / toggles ---
            public bool MouseEnabled { get; set; }          // show mouse visual + process mouse input
            public bool BackgroundEnabled { get; set; }     // show the pill/background bar
            public bool MouseOnlyBackground { get; set; }   // pill shows with mouse only (keys outside)

            // --- Colors (hex, #RRGGBB) ---
            public string MouseColor { get; set; } = "#282828";
            public string FontColor { get; set; } = "#e5e5e5";
            public string KeyFill { get; set; } = "#282828";

            // --- Background (color + opacity) ---
            public string BackgroundRgb { get; set; } = "#000000"; // base color; alpha via BackgroundOpacity
            public double BackgroundOpacity { get; set; }          // 0..1

            // --- Background pill geometry ---
            public double PillPadFactor { get; set; }     // padding as fraction of key "square" size
            public double PillCornerFactor { get; set; }  // corner radius as fraction of key "square" size
        }

        // Presets directory in AppData
        private static string PresetsDir =>
            IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                           "KeyClickOverlay", "presets");

        /// <summary>Make a filesystem-safe filename from a user-visible name.</summary>
        private static string SanitizePresetFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Preset";
            var cleaned = string.Concat(name.Where(c => !InvalidFileChars.Contains(c))).Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "Preset" : cleaned;
        }

        // Cached once for O(1) lookups
        private static readonly HashSet<char> InvalidFileChars = [.. System.IO.Path.GetInvalidFileNameChars()];

        /// <summary>Find a unique path “&lt;name&gt;.json”, “&lt;name&gt; (2).json”, …</summary>
        private static string GetUniquePresetPath(string baseName)
        {
            Directory.CreateDirectory(PresetsDir);

            string fn = SanitizePresetFileName(baseName);
            string path = IOPath.Combine(PresetsDir, fn + ".json");
            if (!File.Exists(path)) return path;

            for (int i = 2; i < 1000; i++)
            {
                string p = IOPath.Combine(PresetsDir, $"{fn} ({i}).json");
                if (!File.Exists(p)) return p;
            }
            return IOPath.Combine(PresetsDir, fn + "." + Guid.NewGuid().ToString("N") + ".json");
        }

        // Convert Color to "#RRGGBB"
        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>Snapshot current UI/state into a preset object.</summary>
        private PresetData BuildPresetFromCurrent()
        {
            double width = (double.IsNaN(ActualWidth) || ActualWidth <= 0) ? Width : ActualWidth;
            double height = (double.IsNaN(ActualHeight) || ActualHeight <= 0) ? Height : ActualHeight;

            // Use normal (restorable) bounds to remember where the user placed it
            Rect normal = (WindowState == WindowState.Normal)
                ? new Rect(Left, Top, width, height)
                : RestoreBounds;

            return new PresetData
            {
                WindowWidth = Math.Max(1.0, width),
                WindowHeight = Math.Max(1.0, height),

                // Position (older presets may deserialize to 0)
                WindowLeft = normal.Left,
                WindowTop = normal.Top,

                MouseEnabled = _mouseEnabled,
                BackgroundEnabled = _backgroundEnabled,
                MouseOnlyBackground = _mouseOnlyBackground,

                MouseColor = ToHex(_mouseColorRgb),
                FontColor = ToHex(_fontColorRgb),
                KeyFill = ToHex(_keyFillRgb),

                BackgroundRgb = ToHex(_backgroundColorRgb),
                BackgroundOpacity = Math.Clamp(_backgroundOpacity, 0, 1),

                PillPadFactor = _pillPadFactor,
                PillCornerFactor = _pillCornerFactor
            };
        }

        /// <summary>Convert “#RRGGBB” to Color; returns Black on invalid.</summary>
        private static Color FromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Colors.Black;
            hex = hex.Trim();
            if (hex.StartsWith('#')) hex = hex[1..];
            if (hex.Length != 6) return Colors.Black;
            byte r = Convert.ToByte(hex[..2], 16);
            byte g = Convert.ToByte(hex[2..4], 16);
            byte b = Convert.ToByte(hex[4..6], 16);
            return Color.FromRgb(r, g, b);
        }

        /// <summary>Apply a preset object to the running app.</summary>
        private void ApplyPresetFromData(PresetData p, bool preserveTransparentMode = false)
        {
            bool wasTransparent = _transparentToMouse;

            // Size
            Width = Math.Max(this.MinWidth, p.WindowWidth);
            Height = Math.Max(this.MinHeight, p.WindowHeight);

            // Position (older presets may have 0/0)
            if (p.WindowLeft != 0 || p.WindowTop != 0)
            {
                Left = p.WindowLeft;
                Top = p.WindowTop;
                ClampWindowToVirtualScreen(); // keep fully visible
            }

            // Toggles / modes
            ApplyMouseEnabled(p.MouseEnabled);
            SetBackgroundEnabled(p.BackgroundEnabled);
            ApplyMouseOnlyMode(p.MouseOnlyBackground);
            SetTransparentMode(false, withPrompt: false); // ensure interactive while applying

            // Colors
            _mouseColorRgb = FromHex(p.MouseColor);
            _fontColorRgb = FromHex(p.FontColor);
            _keyFillRgb = FromHex(p.KeyFill);

            _keyTextBrush.Color = _fontColorRgb;
            _keyFillBrush.Color = _keyFillRgb;
            RetintAllKeyIconsForFontColor();

            PrecacheAllMouseSvgsForColor(_mouseColorRgb);
            SetMouseSvg(_currentMouseImage ?? "mouse_idle.svg");

            // Background color/opacity
            _backgroundColorRgb = FromHex(p.BackgroundRgb);
            _backgroundOpacity = Math.Clamp(p.BackgroundOpacity, 0, 1);
            ApplyBackgroundBrushFromState();

            // Factors
            _pillPadFactor = p.PillPadFactor;
            _pillCornerFactor = p.PillCornerFactor;

            // Layout refresh
            UpdateStripBackgroundMetrics();
            UpdateChromeButtons();

            // Preset switching should not force the app out of transparent mode when requested.
            if (preserveTransparentMode && wasTransparent)
                SetTransparentMode(true, withPrompt: false);
        }


        // === Window placement & bounds ===

        /// <summary>Ensures the window stays visible on the current virtual desktop area (handles monitor/DPI/layout changes).</summary>
        private void ClampWindowToVirtualScreen()
        {
            // Virtual desktop bounds across all monitors
            double vx = SystemParameters.VirtualScreenLeft;
            double vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;

            // Clamp Left/Top so the window's title/body stays reachable
            double minVisible = 24; // require at least 24px visible

            double maxLeft = vx + vw - minVisible;
            double maxTop = vy + vh - minVisible;

            if (double.IsNaN(Left)) Left = vx;
            if (double.IsNaN(Top)) Top = vy;

            Left = Math.Min(Math.Max(Left, vx - (Width - minVisible)), maxLeft);
            Top = Math.Min(Math.Max(Top, vy - (Height - minVisible)), maxTop);
        }

        /// <summary>Move the window by a small delta in DIPs, then clamp to visible bounds.</summary>
        private void NudgeWindow(double dx, double dy)
        {
            Left += dx;
            Top += dy;
            ClampWindowToVirtualScreen();
        }

        /// <summary>
        /// Arrow-key nudge: moves window by 1 DIP per key press. Only when focused, in Normal state, and NOT transparent-to-mouse. 
        /// Also disabled while the context menu is open.
        /// </summary>
        private void OnNudgeWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Only when this window is the active/focused window
            if (!IsActive) return;

            // Only in Normal mode (don’t fight minimize/maximize/restore)
            if (WindowState != WindowState.Normal) return;

            // Only when interactive (normal mode)
            if (_transparentToMouse) return;

            // Don’t nudge while the context menu is open (editing number boxes etc.)
            if (_globalContextMenu?.IsOpen == true) return;

            // Move by 1 DIP per press, move 10 DIP when holding shift
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10.0 : 1.0;

            switch (e.Key)
            {
                case Key.Left:
                    NudgeWindow(-step, 0);
                    e.Handled = true;
                    break;

                case Key.Right:
                    NudgeWindow(step, 0);
                    e.Handled = true;
                    break;

                case Key.Up:
                    NudgeWindow(0, -step);
                    e.Handled = true;
                    break;

                case Key.Down:
                    NudgeWindow(0, step);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Positions the window at the bottom-left if no preset supplied coordinates.</summary>
        private void PlaceAtBottomLeftIfNoPreset()
        {
            // If a preset or something already set coordinates, do nothing
            bool hasExplicitPosition =
                !double.IsNaN(Left) && !double.IsNaN(Top) &&
                (Math.Abs(Left) > double.Epsilon || Math.Abs(Top) > double.Epsilon);
            if (hasExplicitPosition) return;

            // Use the known constants as reliable height/width
            double h = WindowHeight;

            // Primary work area (already in DIPs)
            var wa = SystemParameters.WorkArea;

            Left = wa.Left;
            Top = wa.Bottom - h;
        }

        /// <summary>Load & apply a preset by file path, then remember it for next launch.</summary>
        private void ApplyPresetFromPath(string path, bool preserveTransparentMode = false)
        {
            try
            {
                if (!File.Exists(path))
                {
                    ShowModernInfo("Preset not found", "Preset file not found:\n" + path, ok: "OK", icon: DialogIcon.Warning);
                    return;
                }

                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<PresetData>(json, PrefsJsonOptions);
                if (data is null)
                {
                    ShowModernInfo("Invalid preset", "Preset file is invalid:\n" + path, ok: "OK", icon: DialogIcon.Warning);
                    return;
                }

                ApplyPresetFromData(data, preserveTransparentMode);

                // Remember the preset we are leaving so the preset-switch shortcut can toggle back to it.
                // Store it both in memory and in preferences so it also works after restarting the app.
                if (!string.Equals(_prefs.LastPresetPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _previousPresetPath = _prefs.LastPresetPath;
                    _prefs.PreviousPresetPath = _previousPresetPath;
                }

                // Remember the active preset for next launch.
                _prefs.LastPresetPath = path;
                SavePrefs();
            }
            catch (Exception ex)
            {
                ShowModernInfo("Preset error", "Failed to apply preset:\n" + ex.Message, ok: "OK", icon: DialogIcon.Error);
            }
        }

        /// <summary>Toggle between the active preset and the preset that was active before it.</summary>
        private void TogglePreviousPreset()
        {
            string? previousPath = _previousPresetPath ?? _prefs.PreviousPresetPath;

            if (string.IsNullOrWhiteSpace(previousPath) || !File.Exists(previousPath))
            {
                ShowModernInfoAuto("No previous preset", "There is no previous preset to switch back to yet.", milliseconds: 2500, icon: DialogIcon.Info);
                return;
            }

            ApplyPresetFromPath(previousPath, preserveTransparentMode: true);
        }

        /// <summary>Enable or disable the preset-switch shortcut.</summary>
        private void TogglePresetToggleHotkeyEnabled()
        {
            _prefs.PresetToggleHotkeyEnabled = !_prefs.PresetToggleHotkeyEnabled;
            SavePrefs();
        }

        /// <summary>Overwrite a specific preset file with the current UI/state; optionally make it active.</summary>
        private void SaveCurrentStateToPreset(string path, bool makeActive)
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);

                var data = BuildPresetFromCurrent();
                string json = JsonSerializer.Serialize(data, PrefsJsonOptions);
                File.WriteAllText(path, json);

                if (makeActive)
                {
                    // Apply from disk so LastPresetPath is updated and menu highlight stays consistent
                    ApplyPresetFromPath(path);
                }
                else
                {
                    // Keep it last-used if it already was
                    if (string.Equals(_prefs.LastPresetPath, path, StringComparison.OrdinalIgnoreCase))
                        SavePrefs();
                }
            }
            catch (Exception ex)
            {
                ShowModernInfo("Preset error", "Failed to save preset:\n" + ex.Message, ok: "OK", icon: DialogIcon.Error);
            }
        }

        /// <summary>Overwrite the currently active preset with the current UI/state.</summary>
        private void SaveCurrentStateToActivePreset()
        {
            var path = _prefs?.LastPresetPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowModernInfo(
                    "No active preset",
                    "There’s no active preset to overwrite.\n\nUse “Save preset → Add preset” first, then try again (Ctrl+S).",
                    ok: "OK",
                    icon: DialogIcon.Info);
                return;
            }

            SaveCurrentStateToPreset(path!, makeActive: true);
            ShowModernInfoAuto("Preset saved", "The active preset was overwritten with the current settings.", milliseconds: 2500, icon: DialogIcon.Success);
        }


        // === Reset & purge (app settings and caches) ===

        /// <summary>Delete %AppData%\KeyClickOverlay and restore all in-memory settings to defaults, then refresh the UI.</summary>
        private void ResetToDefaultsAndClearData()
        {
            // Confirm with the user (modern, themed)
            bool confirmed = ShowModernYesNo(
                "Reset KeyClickOverlay?",
                "Restore the app to its default settings;\nremoves your presets and clears preferences.\n\nThis action can’t be undone.",
                yes: "Reset",
                no: "Cancel",
                icon: DialogIcon.Warning);

            if (!confirmed) return;

            // Make sure the window is interactive (avoid getting 'stuck' in transparent mode)
            try { SetTransparentMode(false, withPrompt: false); } catch { /* ignore */ }

            // 1) Delete the AppData settings folder
            try
            {
                string appDataDir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyClickOverlay");

                if (Directory.Exists(appDataDir))
                    Directory.Delete(appDataDir, recursive: true);
            }
            catch { /* ignore any IO issues */ }

            // 2) Delete SVG tint caches under %TEMP%\KeyClickOverlay
            try
            {
                string tempBase = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KeyClickOverlay");
                string mouseCache = IOPath.Combine(tempBase, "mouse_tint_cache");
                string keyCache = IOPath.Combine(tempBase, "key_tint_cache");

                if (Directory.Exists(mouseCache)) Directory.Delete(mouseCache, recursive: true);
                if (Directory.Exists(keyCache)) Directory.Delete(keyCache, recursive: true);

                // If the base folder is now empty, remove it as well (best-effort)
                try
                {
                    if (Directory.Exists(tempBase) && Directory.GetFileSystemEntries(tempBase).Length == 0)
                        Directory.Delete(tempBase, recursive: false);
                }
                catch { /* ignore */ }
            }
            catch { /* ignore */ }

            // 3) Reset in-memory preferences/state to defaults (do NOT save—keeps disk clean)
            _prefs = new UserPrefs(); // e.g., HideTransparentInfo=false by default

            // Reset colors/toggles to defaults you already use at startup
            _backgroundColorRgb = Color.FromRgb(63, 63, 63);
            _backgroundOpacity = 112.0 / 255.0;
            ApplyBackgroundBrushFromState();

            _mouseColorRgb = Color.FromRgb(0xE5, 0xE5, 0xE5);
            _fontColorRgb = Color.FromRgb(0xE5, 0xE5, 0xE5);
            _keyFillRgb = Color.FromRgb(0x28, 0x28, 0x28);

            _keyTextBrush.Color = _fontColorRgb;
            _keyFillBrush.Color = _keyFillRgb;

            // 3b) Reset pill geometry to startup defaults
            SetPillPaddingFactor(DefaultPillPadFactor);   // default padding factor per side
            SetPillCornerFactor(DefaultPillCornerFactor);    // default corner radius factor

            // Ensure master toggles are back on
            ApplyMouseEnabled(true);
            SetBackgroundEnabled(true);
            ApplyMouseOnlyMode(false);

            // 3c) Reset window geometry to startup layout (bottom-left of primary work area)
            Width = WindowWidth;   // constants at top of file
            Height = WindowHeight;

            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Bottom - Height;   // bottom-left anchor

            ClampWindowToVirtualScreen();  // keep fully visible on current monitor setup

            // Re-apply visuals
            SetMouseSvg(_currentMouseImage ?? "mouse_idle.svg");
            RetintAllKeyIconsForFontColor();
            UpdateStripBackgroundMetrics();

            ShowModernInfo(
                "Reset complete",
                "KeyClickOverlay has been restored to its original settings.\nAll presets and preferences were removed.",
                ok: "OK",
                icon: DialogIcon.Info);
        }


        // === Design tokens: geometry, spacing, key visuals (used by UI builders) ===

        /// <summary>Limits for pill dimensions and corner radius to avoid extreme shapes.</summary>
        private static class PillBounds
        {
            // Background "margin" (padding) factor limits
            public const double PaddingMin = 0.00;
            public const double PaddingMax = 2.00;

            // Background corner-radius factor limits
            public const double CornerMin = 0.00;
            public const double CornerMax = 3.00;
        }

        /// <summary>Central source for key visuals: colors, typography, margins, and per-key geometry.</summary>
        private static class KeyStyle
        {
            // ---------- Colors & font ----------

            // Bind directly to the static Inter Regular TTF (family "Inter", face "Regular").
            public static readonly FontFamily Font =
                new(new Uri("pack://application:,,,/KeyClickOverlay;component/assets/fonts/", UriKind.Absolute), "./Inter-Regular.ttf#Inter");

            public static readonly SolidColorBrush KeyFill = Brush("#282828"); // key background
            public static readonly SolidColorBrush TextFill = Brush("#e5e5e5"); // label color

            /// <summary>Create a frozen SolidColorBrush from a hex color (e.g., "#RRGGBB").</summary>
            private static SolidColorBrush Brush(string hex)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex)!;
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }

            // ---------- Shared look ----------
            public const double CornerRadiusFactor = 0.18;

            /// <summary>Compute the corner radius from the square key size.</summary>
            public static double CornerRadius(double square) => square * CornerRadiusFactor;

            // ---------- Normal (square) text keys ----------
            public const double GlyphHeightFactor_Normal = 0.32;   // normal & wide keys
            public const double GlyphHeightFactor_NumPad = 0.40;   // big digit inside its bottom row

            /// <summary>Compute font size for normal (square) text keys.</summary>
            public static double NormalFontSize(double square) => square * NormalFontFactor;

            // ---------- NumPad small header ----------
            public const double NormalFontFactor = 0.15;
            public const double NumPadHeaderTopFactor = 0.10;

            /// <summary>Compute margin for the small NumPad header label.</summary>
            public static Thickness NumPadHeaderMargin(double square)
                => new(0, square * NumPadHeaderTopFactor, 0, 0);

            // ---------- Wide text keys ----------

            /// <summary>Horizontal padding for wide text keys; matches vertical breathing room.</summary>
            public static Thickness WideText_BorderPadding(double square)
            {
                // Vertical margin per side equals (square - glyphHeight) / 2
                double perSide = square * (1.0 - GlyphHeightFactor_Normal) / 2.0; // ≈ 0.34 * square
                return new Thickness(perSide, 0, perSide, 0); // match left/right to that amount
            }

            // ---------- Icon-only & Space ----------
            public const double IconInnerMarginFactor = 0.30;

            /// <summary>Compute inner margin for icon-only keys.</summary>
            public static Thickness IconInnerMargin(double square)
                => new(square * IconInnerMarginFactor);

            public const double SpaceMinWidthFactor = 2.55;

            /// <summary>Compute the minimum width for the Space key.</summary>
            public static double SpaceMinWidth(double square) => square * SpaceMinWidthFactor;

            // ---------- Special keys (icon + label) ----------
            public const double SpecialIconMarginFactor = 0.15;
            public const double SpecialLabelLRFactor = 0.20;
            public const double SpecialLabelBottomFactor = 0.12;
            public const double SpecialLabelFontFactor = 0.25;

            /// <summary>Compute icon margin for special keys.</summary>
            public static Thickness SpecialIconMargin(double square)
                => new(square * SpecialIconMarginFactor);

            /// <summary>Compute label margins for special keys.</summary>
            public static Thickness SpecialLabelMargin(double square)
                => new(square * SpecialLabelLRFactor, 0,
                       square * SpecialLabelLRFactor, square * SpecialLabelBottomFactor);

            /// <summary>Compute label font size for special keys.</summary>
            public static double SpecialLabelFontSize(double square)
                => square * SpecialLabelFontFactor;
        }

        /// <summary>Cache unscaled glyph geometries keyed by text</summary>
        private static readonly Dictionary<string, Geometry> _glyphGeoCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Builds or returns a cached flattened outline geometry for the given text.</summary>
        private static Geometry GetOrBuildTightTextGeometry(string text, Typeface typeface, double emSize)
        {
            if (string.IsNullOrWhiteSpace(text)) text = "?";
            if (_glyphGeoCache.TryGetValue(text, out var cached))
                return cached;

            var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, emSize, Brushes.Black, VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip)
            {
                TextAlignment = TextAlignment.Left,
                Trimming = TextTrimming.None
            };

            Geometry g = ft.BuildGeometry(new Point(0, 0));
            var path = g.GetFlattenedPathGeometry(0.1, ToleranceType.Absolute);
            _glyphGeoCache[text] = path;
            return path;
        }

        /// <summary>Builds a centered glyph view sized by height for normal/num-pad keys.</summary>
        private static Viewbox BuildCenteredGlyphElement(string text, double targetGlyphHeight, Brush textBrush)
            => BuildCenteredGlyphElement(
                text,
                targetGlyphHeight,
                textBrush,
                forWideKey: false,                     // convenience overload: no width reporting
                wideBorderPadding: new Thickness(0),
                out _);

        /// <summary>Builds a centered glyph view; in wide mode also reports natural width so the key can widen.</summary>
        private static Viewbox BuildCenteredGlyphElement(
            string text,
            double targetGlyphHeight,
            Brush textBrush,
            bool forWideKey,
            Thickness wideBorderPadding,
            out double naturalWidth)
        {
            var tf = new Typeface(KeyStyle.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var geo = GetOrBuildTightTextGeometry(text, tf, 100.0); // cached tight outline

            // Aspect from tight bounds
            var b = geo.Bounds;
            double aspect = b.Width / Math.Max(1e-6, b.Height);

            // Path the Viewbox will scale/center
            var path = new System.Windows.Shapes.Path
            {
                Data = geo,
                Fill = textBrush,
                Stretch = Stretch.Uniform,          // Viewbox handles scaling uniformly
                SnapsToDevicePixels = true
            };

            // Viewbox that centers the path
            var vb = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = path
            };

            if (forWideKey)
            {
                // For WIDE: compute natural width at the requested height and report it
                double glyphWidth = targetGlyphHeight * aspect;
                naturalWidth = glyphWidth + wideBorderPadding.Left + wideBorderPadding.Right;

                vb.Width = glyphWidth;
                vb.Height = targetGlyphHeight;
            }
            else
            {
                // For NORMAL/NUMPAD: a square box whose size equals the glyph height target
                naturalWidth = targetGlyphHeight;   // not used by callers
                vb.Width = targetGlyphHeight;
                vb.Height = targetGlyphHeight;
            }

            return vb;
        }

        /// <summary>Builds a baseline-aligned glyph view sized by cap height for sign keys.</summary>
        private static Viewbox BuildBaselineAlignedGlyphElement(string text, double targetCapHeight, Brush textBrush)
        {
            if (string.IsNullOrWhiteSpace(text)) text = "?";

            var tf = new Typeface(KeyStyle.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (!tf.TryGetGlyphTypeface(out GlyphTypeface gt))
                throw new InvalidOperationException("Cannot get GlyphTypeface for embedded font.");

            // capHeightPixels = fontSize * CapsHeight  =>  fontSize = targetCapHeight / CapsHeight
            double fontSize = targetCapHeight / Math.Max(1e-6, gt.CapsHeight);

            // Measure the line height at that font size so our Viewbox can size correctly
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                tf,
                fontSize,
                Brushes.Transparent,
                VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip);

            double lineHeight = ft.Height;

            // Render with a TextBlock: WPF places ink relative to the baseline correctly
            var tb = new TextBlock
            {
                Text = text,
                FontFamily = KeyStyle.Font,
                FontSize = fontSize,
                Foreground = textBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Auto);

            // Square Viewbox: height = line height at this font size, width = same (square key)
            var vb = new Viewbox
            {
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Height = lineHeight,
                Width = lineHeight,
                Child = tb
            };
            return vb;
        }


        // === Constructor & Window lifecycle ===

        /// <summary>Initialize the overlay window, set up UI and global hooks, and start timers.</summary>
        public MainWindow()
        {
            // ---------- Initialize app and load persisted preferences ----------
            InitializeComponent();

            AppTheme.Changed += AppTheme_Changed;
            AppTheme.Start();

            Closed += MainWindow_Closed;

            // Minimum working size for overlay (prevents unusably tiny windows)
            this.MinWidth = 80;   // minimum usable overlay width
            this.MinHeight = 45;  // minimum usable overlay height

            if (_fullyTransparentBrush is SolidColorBrush sc && sc.CanFreeze) sc.Freeze();
            WindowStartupLocation = WindowStartupLocation.Manual;
            PlaceAtBottomLeftIfNoPreset();
            LoadPrefs();
            _previousPresetPath = _prefs.PreviousPresetPath;

            // ---------- Window hooks & chrome ----------
            Loaded += OnMainWindowLoaded;

            PreviewKeyDown += OnNudgeWindowPreviewKeyDown;

            SourceInitialized += (_, _) =>
            {
                // Enable Win32 resize logic (when using border)
                HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);

                // Ensure we stay a normal app window (taskbar icon) even after any style changes
                NativeMethods.EnsureAppWindow(this);
            };

            Activated += (_, __) => UpdateWindowBorderChrome();

            MainGrid.MouseEnter += (_, __) => { _isMouseOverWindow = true; UpdateChromeButtons(); };
            MainGrid.MouseLeave += (_, __) => { _isMouseOverWindow = false; UpdateChromeButtons(); };

            SetupOverlayUI();               // Build overlay UI (containers, chrome, one-time visuals)
            UpdateWindowChromeTheme();      // Apply current Windows light/dark chrome
            SetupHooks();                   // Global input hooks (mouse + keyboard)

            // Keep the current target (main window or dialog) above taskbar/other topmost windows.
            Activated += (_, __) => this.ReassertTopmost();
            Deactivated += (_, __) => this.ReassertTopmost();
            LocationChanged += (_, __) => this.ReassertTopmost();
            SizeChanged += (_, __) => this.ReassertTopmost();
            StateChanged += (_, __) => this.ReassertTopmost();
            IsVisibleChanged += (_, __) => this.ReassertTopmost();

            // Light heartbeat to cover rare z-order steals (every 2 seconds)
            _topmostPulse.Interval = TimeSpan.FromSeconds(2);
            _topmostPulse.Tick += (_, __) => this.ReassertTopmost();
            _topmostPulse.Start();
            this.ReassertTopmost(); // Assert TopMost once right away (covers any style changes during startup)

            // ---------- Stage initial mouse SVG + pill width after first layout ----------
            Dispatcher.BeginInvoke(() =>
            {
                SetMouseSvg("mouse_idle.svg");
                // Calculate background width after mouse SVG is loaded
                Dispatcher.BeginInvoke(() => UpdateBackgroundWidth(), DispatcherPriority.Render);
            }, DispatcherPriority.ApplicationIdle);

            // ---------- Modifier / NumPad guard watchdog ----------
            _keyStateWatch.Tick += (_, __) =>
            {
                if (_pendingModUps.Count == 0 && _pendingRegularUps.Count == 0)
                {
                    if (!GuardActive()) _keyStateWatch.Stop();
                    return;
                }

                var now = DateTime.UtcNow;
                var done = new List<string>();

                foreach (var modId in _pendingModUps)
                {
                    if (!_pendingModSince.TryGetValue(modId, out var since)) continue;
                    bool debouncePassed = (now - since) >= TimeSpan.FromMilliseconds(ShiftDebounceMs);

                    // If guard is active or a NumPad key is physically held, keep waiting.
                    if (!debouncePassed || GuardActive() || AnyNumPadPhysicallyDown())
                        continue;

                    // Release when (a) debounce passed, (b) no guard, (c) neither side is physically down.
                    bool leftDown, rightDown;
                    switch (modId)
                    {
                        case "ShiftKey":
                            leftDown = IsPhysicallyDown(Keys.LShiftKey);
                            rightDown = IsPhysicallyDown(Keys.RShiftKey);
                            break;
                        case "CtrlKey":
                            leftDown = IsPhysicallyDown(Keys.LControlKey);
                            rightDown = IsPhysicallyDown(Keys.RControlKey);
                            break;
                        case "AltKey":
                            leftDown = IsPhysicallyDown(Keys.LMenu);
                            rightDown = IsPhysicallyDown(Keys.RMenu);
                            break;
                        default:
                            leftDown = rightDown = false;
                            break;
                    }
                    if (leftDown || rightDown) continue;

                    // Remove the raw L/R codes from the physical set so state stays consistent
                    if (modId == "ShiftKey")
                    {
                        _downKeys.Remove(Keys.LShiftKey);
                        _downKeys.Remove(Keys.RShiftKey);
                    }
                    else if (modId == "CtrlKey")
                    {
                        _downKeys.Remove(Keys.LControlKey);
                        _downKeys.Remove(Keys.RControlKey);
                    }
                    else if (modId == "AltKey")
                    {
                        _downKeys.Remove(Keys.LMenu);
                        _downKeys.Remove(Keys.RMenu);
                    }

                    ReleaseKeyUI(modId);
                    done.Add(modId);
                }

                foreach (var modId in done)
                {
                    _pendingModUps.Remove(modId);
                    _pendingModSince.Remove(modId);
                }

                // Recover regular keys whose KeyUp was missed or whose UI got stuck.
                // Give them a tiny grace window so very fast taps still get their pop.
                var regularDone = new List<string>();

                foreach (var pair in _pendingRegularUps.ToList())
                {
                    string keyId = pair.Key;
                    DateTime sinceUtc = pair.Value;

                    if (!Enum.TryParse(keyId, out Keys rawKey))
                    {
                        regularDone.Add(keyId);
                        continue;
                    }

                    bool physicallyDown = IsPhysicallyDown(rawKey);
                    bool graceElapsed = (DateTime.UtcNow - sinceUtc).TotalMilliseconds >= 30.0;

                    if (!physicallyDown && graceElapsed)
                    {
                        _downKeys.Remove(rawKey);
                        ReleaseKeyUI(keyId);
                        regularDone.Add(keyId);
                    }
                }

                foreach (var keyId in regularDone)
                    _pendingRegularUps.Remove(keyId);

                if (_pendingModUps.Count == 0 && _pendingRegularUps.Count == 0 && !GuardActive())
                    _keyStateWatch.Stop();
            };
        }

        private void AppTheme_Changed(object? sender, EventArgs e)
        {
            UpdateWindowChromeTheme();

            if (_globalContextMenu != null)
            {
                ApplyWin11ContextMenuStyle(_globalContextMenu);
            }
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            AppTheme.Changed -= AppTheme_Changed;
            AppTheme.Stop();
        }

        /// <summary>Finalize window initialization once loaded (e.g., taskbar toolbar).</summary>
        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string svgDir = System.IO.Path.Combine(baseDir, "assets", "svg");

            if (!Directory.Exists(svgDir))
            {
                ShowModernInfo("Missing content", "The 'assets/svg' folder is missing in the installation.\nPlease reinstall the app.", ok: "OK", icon: DialogIcon.Error);

                Application.Current.Shutdown();
                return;
            }

            SetupThumbnailToolbarButton();
            TryApplyLastPresetOnStartup();
            ShowFirstRunPrivacyNotice();
        }

        /// <summary>Add a Windows taskbar thumbnail toolbar button to toggle mouse transparency.</summary>
        private void SetupThumbnailToolbarButton()
        {
            if (!TaskbarManager.IsPlatformSupported) return;

            try
            {
                // Create the toolbar button for Transparent-mode
                Icon icon = AppResources.MouseIcon ?? throw new InvalidOperationException("MouseIcon missing.");
                var transparentButton = new ThumbnailToolBarButton(icon, $"Transparent-mode ({GetTransparentHotkeyLabel()})");
                transparentButton.Click += (_, _) => SetTransparentMode(!_transparentToMouse, withPrompt: true);

                // Remember it so we can update the tooltip when the shortcut gets changed
                _transparentThumbButton = transparentButton;

                // Clear keys button (ClearKey icon)
                Icon clearIcon = AppResources.ClearKey ?? throw new InvalidOperationException("ClearKey icon missing.");
                var clearButton = new ThumbnailToolBarButton(
                    clearIcon,
                    $"Clear keys ({GetClearOverlayHotkeyLabel()})");

                clearButton.Click += (_, _) => ClearAllKeysFromOverlay();

                _clearThumbButton = clearButton;

                // Attach both buttons to this window’s taskbar thumbnail
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) throw new InvalidOperationException("Window handle not ready.");

                TaskbarManager.Instance.ThumbnailToolBars.AddButtons(handle, transparentButton, clearButton);
            }
            catch (Exception ex)
            {
                // Use the app’s custom modern dialog system (ShowModernInfo) instead of MessageBox
                ShowModernInfo(
                    title: "Taskbar button",
                    message: $"Failed to add the thumbnail toolbar buttons.\n\n{ex.Message}",
                    icon: DialogIcon.Warning
                );
            }
        }

        /// <summary>Begin moving the window when the body is dragged.</summary>			
        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove(); // Allows the window to be dragged when clicking anywhere
        }

        /// <summary>Begin an OS-level resize when the user presses on an edge/corner grip.</summary>
        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_transparentToMouse || e.ChangedButton != MouseButton.Left)
                return; // ignore when transparent-to-mouse or not a left-click

            if (sender is FrameworkElement fe) // only named edge/corner elements participate
            {
                // Map named hit zones to Win32 hit-test direction codes (HT*)
                int direction = fe.Name switch
                {
                    "LeftResize" => 1,
                    "RightResize" => 2,
                    "TopResize" => 3,
                    "TopLeftResize" => 4,
                    "TopRightResize" => 5,
                    "BottomResize" => 6,
                    "BottomLeftResize" => 7,
                    "BottomRightResize" => 8,
                    _ => 0
                };

                // Native resize: only when we have a valid HWND and a non-zero direction
                if (direction != 0 &&
                    PresentationSource.FromVisual(this) is HwndSource s &&
                    s.Handle != IntPtr.Zero)
                {
                    NativeMethods.NativeResizeWindow(s.Handle, direction);
                }
            }
        }

        /// <summary>Minimize the overlay window.</summary>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        /// <summary>Close the overlay window.</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();



        // === Native hit-testing for borderless resize ===

        /// <summary>Win32 constants used in <see cref="WndProc"/> to implement resizing on a borderless window.</summary>
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_TOGGLE_CLICKTHROUGH = 0x8001;
        private const int HTTRANSPARENT = -1;
        private const int HTBOTTOMRIGHT = 17;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTBOTTOM = 15;
        private const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16;

        /// <summary>Handle non-client hit-testing so the window can be resized/dragged.</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // Handle our app-specific toggle message
            if (msg == WM_TOGGLE_CLICKTHROUGH)
            {
                // Flip the mode on the UI thread (and show the info prompt when turning on)
                Dispatcher.Invoke(() => SetTransparentMode(!_transparentToMouse, withPrompt: true));
                handled = true;
                return IntPtr.Zero;
            }

            // In transparent mode, let Windows pass mouse hit-testing through this overlay
            // to the window underneath.
            if (_transparentToMouse && msg == WM_NCHITTEST)
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }

            if (msg == WM_NCHITTEST)
            {
                // Robustly extract signed screen coordinates from lParam (works on x86/x64)
                int lp = lParam.ToInt32();
                short sx = unchecked((short)(lp & 0xFFFF));          // LOWORD signed
                short sy = unchecked((short)((lp >> 16) & 0xFFFF));  // HIWORD signed

                Point pos = PointFromScreen(new Point(sx, sy));
                double edge = ResizeHitEdgePx;

                // Corners first
                if (pos.X <= edge && pos.Y <= edge) { handled = true; return (IntPtr)HTTOPLEFT; }
                if (pos.X >= Width - edge && pos.Y <= edge) { handled = true; return (IntPtr)HTTOPRIGHT; }
                if (pos.X <= edge && pos.Y >= Height - edge) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
                if (pos.X >= Width - edge && pos.Y >= Height - edge) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }

                // Edges
                if (pos.X <= edge) { handled = true; return (IntPtr)HTLEFT; }
                if (pos.X >= Width - edge) { handled = true; return (IntPtr)HTRIGHT; }
                if (pos.Y <= edge) { handled = true; return (IntPtr)HTTOP; }
                if (pos.Y >= Height - edge) { handled = true; return (IntPtr)HTBOTTOM; }
            }

            return handled ? (IntPtr)1 : IntPtr.Zero;
        }


        // === Core overlay layout & right-click context menu ===

        /// <summary>Keeps either this window or an active dialog above all other windows.</summary>

        private void ReassertTopmost()
        {
            if (_suspendTopmostForMenu) return; // Don't fight the menu while it's open

            var target = _topmostTarget ?? this;

            // keep the chosen window above everything (taskbar/games-in-windowed) without stealing focus
            if (target.WindowState == WindowState.Minimized) return;
            NativeMethods.EnsureTopMost(target);
        }

        /// <summary>Build the overlay’s visual tree, auto-sizing rounded background, and the right-click context menu.</summary>
        private void SetupOverlayUI()
        {
            // ---- One-time guard  ------------------------------------------------
            if (_uiBuilt) return;
            _uiBuilt = true;

            // local radii (frame/pill)
            const double FrameRadius = 12;
            const double StripRadius = 20;

            // ---------- Window chrome & backdrop ----------------------------------
            Width = WindowWidth;
            Height = WindowHeight;
            Topmost = true;
            NativeMethods.EnsureTopMost(this);
            WindowStyle = WindowStyle.None;
            Background = Brushes.Transparent;

            // Solid ultra-low-alpha black; Freeze() for perf
            var frameBrush = new SolidColorBrush(Color.FromArgb(WindowAlpha, 0, 0, 0));
            if (frameBrush.CanFreeze) frameBrush.Freeze();
            RoundedVisualFrame.Background = frameBrush;

            RoundedVisualFrame.CornerRadius = new CornerRadius(FrameRadius);

            // Normal mode: stroke 1 + padding 1 = inset 2
            RoundedVisualFrame.BorderThickness = new Thickness(FrameStroke);
            RoundedVisualFrame.Padding = new Thickness(FrameTotalInset - FrameStroke);

            // Crisp pixels (snaps + layout rounding)
            RoundedVisualFrame.SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            UpdateWindowBorderChrome(); // theme-aware border

            // inset chrome buttons to match frame
            if (ChromeButtons != null)
            {
                var m = ChromeButtons.Margin;
                ChromeButtons.Margin = new Thickness(m.Left, FrameTotalInset, FrameTotalInset, m.Bottom);
            }

            // ---------- Mouse visuals & pill hosts -------------------------------------
            _horizontalContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = false,
            };

            // Mouse SVG view
            _mouseSvgDisplay = new SvgViewbox
            {
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(_mouseSvgDisplay, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(_mouseSvgDisplay, EdgeMode.Aliased);

            // analyzer: set via initializer
            _mouseBorder = new()
            {
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new(0),
                Child = _mouseSvgDisplay,
                RenderTransformOrigin = new(0.5, 0.5), // Set via initializer (analyzer)
                RenderTransform = new ScaleTransform(1.0, 1.0) // scaled at layout time
            };
            _horizontalContainer.Children.Add(_mouseBorder);

            // Auto-sizing rounded background (pill)
            _stripBackground = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)Math.Round(_backgroundOpacity * 255.0),
                    _backgroundColorRgb.R,
                    _backgroundColorRgb.G,
                    _backgroundColorRgb.B)),
                CornerRadius = new CornerRadius(StripRadius),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                ClipToBounds = false,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                Child = _horizontalContainer
            };

            // keys-outside container (mouse-only mode)
            _keysOutsideContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed // only visible in Mouse-only mode
            };

            // line host: [pill | keys-outside]
            _lineHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _lineHost.Children.Add(_stripBackground);
            _lineHost.Children.Add(_keysOutsideContainer);

            MainGrid.Children.Add(_lineHost);
            _stripBackground.Margin = new Thickness(0);
            Panel.SetZIndex(_lineHost, 1);

            // ---------- First-size pass: scale children & normalize gaps ---------------------------

            _stripBackground.SizeChanged += (_, __) =>
            {
                // Run baseline sizing after first measure
                ApplyContentScaleToChildren();
                NormalizeInterItemSpacing();
                SyncKeyUnitCacheFromTree();
                UpdateBackgroundWidth();
            };

            // ---------- Capture background brush state ------------------------------------------------

            _stripBackgroundBrushOn = _stripBackground.Background;
            if (_stripBackground.Background is SolidColorBrush sb)
            {
                _backgroundOpacity = sb.Color.A / 255.0;
                _backgroundColorRgb = Color.FromRgb(sb.Color.R, sb.Color.G, sb.Color.B);

                // Mutable brush instance for runtime updates
                _stripBackgroundBrushOn = new SolidColorBrush(sb.Color);
                if (_backgroundEnabled) _stripBackground.Background = _stripBackgroundBrushOn;
            }

            // ---------- Context menu: Save preset submenu ------------------------------------------------

            var cm = _globalContextMenu ??= new ContextMenu();
            Grid.SetIsSharedSizeScope(cm, true);

            cm.Opened += (_, __) =>
            {
                // Don’t fight the menu’s z-order while it’s open.
                _suspendTopmostForMenu = true;
                this.Topmost = false;
            };

            // ToolTip behavior (appears quickly, stays long enough to read)
            ApplyToolTipTiming(cm);

            // Custom header grid + icon
            var savePresetMenu = new MenuItem
            {
                ToolTip = "Save these settings as a preset, or load a saved preset.",
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // Close the submenu + its context menu
            void CloseContextMenu()
            {
                savePresetMenu.IsSubmenuOpen = false;
                if (cm != null) cm.IsOpen = false;
            }

            // Remove the default padding inside the submenu popup
            savePresetMenu.Resources[typeof(ContextMenu)] = new Style(typeof(ContextMenu))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0))
                }
            };

            // Build a two-column header like the preset rows: [label | right arrow]
            string arrowIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "submenuarrow.svg");

            var spHeader = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            spHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            spHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var spLabel = new TextBlock
            {
                Text = "Save preset",
                FontSize = MenuUI.ItemFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)  // same 8px gap you use before the ✕
            };
            Grid.SetColumn(spLabel, 0);

            // Themed arrow (tints to menu foreground)
            FrameworkElement spArrow = File.Exists(arrowIconPath)
                ? CreateThemedSvgIcon(arrowIconPath, MenuBrush, MenuUI.SubmenuArrowSize)
                : new TextBlock { Text = "›", FontSize = MenuUI.ItemFontSize, VerticalAlignment = VerticalAlignment.Center };
            spArrow.Margin = new Thickness(8, 0, 0, 0);
            spArrow.HorizontalAlignment = HorizontalAlignment.Right;
            spArrow.VerticalAlignment = VerticalAlignment.Center;
            spArrow.IsHitTestVisible = false;  // no hover, no click
            Grid.SetColumn(spArrow, 1);

            spHeader.Children.Add(spLabel);
            spHeader.Children.Add(spArrow);
            savePresetMenu.Header = spHeader;

            // Top-level icon (left glyph column) stays as-is
            string saveIconPath_Init = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "save.svg");
            if (File.Exists(saveIconPath_Init))
                savePresetMenu.Icon = CreateThemedSvgIcon(saveIconPath_Init, MenuBrush, MenuUI.IconSize);
            else
                savePresetMenu.Icon = new TextBlock { Text = "💾", FontSize = MenuUI.ItemFontSize, VerticalAlignment = VerticalAlignment.Center };

            // Submenu style mirrors look main menu
            savePresetMenu.Resources[MenuItem.SeparatorStyleKey] = cm.Resources[MenuItem.SeparatorStyleKey];
            savePresetMenu.Resources[typeof(MenuItem)] = cm.Resources[typeof(MenuItem)];

            // Keep the submenu width consistent AND retain the main MenuItem template via BasedOn
            var baseMiStyle = (Style)cm.Resources[typeof(MenuItem)];
            var childStyle = new Style(typeof(MenuItem)) { BasedOn = baseMiStyle };
            childStyle.Setters.Add(new Setter(MenuItem.MinWidthProperty, 300.0)); // temporary; refined on cm.Opened
            childStyle.Setters.Add(new Setter(MenuItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            childStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(0)));
            savePresetMenu.ItemContainerStyle = childStyle;

            // + Add preset command (keeps submenu open)
            var addPresetItem = new MenuItem
            {
                Header = "Add preset",
                StaysOpenOnClick = true,
                ToolTip = "Add a preset with the current settings."
            };

            // Use the SVG so it matches other menu icons exactly
            string plusIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "plus.svg");
            addPresetItem.Icon = CreateThemedSvgIcon(plusIconPath, MenuBrush, MenuUI.SubmenuArrowSize);

            // Inline new-preset editor (TextBox)
            var inlineNewItem = new MenuItem
            {
                StaysOpenOnClick = true,
                Visibility = Visibility.Collapsed,
                Focusable = false,
                Template = CreateContextMenuHostItemTemplate(),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // Placeholder config
            const string PresetNamePlaceholder = "Name preset";
            var hintBrush = SystemColors.GrayTextBrush;       // subtle hint color

            var nameInput = CreateContextMenuTextInput(PresetNamePlaceholder, minWidth: 200);

            var nameBox = nameInput.TextBox;
            var nameInputRoot = nameInput.Root;

            nameInputRoot.Margin = new Thickness(MenuUI.GlyphColWidth + MenuUI.ContentLeftInset, 0, 12, 0);

            // Focus: keep the placeholder, just select it so the first key replaces it
            nameBox.GotKeyboardFocus += (_, __) =>
            {
                if (nameBox.Text == PresetNamePlaceholder)
                {
                    nameBox.SelectAll(); // user typing immediately replaces the placeholder
                }
            };

            // Typing/paste: as soon as text differs from the placeholder, use normal brush
            nameBox.TextChanged += (_, __) =>
            {
                if (nameBox.Text != PresetNamePlaceholder)
                    nameInput.ApplyTheme();
            };

            // Blur: if empty, restore placeholder + hint color
            nameBox.LostKeyboardFocus += (_, __) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    nameBox.Text = PresetNamePlaceholder;
                    nameBox.Foreground = hintBrush;
                    nameBox.SelectAll();
                }
                else
                {
                    nameInput.ApplyTheme();
                }
            };

            inlineNewItem.Header = nameInputRoot;

            // Separator before live list
            var presetsSep = new Separator();

            // Assemble submenu
            savePresetMenu.Items.Add(addPresetItem);
            savePresetMenu.Items.Add(inlineNewItem);
            savePresetMenu.Items.Add(presetsSep);

            // Local state for inline editor
            bool inlineActive = false;
            bool committing = false;
            bool lastCloseByEsc = false;   // track if the menu closed via ESC (to cancel)

            // Rebuild live preset list under the separator
            void RebuildPresetList()
            {
                // prevent row Click while inline rename editor is open
                bool renamingNow = false;

                // delay single-click apply to allow double-click rename
                bool labelClickPending = false;
                DispatcherTimer? labelClickTimer = null;

                Directory.CreateDirectory(PresetsDir);

                // Remove anything AFTER presetsSep
                int idx = savePresetMenu.Items.IndexOf(presetsSep);
                while (savePresetMenu.Items.Count > idx + 1)
                    savePresetMenu.Items.RemoveAt(savePresetMenu.Items.Count - 1);

                // Track whether any preset rows were added
                bool addedAny = false;

                // Load files and add as clickable items
                var files = Directory.GetFiles(PresetsDir, "*.json")
                                     .OrderBy(p => IOPath.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                                     .ToList();

                // Inline rename for a preset file.
                void BeginInlineRename(string oldPath, TextBlock label, Grid row)
                {
                    string oldName = System.IO.Path.GetFileNameWithoutExtension(oldPath);

                    var renameInput = CreateContextMenuTextInput(oldName, minWidth: 200);

                    var editor = renameInput.TextBox;
                    var editorRoot = renameInput.Root;

                    editorRoot.Margin = label.Margin;
                    editorRoot.VerticalAlignment = VerticalAlignment.Center;

                    // Put editor in the same grid cell as the label
                    Grid.SetColumn(editorRoot, 0);

                    // Swap label -> editor (same index so layout stays stable)
                    int labelIndex = row.Children.IndexOf(label);
                    row.Children.RemoveAt(labelIndex);
                    row.Children.Insert(labelIndex, editorRoot);

                    // State used while inline rename is active
                    renamingNow = true;
                    editor.Focus();
                    editor.SelectAll();

                    // Guard so Commit/Cancel only run once
                    bool finished = false;

                    // Parent MenuItem that owns this preset row.
                    MenuItem? ownerRow = FindAncestor<MenuItem>(row);

                    // Remember whether this was the active preset, because Tag is also
                    // used for the active-preset highlight.
                    object? originalTag = ownerRow?.Tag;

                    if (ownerRow != null)
                    {
                        ownerRow.StaysOpenOnClick = true;
                        ownerRow.Tag = RenamingPresetTag;
                    }

                    // Captured reference declared first so local functions can use it.
                    MouseButtonEventHandler? commitOnMenuClick = null;

                    // While renaming: keep the keyboard focus anchored to the editor
                    void KeepEditorFocusOnMove(object? _, MouseEventArgs __)
                    {
                        if (!editor.IsKeyboardFocusWithin)
                            Dispatcher.BeginInvoke(new Action(() => editor.Focus()), DispatcherPriority.Background);
                    }

                    void KeepEditorFocusOnKb(object? _, KeyboardFocusChangedEventArgs e)
                    {
                        // If focus is leaving the whole context menu, let LostKeyboardFocus handle it.
                        if (!IsSourceInside(e.NewFocus as DependencyObject, cm)) return;

                        // Redirect focus attempts inside the menu back to the editor
                        if (!IsSourceInside(e.NewFocus as DependencyObject, editor))
                        {
                            e.Handled = true;
                            Dispatcher.BeginInvoke(new Action(() => editor.Focus()), DispatcherPriority.Background);
                        }
                    }

                    // Create concrete delegate instances so we can detach the same ones later
                    var keepEditorFocusOnMove = new MouseEventHandler(KeepEditorFocusOnMove);
                    var keepEditorFocusOnKb = new KeyboardFocusChangedEventHandler(KeepEditorFocusOnKb);

                    void SafeSwapBackEditorToLabel()
                    {
                        // Only swap if the editor is still in the row
                        // (avoids ArgumentOutOfRangeException).
                        int idx = row.Children.IndexOf(editorRoot);

                        if (idx >= 0)
                        {
                            row.Children.RemoveAt(idx);
                            row.Children.Insert(idx, label);
                            label.Visibility = Visibility.Visible;
                        }

                        // Restore normal row behavior and its original active/non-active state.
                        if (ownerRow != null)
                        {
                            ownerRow.StaysOpenOnClick = false;
                            ownerRow.Tag = originalTag;
                        }
                    }

                    void DetachTempHandlers()
                    {
                        if (commitOnMenuClick != null)
                        {
                            savePresetMenu.PreviewMouseDown -= commitOnMenuClick;
                            cm.PreviewMouseDown -= commitOnMenuClick;
                        }

                        savePresetMenu.PreviewMouseMove -= keepEditorFocusOnMove;
                        cm.PreviewMouseMove -= keepEditorFocusOnMove;

                        cm.RemoveHandler(Keyboard.GotKeyboardFocusEvent, keepEditorFocusOnKb);
                    }

                    void Cancel()
                    {
                        if (finished) return;
                        finished = true;

                        // Keep submenu visible if menu is still open
                        savePresetMenu.IsSubmenuOpen = true;

                        DetachTempHandlers();
                        SafeSwapBackEditorToLabel();

                        renamingNow = false;
                    }

                    void Commit()
                    {
                        if (finished) return;
                        finished = true;

                        DetachTempHandlers();

                        string proposed = editor.Text?.Trim() ?? string.Empty;

                        // No change or empty? Just revert without touching files.
                        if (string.IsNullOrWhiteSpace(proposed) ||
                            string.Equals(proposed, oldName, StringComparison.OrdinalIgnoreCase))
                        {
                            SafeSwapBackEditorToLabel();
                            renamingNow = false;
                            savePresetMenu.IsSubmenuOpen = true;
                            return;
                        }

                        // Compute unique new path
                        string newPath = GetUniquePresetPath(proposed);

                        try
                        {
                            // Rename the file on disk
                            File.Move(oldPath, newPath);

                            // If this preset was active, update pointer
                            if (string.Equals(_prefs.LastPresetPath, oldPath, StringComparison.OrdinalIgnoreCase))
                            {
                                _prefs.LastPresetPath = newPath;
                                SavePrefs();
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowModernInfo("Rename failed", ex.Message, ok: "OK", icon: DialogIcon.Error);
                        }

                        // Restore the temporary rename state before rebuilding the row.
                        if (ownerRow != null)
                        {
                            ownerRow.StaysOpenOnClick = false;
                            ownerRow.Tag = originalTag;
                        }

                        // Rebuild list to reflect new name; keep submenu open so highlight is visible.
                        RebuildPresetList();
                        savePresetMenu.IsSubmenuOpen = true;
                        renamingNow = false;
                    }

                    // Keep parent submenu open while renaming.
                    savePresetMenu.IsSubmenuOpen = true;

                    // Click-away inside the menu (but not in the editor) => commit (matches "Add preset" UX)
                    commitOnMenuClick = (_, e) =>
                    {
                        if (!IsSourceInside(e.OriginalSource as DependencyObject, editor))
                        {
                            e.Handled = true;  // prevent the row’s Click from firing with a stale path
                            Commit();
                        }
                    };

                    // Temporary handlers: removed in Commit/Cancel
                    savePresetMenu.PreviewMouseDown += commitOnMenuClick;
                    cm.PreviewMouseDown += commitOnMenuClick;

                    savePresetMenu.PreviewMouseMove += keepEditorFocusOnMove;
                    cm.PreviewMouseMove += keepEditorFocusOnMove;

                    cm.AddHandler(Keyboard.GotKeyboardFocusEvent, keepEditorFocusOnKb, /* handledEventsToo */ true);

                    // Keyboard: Enter=commit, Esc=cancel
                    editor.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                        else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
                    };

                    // Only cancel when focus leaves the entire context menu
                    editor.LostKeyboardFocus += (_, e) =>
                    {
                        if (!IsSourceInside(e.NewFocus as DependencyObject, cm))
                            Cancel();
                    };
                }

                foreach (var path in files)
                {
                    string name = IOPath.GetFileNameWithoutExtension(path);

                    // Row: [ preset name (fills) ][ Save ][ ✕ ]
                    var row = new Grid
                    {
                        Margin = new Thickness(0, 0, -8, 0),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // save
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // delete

                    var label = new TextBlock
                    {
                        Text = name,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 8, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        ToolTip = "Apply this preset's settings.",
                        FontSize = MenuUI.ItemFontSize
                    };
                    Grid.SetColumn(label, 0);

                    // Track the first-click point so we can early-apply when the mouse moves/escapes
                    Point? _labelFirstClickPoint = null;

                    label.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                        new MouseButtonEventHandler((s, e) =>
                        {
                            if (renamingNow) { e.Handled = true; return; }

                            // SECOND click within the system double-click interval -> rename
                            if (e.ClickCount == 2)
                            {
                                e.Handled = true;

                                _labelFirstClickPoint = null;

                                // cancel any pending single-click apply
                                if (labelClickPending && labelClickTimer != null)
                                {
                                    labelClickTimer.Stop();
                                    labelClickPending = false;
                                    // labelClickTimer = null; // (optional)
                                }

                                BeginInlineRename(path, label, row);
                                return;
                            }

                            // FIRST click: hold the apply until we know if a second click arrives
                            if (!labelClickPending)
                            {
                                e.Handled = true; // keep the row from applying right away

                                labelClickPending = true;
                                labelClickTimer ??= new DispatcherTimer { Interval = SingleClickGrace };
                                labelClickTimer.Tick += (_, __) =>
                                {
                                    // timer expired => it's a real single-click; apply preset now
                                    labelClickTimer!.Stop();
                                    labelClickPending = false;

                                    if (!renamingNow)
                                    {
                                        ApplyPresetFromPath(path);
                                        RebuildPresetList();
                                        CloseContextMenu();
                                    }
                                };

                                _labelFirstClickPoint = e.GetPosition(label);
                                labelClickTimer.Start();
                            }
                            else
                            {
                                // Safety: should be covered by ClickCount==2 path above
                                e.Handled = true;
                            }
                        }),
                        /* handledEventsToo: */ true);

                    label.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                        new MouseButtonEventHandler((s, e) =>
                        {
                            // Swallow the release so MenuItem.Click doesn't fire from the label
                            e.Handled = true;
                        }),
                        /* handledEventsToo: */ true);

                    // EARLY-APPLY: If the mouse moves beyond drag threshold after first click, apply immediately
                    label.AddHandler(UIElement.MouseMoveEvent,
                        new MouseEventHandler((s, e) =>
                        {
                            if (!renamingNow && labelClickPending && _labelFirstClickPoint.HasValue && labelClickTimer != null)
                            {
                                var p = e.GetPosition(label);
                                if (Math.Abs(p.X - _labelFirstClickPoint.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                                    Math.Abs(p.Y - _labelFirstClickPoint.Value.Y) > SystemParameters.MinimumVerticalDragDistance)
                                {
                                    labelClickTimer.Stop();
                                    labelClickPending = false;
                                    _labelFirstClickPoint = null;

                                    ApplyPresetFromPath(path);
                                    RebuildPresetList();
                                    CloseContextMenu();
                                }
                            }
                        }),
                        /* handledEventsToo: */ true);

                    // EARLY-APPLY: If the mouse leaves the label before second click, apply immediately
                    label.AddHandler(UIElement.MouseLeaveEvent,
                        new MouseEventHandler((s, e) =>
                        {
                            if (!renamingNow && labelClickPending && labelClickTimer != null)
                            {
                                labelClickTimer.Stop();
                                labelClickPending = false;
                                _labelFirstClickPoint = null;

                                ApplyPresetFromPath(path);
                                RebuildPresetList();
                                CloseContextMenu();
                            }
                        }),
                        /* handledEventsToo: */ true);

                    var presetIconButtonTemplate =
                        CreateContextMenuIconButtonTemplate();

                    // Save (overwrite) button for this preset
                    var saveBtn = new Button
                    {
                        Width = 24,
                        Height = 24,
                        Template = presetIconButtonTemplate,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent,
                        BorderBrush = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Focusable = false,
                        Cursor = Cursors.Hand,
                        ToolTip = $"Save (overwrite) this preset.\n{path}",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Content = CreateThemedSvgIcon(IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "save.svg"), MenuBrush, MenuUI.IconSize)
                    };
                    Grid.SetColumn(saveBtn, 1);

                    // Clicking the save icon overwrites *this* preset and makes it active
                    saveBtn.Click += (_, __) =>
                    {
                        SaveCurrentStateToPreset(path, makeActive: true);
                        RebuildPresetList();
                        savePresetMenu.IsSubmenuOpen = true; // Stay open to show updated highlight
                    };

                    var delBtn = new Button
                    {
                        Width = 24,
                        Height = 24,
                        Template = presetIconButtonTemplate,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent,
                        BorderBrush = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Focusable = false,
                        Cursor = Cursors.Hand,
                        ToolTip = "Delete preset",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Content = new TextBlock
                        {
                            Text = "✕",
                            FontSize = MenuUI.ItemFontSize,
                            Foreground = MenuBrush,
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    };
                    Grid.SetColumn(delBtn, 2);

                    var mi = new MenuItem
                    {
                        Header = row,
                        StaysOpenOnClick = false,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        ToolTip = "Apply this preset's settings."
                    };

                    // Mark the currently active preset so the style trigger can highlight it
                    if (string.Equals(_prefs.LastPresetPath, path, StringComparison.OrdinalIgnoreCase))
                        mi.Tag = ActivePresetTag;
                    else
                        mi.Tag = null; // Default/non-active

                    // Bullet in the glyph column (U+2022)
                    mi.Icon = new TextBlock
                    {
                        Text = "\u2022",
                        FontSize = MenuUI.ItemFontSize + 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    // Click anywhere on the row (except the ✕) to apply the preset
                    mi.Click += (_, __) =>
                    {
                        if (renamingNow) return;    // ignore clicks while inline editor is up
                        ApplyPresetFromPath(path);
                        RebuildPresetList();
                        CloseContextMenu();
                    };

                    // Delete via ✕ (keep submenu open)
                    delBtn.Click += (s, e) =>
                    {
                        e.Handled = true; // Don't let the row Click fire

                        bool confirmed = ShowModernYesNo(
                            "Delete preset?",
                            $"Are you sure you want to delete “{name}”?\nThis action can’t be undone.",
                            yes: "Delete",
                            no: "Cancel",
                            icon: DialogIcon.Warning
                        );
                        if (!confirmed) return;

                        try
                        {
                            File.Delete(path);

                            // If it was the last-used preset, forget it
                            if (string.Equals(_prefs.LastPresetPath, path, StringComparison.OrdinalIgnoreCase))
                            {
                                _prefs.LastPresetPath = null;
                                SavePrefs();
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowModernInfo("Delete failed", ex.Message, ok: "OK", icon: DialogIcon.Error);
                            return;
                        }

                        RebuildPresetList();
                        savePresetMenu.IsSubmenuOpen = false;   // After a confirmation dialog, do NOT auto-open next time
                    };

                    // Assemble row
                    row.Children.Add(label);
                    row.Children.Add(saveBtn);
                    row.Children.Add(delBtn);

                    savePresetMenu.Items.Add(mi);
                    addedAny = true;
                }

                // Separator only when something is shown under it
                presetsSep.Visibility = addedAny ? Visibility.Visible : Visibility.Collapsed;
            }

            // Begin inline editor
            void BeginInlineAdd()
            {
                // Enforce max
                int count = Directory.Exists(PresetsDir) ? Directory.GetFiles(PresetsDir, "*.json").Length : 0;
                if (count >= MaxPresets)
                {
                    ShowModernInfo(
                        title: "Preset limit reached",
                        message: $"You already have {count} presets. The limit is {MaxPresets}.\n\nDelete one first.",
                        ok: "OK",
                        icon: DialogIcon.Info
                    ); // themed info dialog
                    return;
                }

                inlineNewItem.Visibility = Visibility.Visible;
                inlineActive = true;

                // Focus/select after submenu positions
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    nameBox.Focus();
                    nameBox.SelectAll();
                }), DispatcherPriority.Background);
            }

            // Cancel inline editor (no save)
            void CancelInline()
            {
                inlineActive = false;
                inlineNewItem.Visibility = Visibility.Collapsed;
            }

            // Save inline editor if there is a name present
            void CommitInlineIfNeeded()
            {
                if (!inlineActive || committing) return;

                string proposed = nameBox.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(proposed))
                {
                    // No name = don't save. Keep editor open and re-focus the box.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (inlineActive)
                        {
                            nameBox.Focus();
                            nameBox.SelectAll();
                        }
                    }), DispatcherPriority.Background);
                    return;
                }

                committing = true;
                try
                {
                    // Enforce max again
                    int count = Directory.Exists(PresetsDir) ? Directory.GetFiles(PresetsDir, "*.json").Length : 0;
                    if (count < MaxPresets)
                    {
                        var data = BuildPresetFromCurrent();
                        string path = GetUniquePresetPath(proposed);
                        string json = JsonSerializer.Serialize(data, PrefsJsonOptions);
                        File.WriteAllText(path, json);

                        // Apply immediately so it's remembered as last-used
                        ApplyPresetFromPath(path);
                    }
                }
                catch (Exception ex)
                {
                    ShowModernInfo(title: "Save failed", message: "Failed to save preset:\n" + ex.Message, ok: "OK", icon: DialogIcon.Error); // themed error dialog
                }
                finally
                {
                    CancelInline();
                    RebuildPresetList();
                    committing = false;
                }
            }

            // Walk up visual tree to test "source inside target"
            bool IsSourceInside(DependencyObject? source, FrameworkElement target)
            {
                DependencyObject? cur = source;
                while (cur != null)
                {
                    if (ReferenceEquals(cur, target)) return true;
                    cur = (cur as FrameworkElement)?.Parent ?? VisualTreeHelper.GetParent(cur);
                }
                return false;
            }

            // Find the first ancestor of a given type in the visual tree
            T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
            {
                while (d != null)
                {
                    if (d is T t) return t;
                    d = (d as FrameworkElement)?.Parent ?? VisualTreeHelper.GetParent(d);
                }
                return null;
            }

            // Keep TextBox focus while editing
            void ForceEditorFocusIfNeeded()
            {
                if (inlineActive && !nameBox.IsKeyboardFocusWithin)
                    Dispatcher.BeginInvoke(new Action(() => nameBox.Focus()), DispatcherPriority.Background);
            }

            // Wire inline editor events
            addPresetItem.Click += (_, __) =>
            {
                if (inlineActive) CancelInline();
                else BeginInlineAdd();
            };

            // Enter = save, Esc = cancel
            nameBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitInlineIfNeeded(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelInline(); e.Handled = true; }
            };

            // Track ESC on the whole menu to cancel-on-close
            cm.PreviewKeyDown += (_, e) =>
            {
                if (!inlineActive) return;
                if (e.Key == Key.Escape) lastCloseByEsc = true;
            };

            // Prevent focus loss on mere mouse movement
            savePresetMenu.PreviewMouseMove += (_, __) => ForceEditorFocusIfNeeded();
            cm.PreviewMouseMove += (_, __) => ForceEditorFocusIfNeeded();

            // Click-away saves (inside menu)
            savePresetMenu.PreviewMouseDown += (_, e) =>
            {
                if (!inlineActive) return;
                if (IsSourceInside(e.OriginalSource as DependencyObject, nameBox)) return;
                CommitInlineIfNeeded();
            };
            cm.PreviewMouseDown += (_, e) =>
            {
                if (!inlineActive) return;
                if (IsSourceInside(e.OriginalSource as DependencyObject, nameBox)) return;
                CommitInlineIfNeeded();
            };

            cm.Closed += (_, __) =>
            {
                // Handle inline editor (only if it was active)
                if (inlineActive)
                {
                    if (lastCloseByEsc)
                    {
                        lastCloseByEsc = false;
                        CancelInline();
                    }
                    else
                    {
                        string proposed = nameBox.Text?.Trim() ?? "";
                        if (proposed == PresetNamePlaceholder) proposed = "";
                        if (!string.IsNullOrWhiteSpace(proposed)) CommitInlineIfNeeded();
                        else CancelInline();
                    }
                }
                else
                {
                    // no inline editor -> just clear the ESC flag
                    lastCloseByEsc = false;
                }

                // ALWAYS reclaim overlay’s z-order after any menu close
                _suspendTopmostForMenu = false;
                this.Topmost = true;
                ReassertTopmost();
                savePresetMenu.IsSubmenuOpen = false; // Ensure submenu won't auto-open on next show
            };

            // Insert submenu at top of context menu
            cm.Items.Insert(0, savePresetMenu);

            // Initial list build
            RebuildPresetList();

            // Toggle preset switching
            var presetToggleHotkeyItem = new MenuItem
            {
                Header = "Enable preset switch shortcut",
                IsCheckable = true,
                IsChecked = _prefs.PresetToggleHotkeyEnabled,
                ToolTip = "Enable or disable the shortcut for switching between the current preset and the previous preset.",
                StaysOpenOnClick = true
            };

            // Transparent-mode (checkable) – goes at the very top
            var transparentItem = new MenuItem
            {
                Header = "Toggle Transparent-mode",
                IsCheckable = true,
                IsChecked = _transparentToMouse,
                ToolTip = $"Disables mouse interaction with KeyClickOverlay. Exit with {GetTransparentHotkeyLabel()} or via the taskbar hover menu (hover its icon).",
                StaysOpenOnClick = true
            };

            // Remember it so we can update the tooltip text when the shortcut for the mode gets changed
            _transparentMenuItem = transparentItem;

            // Pause overlay input display
            var pauseOverlayItem = new MenuItem
            {
                Header = "Pause KeyClickOverlay",
                IsCheckable = true,
                IsChecked = _overlayPaused,
                ToolTip =
                    $"Pause or resume the display of keyboard and mouse input. " +
                    $"You can also use {GetPauseOverlayHotkeyLabel()}.",
                StaysOpenOnClick = true
            };

            // Submenu for changing application shortcuts
            var customizeShortcutsMenu = new MenuItem
            {
                ToolTip = "Change KeyClickOverlay shortcuts.",
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // Build a two-column header like “Save preset”: [label | right arrow]
            var shortcutsHeader = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };
            shortcutsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shortcutsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var shortcutsLabel = new TextBlock
            {
                Text = "Customize Shortcuts",
                FontSize = MenuUI.ItemFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(shortcutsLabel, 0);

            FrameworkElement shortcutsArrow = File.Exists(arrowIconPath)
                ? CreateThemedSvgIcon(arrowIconPath, MenuBrush, MenuUI.SubmenuArrowSize)
                : new TextBlock { Text = "›", FontSize = MenuUI.ItemFontSize, VerticalAlignment = VerticalAlignment.Center };

            shortcutsArrow.Margin = new Thickness(8, 0, 0, 0);
            shortcutsArrow.HorizontalAlignment = HorizontalAlignment.Right;
            shortcutsArrow.VerticalAlignment = VerticalAlignment.Center;
            shortcutsArrow.IsHitTestVisible = false;
            Grid.SetColumn(shortcutsArrow, 1);

            shortcutsHeader.Children.Add(shortcutsLabel);
            shortcutsHeader.Children.Add(shortcutsArrow);
            customizeShortcutsMenu.Header = shortcutsHeader;

            // Left icon for the main menu row
            customizeShortcutsMenu.Icon = new TextBlock
            {
                Text = "⌘",
                FontSize = MenuUI.ItemFontSize + 6,
                FontWeight = FontWeights.Light,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            };

            var presetSwitchShortcutText = new TextBlock
            {
                Text = GetPresetSwitchHotkeyLabel()
            };

            var presetSwitchToggleShortcutText = new TextBlock
            {
                Text = GetPresetSwitchToggleHotkeyLabel()
            };

            var clearOverlayShortcutText = new TextBlock
            {
                Text = GetClearOverlayHotkeyLabel()
            };

            var pauseOverlayShortcutText = new TextBlock
            {
                Text = GetPauseOverlayHotkeyLabel()
            };

            var transparentModeShortcutText = new TextBlock
            {
                Text = GetTransparentHotkeyLabel()
            };

            var changePresetSwitchHotkeyItem = new MenuItem
            {
                Header = CreateShortcutMenuHeader("Preset switch shortcut", presetSwitchShortcutText),
                ToolTip =
                    "Switches between the current preset and the previously used preset.\n\n" +
                    "Click to change this shortcut."
            };

            var changePresetSwitchToggleHotkeyItem = new MenuItem
            {
                Header = CreateShortcutMenuHeader("Preset-switch toggle shortcut", presetSwitchToggleShortcutText),
                ToolTip =
                    "Enables or disables the preset switch shortcut.\n\n" +
                    "Click to change this shortcut."
            };

            var changeClearOverlayHotkeyItem = new MenuItem
            {
                Header = CreateShortcutMenuHeader("Clear KeyClickOverlay shortcut", clearOverlayShortcutText),
                ToolTip =
                    "Removes all currently displayed keys.\n\n" +
                    "Click to change this shortcut."
            };

            var changePauseOverlayHotkeyItem = new MenuItem
            {
                Header = CreateShortcutMenuHeader("Pause KeyClickOverlay shortcut", pauseOverlayShortcutText),

                ToolTip =
                    "Pauses or resumes keyboard and mouse input display.\n\n" +
                    "Click to change this shortcut."
            };

            // Existing shortcut editor moved into the submenu
            var changeHotkeyItem = new MenuItem
            {
                Header = CreateShortcutMenuHeader("Transparent-mode shortcut", transparentModeShortcutText),
                ToolTip =
                    "Enables or disables Transparent Mode, allowing mouse clicks to pass through the overlay.\n\n" +
                    "Click to change this shortcut."
            };

            customizeShortcutsMenu.Items.Add(changeHotkeyItem);
            customizeShortcutsMenu.Items.Add(changePresetSwitchHotkeyItem);
            customizeShortcutsMenu.Items.Add(changePresetSwitchToggleHotkeyItem);
            customizeShortcutsMenu.Items.Add(changeClearOverlayHotkeyItem);
            customizeShortcutsMenu.Items.Add(changePauseOverlayHotkeyItem);

            // Top-level master mouse toggle (goes above "Toggle Background")
            var toggleMouseItem = new MenuItem
            {
                Header = "Toggle Mouse",
                IsCheckable = true,
                IsChecked = _mouseEnabled,
                ToolTip = "Show or hide the mouse image.",
                StaysOpenOnClick = true
            };

            // Show/Hide background
            var showBgItem = new MenuItem
            {
                Header = "Toggle Background",
                IsCheckable = true,
                IsChecked = _backgroundEnabled,
                ToolTip = "Show or hide the background.",
                StaysOpenOnClick = true
            };

            // Set background color
            var bgColorItem = new MenuItem
            {
                Header = new TextBlock { Text = "Set background color" },
                ToolTip = "Pick the background color."
            };

            // Build the initial icon using the current menu Foreground (theme-aware)
            string iconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "keybgcolor.svg");
            if (File.Exists(iconPath))
            {
                bgColorItem.Icon = CreateThemedSvgIcon(iconPath, MenuBrush, MenuUI.IconSize);
            }

            // Prepare the "Reset to defaults" item (we'll append it at the very end)
            var resetItem = new MenuItem
            {
                Header = "Reset to defaults",
                ToolTip = "Delete settings and tint caches and return to factory defaults"
            };

            // Separator above "Reset to defaults" (toggled with background group)
            var sepBeforeReset = new Separator();

            // Measure text width in DIPs for submenu width calc
            double MeasureMenuTextWidth(string text, double fontSize)
            {
                var typeface = new Typeface(SystemFonts.MenuFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText(
                    text ?? string.Empty,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black,
                    dip
                );
                return ft.WidthIncludingTrailingWhitespace;
            }


            // ---------- Context menu: core toggles & background group --------------

            // Insert an item right after a given item (fallback Add)
            void InsertAfter(ItemsControl menu, object item, object afterItem)
            {
                int idx = menu.Items.IndexOf(afterItem);
                if (idx >= 0)
                    menu.Items.Insert(idx + 1, item);
                else
                    menu.Items.Add(item);
            }

            // Toggle shortcut preset toggle
            cm.Items.Insert(1, presetToggleHotkeyItem);

            // Transparent-mode
            cm.Items.Add(transparentItem);

            // Pause KeyClickOverlay directly below Transparent-mode
            InsertAfter(cm, pauseOverlayItem, transparentItem);

            // Separator under the Pause row
            var sepAfterTransparent = new Separator();
            InsertAfter(cm, sepAfterTransparent, pauseOverlayItem);

            // Handler for enabling/disabling the preset-switch shortcut
            presetToggleHotkeyItem.Checked += (_, __) =>
            {
                if (_syncingMenu) return;

                _prefs.PresetToggleHotkeyEnabled = true;
                SavePrefs();
            };

            presetToggleHotkeyItem.Unchecked += (_, __) =>
            {
                if (_syncingMenu) return;

                _prefs.PresetToggleHotkeyEnabled = false;
                SavePrefs();
            };

            // Handlers for transparency toggle
            transparentItem.Checked += (_, __) =>
            {
                if (_syncingMenu) return;
                SetTransparentMode(true, withPrompt: true);
                _syncingMenu = true;
                transparentItem.IsChecked = _transparentToMouse;
                _syncingMenu = false;
            };
            transparentItem.Unchecked += (_, __) =>
            {
                if (_syncingMenu) return;
                SetTransparentMode(false, withPrompt: false);
            };

            // Handlers for Pause KeyClickOverlay
            pauseOverlayItem.Checked += (_, __) =>
            {
                if (_syncingMenu) return;

                SetOverlayPaused(true);

                // Make sure the menu reflects the actual resulting state.
                _syncingMenu = true;
                pauseOverlayItem.IsChecked = _overlayPaused;
                _syncingMenu = false;
            };

            pauseOverlayItem.Unchecked += (_, __) =>
            {
                if (_syncingMenu) return;

                SetOverlayPaused(false);

                // Make sure the menu reflects the actual resulting state.
                _syncingMenu = true;
                pauseOverlayItem.IsChecked = _overlayPaused;
                _syncingMenu = false;
            };

            // Handler for "Set shortcut..." menu items
            changeHotkeyItem.Click +=
                (_, __) => ChangeTransparentHotkeyViaDialog();

            changePresetSwitchHotkeyItem.Click +=
                (_, __) => ChangePresetSwitchHotkeyViaDialog();

            changePresetSwitchToggleHotkeyItem.Click +=
                (_, __) => ChangePresetSwitchToggleHotkeyViaDialog();

            changeClearOverlayHotkeyItem.Click +=
                (_, __) => ChangeClearOverlayHotkeyViaDialog();

            changePauseOverlayHotkeyItem.Click +=
                (_, __) => ChangePauseOverlayHotkeyViaDialog();


            // Toggle Mouse (under the separator)
            InsertAfter(cm, toggleMouseItem, sepAfterTransparent);

            // "Set mouse color" (under Toggle Mouse)
            var mouseColorItem = new MenuItem
            {
                Header = "Set mouse color",
                ToolTip = "Pick the color of the mouse image."
            };

            {
                string mouseIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "mousecolor.svg");
                if (File.Exists(mouseIconPath))
                    mouseColorItem.Icon = CreateThemedSvgIcon(mouseIconPath, MenuBrush, MenuUI.IconSize);
            }
            InsertAfter(cm, mouseColorItem, toggleMouseItem);
            mouseColorItem.Click += (_, __) => PickMouseColorViaDialog();

            // Separator under “Set mouse color”
            var sepAfterMouseColor = new Separator();
            InsertAfter(cm, sepAfterMouseColor, mouseColorItem);

            // Keep mouse color row in sync with mouse enabled/disabled
            toggleMouseItem.Checked += (_, __) =>
            {
                if (_syncingMenu) return;
                ApplyMouseEnabled(true);
                UpdateMouseMenuState(true);   // Show row + separator
            };
            toggleMouseItem.Unchecked += (_, __) =>
            {
                if (_syncingMenu) return;
                ApplyMouseEnabled(false);
                UpdateMouseMenuState(false);  // Hide both
            };

            // Show/hide the “Set mouse color” row (and separator) based on mouse enabled state
            void UpdateMouseMenuState(bool enabled)
            {
                var vis = enabled ? Visibility.Visible : Visibility.Collapsed;
                mouseColorItem.Visibility = vis;
                sepAfterMouseColor.Visibility = vis;

                // Hide/show the separator under "Transparent-mode" too
                sepAfterTransparent.Visibility = vis;
            }

            // Initialize once using current state
            UpdateMouseMenuState(_mouseEnabled);

            // Set font color (under the mouse-color separator)
            var fontColorItem = new MenuItem
            {
                Header = "Set font color",
                ToolTip = "Pick the font color of the keys."
            };

            {
                string fontIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "fontcolor.svg");
                if (File.Exists(fontIconPath))
                    fontColorItem.Icon = CreateThemedSvgIcon(fontIconPath, MenuBrush, MenuUI.IconSize);
            }
            InsertAfter(cm, fontColorItem, sepAfterMouseColor);
            fontColorItem.Click += (_, __) => PickFontColorViaDialog();

            // "Set key color" (under "Set font color")
            var keyColorItem = new MenuItem
            {
                Header = "Set key color",
                ToolTip = "Pick the fill color of the key tiles."
            };

            {
                string keyIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "keycolor.svg");
                if (File.Exists(keyIconPath))
                    keyColorItem.Icon = CreateThemedSvgIcon(keyIconPath, MenuBrush, MenuUI.IconSize);
            }
            InsertAfter(cm, keyColorItem, fontColorItem);
            keyColorItem.Click += (_, __) => PickKeyFillColorViaDialog();

            // Separator under “Set key color”
            var sepAfterKeyColor = new Separator();
            InsertAfter(cm, sepAfterKeyColor, keyColorItem);

            // “Toggle Background” (starts background group)
            InsertAfter(cm, showBgItem, sepAfterKeyColor);

            // Mouse-only toggle (stays in the background group)
            var mouseOnlyItem = new MenuItem
            {
                Header = "Mouse only",
                IsCheckable = true,
                IsChecked = _mouseOnlyBackground,
                StaysOpenOnClick = true,
                ToolTip = "Show the background only around the mouse image."
            };
            cm.Items.Add(mouseOnlyItem);

            // Set background color (stays in the background group)
            cm.Items.Add(bgColorItem);

            // Background padding row (insert right after bg color)
            var (padItem, _, _, padSync) = CreateFactorRow(
                title: "Background padding",
                min: PillBounds.PaddingMin, max: PillBounds.PaddingMax,
                getValue: () => _pillPadFactor,
                setValue: v => SetPillPaddingFactor(v));
            padItem.ToolTip = "Size the background.";
            int colorIdx = cm.Items.IndexOf(bgColorItem);
            if (colorIdx >= 0 && !cm.Items.Contains(padItem))
                cm.Items.Insert(colorIdx + 1, padItem);

            // Corner radius row (under padding)
            var (cornerItem, _, _, cornerSync) = CreateFactorRow(
                title: "Corner radius",
                min: PillBounds.CornerMin, max: PillBounds.CornerMax,
                getValue: () => _pillCornerFactor,
                setValue: v => SetPillCornerFactor(v));
            cornerItem.ToolTip = "Roundness of the background corners.";
            int padIdx = cm.Items.IndexOf(padItem);
            if (padIdx >= 0 && !cm.Items.Contains(cornerItem))
                cm.Items.Insert(padIdx + 1, cornerItem);

            // Separator under "Corner radius"
            var sepAfterCorner = new Separator();
            int cornerIdx = cm.Items.IndexOf(cornerItem);
            if (cornerIdx >= 0 && !cm.Items.Contains(sepAfterCorner))
                cm.Items.Insert(cornerIdx + 1, sepAfterCorner);

            // Window size row under the separator
            string windowSizeIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "WindowSize.svg");

            // Build 2-row section
            var (sizeTitleItem, sizeEditorItem, sizeSync) =
                CreateWindowSizeRow("Window size", windowSizeIconPath);

            // Window position (bottom-left) under window size
            string windowPosIconPath = IOPath.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "assets", "svg", "WindowPosition.svg");

            var (posTitleItem, posEditorItem, posSync) = CreateWindowBottomLeftRow("Window position (bottom-left)", windowPosIconPath);

            // Insert under sepAfterCorner: title row first, editor row second
            int sepIdx = cm.Items.IndexOf(sepAfterCorner);
            if (sepIdx >= 0)
            {
                cm.Items.Insert(sepIdx + 1, sizeTitleItem);
                cm.Items.Insert(sepIdx + 2, sizeEditorItem);

                cm.Items.Insert(sepIdx + 3, posTitleItem);
                cm.Items.Insert(sepIdx + 4, posEditorItem);
            }

            // Show/hide the background group rows as a unit
            void UpdateBgColorItemState(bool enabled)
            {
                var vis = enabled ? Visibility.Visible : Visibility.Collapsed;

                // Show/hide the "Mouse only" toggle
                mouseOnlyItem.Visibility = vis;

                // Show/hide BG color settings
                bgColorItem.Visibility = vis;

                // Show/hide the padding row
                padItem.Visibility = vis;

                // Show/hide the corner radius row
                cornerItem.Visibility = vis;

                // Hide/show the separators that bracket the background group
                sepAfterKeyColor.Visibility = vis;   // separator under "Set key color"
                sepBeforeReset.Visibility = vis;   // separator above "Reset to defaults"
            }

            // ---------- Handlers for background group & reset ----------
            showBgItem.Checked += (_, __) =>
            {
                SetBackgroundEnabled(true);
                UpdateBgColorItemState(true);   // Reflect immediately if menu stays open
            };
            showBgItem.Unchecked += (_, __) =>
            {
                SetBackgroundEnabled(false);
                UpdateBgColorItemState(false);
            };

            mouseOnlyItem.Checked += (_, __) => ApplyMouseOnlyMode(true);
            mouseOnlyItem.Unchecked += (_, __) => ApplyMouseOnlyMode(false);

            bgColorItem.Click += (_, __) => PickBackgroundColorViaDialog();

            // Icon: prefer SVG; fallback to glyph if missing
            string refreshIconPath_Init = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "refresh.svg");
            if (File.Exists(refreshIconPath_Init))
            {
                resetItem.Icon = CreateThemedSvgIcon(refreshIconPath_Init, MenuBrush, MenuUI.IconSize);
            }
            else
            {
                resetItem.Icon = new TextBlock
                {
                    Text = "↻",
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            // Reset to defaults
            resetItem.Click += (_, __) => ResetToDefaultsAndClearData();

            // ---------- On open: sync widths, state, and retint icons ----------
            cm.Opened += (_, __) =>
            {
                _suspendTopmostForMenu = true;  // Pause our top-most enforcement
                this.Topmost = false;           // Let the menu pop above us

                _syncingMenu = true; // Begin guard

                // Submenu starts from our constants (not the main menu's current width); may grow later
                double baseInnerWidth = Math.Max(0, MainMenuWidth - (MenuUI.ChromePadding * 2));

                // Measure longest preset name (DIPs)
                double longestNameWidth = 0;
                try
                {
                    var files = Directory.GetFiles(PresetsDir, "*.json");
                    foreach (var path in files)
                    {
                        string name = IOPath.GetFileNameWithoutExtension(path);
                        longestNameWidth = Math.Max(longestNameWidth, MeasureMenuTextWidth(name, MenuUI.ItemFontSize));
                    }
                }
                catch { /* Ignore IO errors; fallback to baseInnerWidth */ }

                // Row width math: glyph col + left inset + name + 8 + 24 (save) + 24 (✕) + 12
                double presetRowInnerNeeded =
                    MenuUI.GlyphColWidth +
                    MenuUI.ContentLeftInset +
                    longestNameWidth +
                    8 +
                    24 + // save
                    24 + // delete
                    12;

                // Final inner width
                double submenuInnerWidth = Math.Max(baseInnerWidth, presetRowInnerNeeded);

                // Apply min width to submenu items
                var baseMi = (Style)cm.Resources[typeof(MenuItem)];
                var newItemContainerStyle = new Style(typeof(MenuItem)) { BasedOn = baseMi };
                newItemContainerStyle.Setters.Add(new Setter(MenuItem.MinWidthProperty, submenuInnerWidth));
                newItemContainerStyle.Setters.Add(new Setter(MenuItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                savePresetMenu.ItemContainerStyle = newItemContainerStyle;

                // Inline editor tracks submenu width
                inlineNewItem.MinWidth = submenuInnerWidth;

                nameInputRoot.Width = double.NaN;
                nameInputRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
                nameInputRoot.Margin = new Thickness(8, 0, 8, 0);

                nameInput.ApplyTheme();

                // Sync state
                RebuildPresetList();
                presetToggleHotkeyItem.IsChecked = _prefs.PresetToggleHotkeyEnabled;
                transparentItem.IsChecked = _transparentToMouse;
                pauseOverlayItem.IsChecked = _overlayPaused;
                toggleMouseItem.IsChecked = _mouseEnabled; // sync the correct item explicitly
                showBgItem.IsChecked = _backgroundEnabled; // sync background explicitly

                transparentModeShortcutText.Text = GetTransparentHotkeyLabel();
                presetSwitchShortcutText.Text = GetPresetSwitchHotkeyLabel();
                presetSwitchToggleShortcutText.Text = GetPresetSwitchToggleHotkeyLabel();
                clearOverlayShortcutText.Text = GetClearOverlayHotkeyLabel();
                pauseOverlayShortcutText.Text = GetPauseOverlayHotkeyLabel();

                pauseOverlayItem.ToolTip =
                    $"Pause or resume the display of keyboard and mouse input. " +
                    $"You can also use {GetPauseOverlayHotkeyLabel()}.";

                // Show/hide background group
                UpdateBgColorItemState(_backgroundEnabled);

                //  Retint the “Set background color” icon
                if (bgColorItem.Icon is Image img && img.Source is DrawingImage di)
                    TintDrawingRecursive(di.Drawing, MenuBrush);
                else if (File.Exists(iconPath))
                    bgColorItem.Icon = CreateThemedSvgIcon(iconPath, MenuBrush, MenuUI.IconSize);
                else
                    bgColorItem.Icon = null;

                // Retint "Save preset" icon
                {
                    string path = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "save.svg");
                    if (savePresetMenu.Icon is Image spImg && spImg.Source is DrawingImage spDi)
                        TintDrawingRecursive(spDi.Drawing, MenuBrush);
                    else if (File.Exists(path))
                        savePresetMenu.Icon = CreateThemedSvgIcon(path, MenuBrush, MenuUI.IconSize);
                }

                // Retint custom header arrow; enforce size
                if (savePresetMenu.Header is Grid h && h.Children.Count >= 2 && h.Children[1] is Image arr)
                {
                    if (arr.Source is DrawingImage adi)
                        TintDrawingRecursive(adi.Drawing, MenuBrush);
                    arr.Width = MenuUI.SubmenuArrowSize;
                    arr.Height = MenuUI.SubmenuArrowSize;
                }

                // Retint "Customize Shortcuts" submenu arrow; enforce size
                if (customizeShortcutsMenu.Header is Grid sh && sh.Children.Count >= 2 && sh.Children[1] is Image shortcutArr)
                {
                    if (shortcutArr.Source is DrawingImage sadi)
                        TintDrawingRecursive(sadi.Drawing, MenuBrush);
                    shortcutArr.Width = MenuUI.SubmenuArrowSize;
                    shortcutArr.Height = MenuUI.SubmenuArrowSize;
                }

                // Retint “+ Add preset” icon; enforce size
                {
                    string path = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "plus.svg");
                    if (addPresetItem.Icon is Image pImg)
                    {
                        if (pImg.Source is DrawingImage pDi)
                            TintDrawingRecursive(pDi.Drawing, MenuBrush);

                        pImg.Width = MenuUI.SubmenuArrowSize;
                        pImg.Height = MenuUI.SubmenuArrowSize;
                    }
                    else if (File.Exists(path))
                    {
                        addPresetItem.Icon = CreateThemedSvgIcon(path, MenuBrush, MenuUI.SubmenuArrowSize);
                    }
                }

                // Retint the "Set mouse color" icon to the current menu foreground
                string mouseIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "mousecolor.svg");
                if (mouseColorItem.Icon is Image mimg && mimg.Source is DrawingImage mdi)
                    TintDrawingRecursive(mdi.Drawing, MenuBrush);
                else if (File.Exists(mouseIconPath))
                    mouseColorItem.Icon = CreateThemedSvgIcon(mouseIconPath, MenuBrush, MenuUI.IconSize);
                else
                    mouseColorItem.Icon = null;

                // Retint the "Set font color" icon to current menu foreground
                string fontIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "fontcolor.svg");
                if (fontColorItem.Icon is Image fimg && fimg.Source is DrawingImage fdi)
                    TintDrawingRecursive(fdi.Drawing, MenuBrush);
                else if (File.Exists(fontIconPath))
                    fontColorItem.Icon = CreateThemedSvgIcon(fontIconPath, MenuBrush, MenuUI.IconSize);
                else
                    fontColorItem.Icon = null;

                // Retint the "Set key color" icon to current menu foreground
                string keyIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "keycolor.svg");
                if (keyColorItem.Icon is Image kimg && kimg.Source is DrawingImage kdi)
                    TintDrawingRecursive(kdi.Drawing, MenuBrush);
                else if (File.Exists(keyIconPath))
                    keyColorItem.Icon = CreateThemedSvgIcon(keyIconPath, MenuBrush, MenuUI.IconSize);
                else
                    keyColorItem.Icon = null;

                // Retint the "Reset to defaults" icon to current menu foreground
                string refreshIconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "refresh.svg");
                if (resetItem.Icon is Image rimg && rimg.Source is DrawingImage rdi)
                    TintDrawingRecursive(rdi.Drawing, MenuBrush);
                else if (File.Exists(refreshIconPath))
                    resetItem.Icon = CreateThemedSvgIcon(refreshIconPath, MenuBrush, MenuUI.IconSize);

                // Retint the "Window size" icon
                if (sizeTitleItem.Icon is Image wsImg && wsImg.Source is DrawingImage wsDi)
                    TintDrawingRecursive(wsDi.Drawing, MenuBrush);
                else if (File.Exists(windowSizeIconPath))
                    sizeTitleItem.Icon = CreateThemedSvgIcon(windowSizeIconPath, MenuBrush, MenuUI.IconSize);
                else
                    sizeTitleItem.Icon = null;

                // Retint the "Window position" icon
                if (posTitleItem.Icon is Image wpImg && wpImg.Source is DrawingImage wpDi)
                    TintDrawingRecursive(wpDi.Drawing, MenuBrush);
                else if (File.Exists(windowPosIconPath))
                    posTitleItem.Icon = CreateThemedSvgIcon(windowPosIconPath, MenuBrush, MenuUI.IconSize);
                else
                    posTitleItem.Icon = null;

                // Sync dynamic items
                mouseOnlyItem.IsChecked = _mouseOnlyBackground;
                padSync();
                cornerSync();
                sizeSync();
                posSync();

                _syncingMenu = false;

                cm.UpdateLayout();

                // Keep the main menu fixed at the constant width (no pinning / no growth)
                cm.MinWidth = MainMenuWidth;
                cm.Width = MainMenuWidth;
                cm.MaxWidth = MainMenuWidth;

                // Ensure "Set mouse color" visibility matches current mouse state
                UpdateMouseMenuState(_mouseEnabled);

                // Refresh the window outline to the current theme
                UpdateWindowBorderChrome();
            };

            // ---------- Finalize context menu ----------
            cm.Items.Add(sepBeforeReset);
            cm.Items.Add(customizeShortcutsMenu);
            cm.Items.Add(resetItem);
            ApplyWin11ContextMenuStyle(cm); // Win11 look

            // ---------- Right-click + responsive metrics ----------
            MainGrid.Background = Brushes.Transparent; // Hit empty areas
            PreviewMouseRightButtonUp += (s, e) =>
            {
                OpenGlobalContextMenuFromCurrentPointer();
                e.Handled = true;
            };

            // Initial compute (run once now)
            UpdateStripBackgroundMetrics();

            // Coalesced, height-only full recompute on next frame after first present
            ContentRendered += (_, __) => QueueFullRelayout();

            // Only care about height; width changes alone do not require a full rebuild
            SizeChanged += OnWindowSizeChanged;

            _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollHideMs) };
            _scrollTimer.Tick += (_, _) =>
            {
                _scrollTimer.Stop();
                SetMouseSvg("mouse_idle.svg");
            };

            UpdatePillVisibility(); // Hide pill when no content
        }

        /// <summary>Give the context menu Windows 11-style chrome (rounded, separators, hover).</summary>
        private static void ApplyWin11ContextMenuStyle(ContextMenu? cm)
        {
            if (cm is null) return;

            // Theme brushes (light/dark)
            var bg = new SolidColorBrush(AppTheme.MenuBackgroundColor)
            {
                Opacity = 0.96
            };

            var fg = new SolidColorBrush(AppTheme.MenuForegroundColor);
            if (fg.CanFreeze)
                fg.Freeze();

            var hoverBg = new SolidColorBrush(AppTheme.MenuHoverColor);
            if (hoverBg.CanFreeze)
                hoverBg.Freeze();

            var outline = new SolidColorBrush(AppTheme.WindowOutlineColor);
            if (outline.CanFreeze)
                outline.Freeze();

            // Text selection colors
            var selectionBg = SolidBrush(AppTheme.IsLight ? "#FF9A9A9A" : "#FF606060");
            var selectionFg = SolidBrush(AppTheme.IsLight ? "#FF111111" : "#FFFFFFFF");

            var textBoxStyle = new Style(typeof(TextBox));
            textBoxStyle.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, selectionBg));
            textBoxStyle.Setters.Add(new Setter(TextBoxBase.SelectionTextBrushProperty, selectionFg));
            cm.Resources[typeof(TextBox)] = textBoxStyle;

            // ---------- Context-menu slider resources ----------

            // Inactive track.
            var sliderTrack = SolidBrush(AppTheme.IsLight ? "#FFD0D0D0" : "#FF666666");

            // Filled/value track.
            var sliderValue = SolidBrush(AppTheme.IsLight ? "#FF8A8A8A" : "#FFB8B8B8");

            // Thumb.
            var sliderThumb = SolidBrush(AppTheme.IsLight ? "#FF3F3F3F" : "#FFE0E0E0");

            // Hover/drag thumb.
            var sliderThumbHover = SolidBrush(AppTheme.IsLight ? "#FF202020" : "#FFFFFFFF");

            // Kept for compatibility even though dragging now uses hover color.
            var sliderThumbPressed = sliderThumbHover;

            if (sliderTrack.CanFreeze)
                sliderTrack.Freeze();

            if (sliderValue.CanFreeze)
                sliderValue.Freeze();

            if (sliderThumb.CanFreeze)
                sliderThumb.Freeze();

            if (sliderThumbHover.CanFreeze)
                sliderThumbHover.Freeze();

            cm.Resources["ContextMenuSliderTrackBrush"] =
                sliderTrack;

            cm.Resources["ContextMenuSliderValueBrush"] =
                sliderValue;

            cm.Resources["ContextMenuSliderThumbBrush"] =
                sliderThumb;

            cm.Resources["ContextMenuSliderThumbHoverBrush"] =
                sliderThumbHover;

            cm.Resources["ContextMenuSliderThumbPressedBrush"] =
                sliderThumbPressed;

            var sliderDictionary = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KeyClickOverlay;component/Styles/ContextMenuSliderStyle.xaml", UriKind.Absolute)
            };

            foreach (DictionaryEntry entry in sliderDictionary)
            {
                cm.Resources[entry.Key] = entry.Value;
            }

            // Context menu root (rounded border + shadow + padding)
            var root = new FrameworkElementFactory(typeof(Border));
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(MenuUI.CornerRadius));
            root.SetValue(Border.BackgroundProperty, bg);
            root.SetValue(Border.BorderBrushProperty, outline);
            root.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            root.SetValue(Border.PaddingProperty, new Thickness(MenuUI.ChromePadding));
            root.SetValue(Border.SnapsToDevicePixelsProperty, true);
            root.SetValue(Border.EffectProperty, new DropShadowEffect { BlurRadius = MenuUI.ShadowBlur, ShadowDepth = 0, Opacity = 0.35 });

            // Scrollable items host
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            root.AppendChild(scroll);

            cm.Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = root };
            cm.UseLayoutRounding = true;
            cm.MinWidth = MainMenuWidth;
            cm.Width = MainMenuWidth;       // lock the main menu width
            cm.MaxWidth = MainMenuWidth;    // prevent the main menu from growing
            cm.Foreground = fg;
            cm.BorderThickness = new Thickness(0);

            // ToolTip style - match the context menu theme.
            var toolTipTemplate = new ControlTemplate(typeof(System.Windows.Controls.ToolTip));
            var toolTipBorder = new FrameworkElementFactory(typeof(Border));
            toolTipBorder.SetValue(Border.BackgroundProperty, bg);
            toolTipBorder.SetValue(Border.BorderBrushProperty, outline);
            toolTipBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            toolTipBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            toolTipBorder.SetValue(Border.PaddingProperty, new Thickness(8, 5, 8, 5));
            toolTipBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

            // Wrapping tooltip text
            var toolTipText = new FrameworkElementFactory(typeof(TextBlock));
            toolTipText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent),
                Path = new PropertyPath(System.Windows.Controls.ToolTip.ContentProperty)
            });
            toolTipText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            toolTipText.SetValue(TextBlock.MaxWidthProperty, 360.0);
            toolTipText.SetValue(TextBlock.FontSizeProperty, 13.0);
            toolTipText.SetValue(TextBlock.ForegroundProperty, fg);
            toolTipBorder.AppendChild(toolTipText);
            toolTipTemplate.VisualTree = toolTipBorder;

            var toolTipStyle = new Style(typeof(System.Windows.Controls.ToolTip));
            toolTipStyle.Setters.Add(new Setter(System.Windows.Controls.ToolTip.TemplateProperty, toolTipTemplate));
            cm.Resources[typeof(System.Windows.Controls.ToolTip)] = toolTipStyle;

            // Separator (thin line with vertical margins)
            var sepTpl = new ControlTemplate(typeof(Separator));
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(FrameworkElement.HeightProperty, 1.0);
            line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 4));
            line.SetValue(Border.BackgroundProperty, outline);
            line.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            sepTpl.VisualTree = line;

            var sepStyle = new Style(typeof(Separator));
            sepStyle.Setters.Add(new Setter(Separator.TemplateProperty, sepTpl));
            cm.Resources[MenuItem.SeparatorStyleKey] = sepStyle;

            // Menu item style (glyph column + content)
            var miStyle = new Style(typeof(MenuItem));
            miStyle.Setters.Add(new Setter(Control.FontSizeProperty, MenuUI.ItemFontSize));
            miStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, MenuUI.ItemPadding));
            miStyle.Setters.Add(new Setter(MenuItem.MarginProperty, MenuUI.ItemMargin));
            miStyle.Setters.Add(new Setter(MenuItem.MinHeightProperty, MenuUI.ItemMinHeight));
            miStyle.Setters.Add(new Setter(MenuItem.BackgroundProperty, Brushes.Transparent));
            miStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, fg));
            miStyle.Setters.Add(new Setter(MenuItem.FocusVisualStyleProperty, null));

            // Item container (rounded hover background)
            var outer = new FrameworkElementFactory(typeof(Border)) { Name = "Outer" };
            outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            outer.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            outer.SetValue(FrameworkElement.MarginProperty, MenuUI.ItemMargin);
            outer.SetValue(Border.SnapsToDevicePixelsProperty, true);

            // Two-column grid: glyph (fixed) + content (fill)
            var grid = new FrameworkElementFactory(typeof(Grid));
            var colGlyph = new FrameworkElementFactory(typeof(ColumnDefinition)) { Name = "GlyphCol" };
            colGlyph.SetValue(ColumnDefinition.WidthProperty, new GridLength(MenuUI.GlyphColWidth));
            var colContent = new FrameworkElementFactory(typeof(ColumnDefinition));
            colContent.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            grid.AppendChild(colGlyph);
            grid.AppendChild(colContent);

            // Check/cross glyphs for checkable rows (hidden by default)
            var checkGlyph = new FrameworkElementFactory(typeof(TextBlock)) { Name = "CheckGlyph" };
            checkGlyph.SetValue(Grid.ColumnProperty, 0);
            checkGlyph.SetValue(TextBlock.TextProperty, "✓");
            checkGlyph.SetValue(TextBlock.FontSizeProperty, MenuUI.ItemFontSize);
            checkGlyph.SetValue(TextBlock.ForegroundProperty, fg);
            checkGlyph.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkGlyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkGlyph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            var crossGlyph = new FrameworkElementFactory(typeof(TextBlock)) { Name = "CrossGlyph" };
            crossGlyph.SetValue(Grid.ColumnProperty, 0);
            crossGlyph.SetValue(TextBlock.TextProperty, "✕");
            crossGlyph.SetValue(TextBlock.FontSizeProperty, MenuUI.ItemFontSize);
            crossGlyph.SetValue(TextBlock.ForegroundProperty, fg);
            crossGlyph.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            crossGlyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            crossGlyph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);

            // Icon presenter (binds to MenuItem.Icon)
            var iconPresenter = new FrameworkElementFactory(typeof(ContentPresenter)) { Name = "IconPresenter" };
            iconPresenter.SetValue(Grid.ColumnProperty, 0);
            iconPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            iconPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            iconPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Icon");
            iconPresenter.SetValue(UIElement.VisibilityProperty, Visibility.Visible);

            // Header presenter (binds MenuItem.Header)
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(Grid.ColumnProperty, 1);
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(FrameworkElement.MarginProperty, new Thickness(MenuUI.ContentLeftInset, 0, 12, 0));

            grid.AppendChild(checkGlyph);
            grid.AppendChild(crossGlyph);
            grid.AppendChild(iconPresenter);
            grid.AppendChild(content);
            outer.AppendChild(grid);

            // Menu item template (triggers set hover/checked/visibility)
            var tpl = new ControlTemplate(typeof(MenuItem));

            // Hover/disabled/check states
            var tHover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            tHover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Outer"));

            var tDisabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            tDisabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5, "Outer"));

            var tCheckable = new Trigger { Property = MenuItem.IsCheckableProperty, Value = true };
            tCheckable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "IconPresenter"));

            var tChecked = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
            tChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CheckGlyph"));
            tChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "CrossGlyph"));

            var tUncheckedCheckable = new MultiTrigger();
            tUncheckedCheckable.Conditions.Add(new Condition(MenuItem.IsCheckableProperty, true));
            tUncheckedCheckable.Conditions.Add(new Condition(MenuItem.IsCheckedProperty, false));
            tUncheckedCheckable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "CrossGlyph"));
            tUncheckedCheckable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "CheckGlyph"));

            var tNoIconAndNotCheckable = new MultiTrigger();
            tNoIconAndNotCheckable.Conditions.Add(new Condition(MenuItem.IsCheckableProperty, false));
            tNoIconAndNotCheckable.Conditions.Add(new Condition(MenuItem.IconProperty, null));
            tNoIconAndNotCheckable.Setters.Add(new Setter(ColumnDefinition.WidthProperty, new GridLength(0), "GlyphCol"));
            tNoIconAndNotCheckable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "IconPresenter"));

            tpl.Triggers.Add(tHover);
            tpl.Triggers.Add(tDisabled);
            tpl.Triggers.Add(tCheckable);
            tpl.Triggers.Add(tChecked);
            tpl.Triggers.Add(tUncheckedCheckable);
            tpl.Triggers.Add(tNoIconAndNotCheckable);

            // Highlight active preset row (same look as hover)
            var tActivePreset = new Trigger { Property = FrameworkElement.TagProperty, Value = ActivePresetTag };
            tActivePreset.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Outer"));
            tActivePreset.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            tpl.Triggers.Add(tActivePreset);

            // Suppress row hover while renaming a preset
            var tRenamingPreset = new Trigger { Property = FrameworkElement.TagProperty, Value = RenamingPresetTag };
            tRenamingPreset.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent, "Outer"));
            tpl.Triggers.Add(tRenamingPreset);

            // Submenu popup (same chrome as main)
            var rootContainer = new FrameworkElementFactory(typeof(Grid));
            rootContainer.AppendChild(outer);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsSubmenuOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(MenuUI.CornerRadius));
            popupBorder.SetValue(Border.BackgroundProperty, bg);
            popupBorder.SetValue(Border.BorderBrushProperty, outline);
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.PaddingProperty, new Thickness(MenuUI.ChromePadding));
            popupBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);
            popupBorder.SetValue(Border.EffectProperty, new DropShadowEffect { BlurRadius = MenuUI.ShadowBlur, ShadowDepth = 0, Opacity = 0.35 });

            var popupScroll = new FrameworkElementFactory(typeof(ScrollViewer));
            popupScroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            popupScroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            popupScroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);

            var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
            popupScroll.AppendChild(itemsPresenter);
            popupBorder.AppendChild(popupScroll);
            popup.AppendChild(popupBorder);

            rootContainer.AppendChild(popup);
            tpl.VisualTree = rootContainer;

            miStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, tpl));
            cm.Resources[typeof(MenuItem)] = miStyle;
        }

        /// <summary>
        /// Applies the shared tooltip duration.
        /// Tooltip opening delay is handled separately so every tooltip is delayed.
        /// </summary>
        private static void ApplyToolTipTiming(DependencyObject target)
        {
            ToolTipService.SetShowDuration(target, ToolTipDuration);
            ToolTipService.SetInitialShowDelay(target, ToolTipDelay);
        }

        /// <summary>
        /// Returns true when the current cursor position is inside this window's bounds.
        /// Uses screen coordinates, so it works for both local WPF RMB and global-hook RMB.
        /// </summary>
        private bool IsCursorInsideOverlayWindow()
        {
            if (!IsLoaded || WindowState == WindowState.Minimized)
                return false;

            double w = ActualWidth;
            double h = ActualHeight;

            if (double.IsNaN(w) || w <= 0) w = Width;
            if (double.IsNaN(h) || h <= 0) h = Height;

            var p = System.Windows.Forms.Control.MousePosition;
            Point dip = PointFromScreen(new Point(p.X, p.Y));

            return dip.X >= 0 && dip.X <= w &&
                   dip.Y >= 0 && dip.Y <= h;
        }

        /// <summary>
        /// Opens the main context menu at the current mouse position when it is inside the window.
        /// Uses a short debounce so local WPF RMB and global-hook RMB do not double-open it.
        /// </summary>
        private void OpenGlobalContextMenuFromCurrentPointer()
        {
            if (_transparentToMouse) return;
            if (!IsCursorInsideOverlayWindow()) return;

            var menu = _globalContextMenu;
            if (menu is null) return;

            var now = DateTime.UtcNow;
            if ((now - _lastContextMenuOpenUtc).TotalMilliseconds < 250)
                return;

            _lastContextMenuOpenUtc = now;

            menu.PlacementTarget = MainGrid;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        /// <summary>Context-menu design tokens (single source of truth)</summary>
        private static class MenuUI
        {
            // Outer menu chrome
            public const double CornerRadius = 8.0;
            public const double ShadowBlur = 14.0;
            public const double Padding = 6.0;   // Left/right/top/bottom padding
            public const double ChromePadding = 0.0; // Inner padding of the menu panel (main + submenu)

            // Item row
            public const double ItemFontSize = 16.0;
            public static readonly Thickness ItemPadding = new(12, 8, 12, 8);
            public static readonly Thickness ItemMargin = new(2);
            public const double ItemMinHeight = 32.0;

            // Glyph column (✓/✕ or icon)
            public const double GlyphColWidth = 24.0;
            public const double ContentLeftInset = 4.0; // Content margin inside row (matches your template)

            // Icon defaults
            public const double IconSize = 16.0;
            public const double SubmenuArrowSize = 12.0;

            // Context menu number boxes
            public const double FactorInputWidth = 64.0;
            public const double GeometryLabelGap = 18.0;
            public const double RowRightInset = 10.0;
            public const double NumericInputHeight = 30.0;
            public const double NumericClearButtonWidth = 20.0;
            public const double NumericInputCornerRadius = 6.0;
            public const double NumericInputRightInset = 4.0;
        }

        /// <summary>
        /// Creates the shared hover template for compact icon buttons used in the context menu.
        /// </summary>
        private static ControlTemplate CreateContextMenuIconButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));

            var chrome = new FrameworkElementFactory(typeof(Border))
            {
                Name = "ButtonChrome"
            };

            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            chrome.AppendChild(content);
            template.VisualTree = chrome;

            var hoverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, SolidBrush("#307F7F7F"), "ButtonChrome"));
            template.Triggers.Add(hoverTrigger);

            return template;
        }

        /// <summary>
        /// Creates a transparent host-only MenuItem template without hover chrome.
        /// </summary>
        private static ControlTemplate CreateContextMenuHostItemTemplate()
        {
            var template = new ControlTemplate(typeof(MenuItem));

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(FrameworkElement.MarginProperty, new Thickness(0));

            border.AppendChild(content);
            template.VisualTree = border;

            return template;
        }

        /// <summary>
        /// Applies the shared context-menu input colors to a text field and its rounded container.
        /// </summary>
        private static void ApplyContextMenuInputTheme(Border border, TextBox textBox, bool isHovering)
        {
            bool focused = textBox.IsKeyboardFocusWithin;

            if (AppTheme.IsLight)
            {
                border.Background = focused
                    ? SolidBrush("#FFE8E8E8")
                    : SolidBrush("#FFFFFFFF");

                border.BorderBrush = focused
                    ? SolidBrush("#73000000")
                    : isHovering
                        ? SolidBrush("#40000000")
                        : SolidBrush("#26000000");

                textBox.Foreground = SolidBrush("#111111");
                textBox.CaretBrush = SolidBrush("#111111");
            }
            else
            {
                border.Background = focused
                    ? SolidBrush("#FF202020")
                    : SolidBrush("#FF333333");

                border.BorderBrush = focused
                    ? SolidBrush("#90FFFFFF")
                    : isHovering
                        ? SolidBrush("#55FFFFFF")
                        : SolidBrush("#30FFFFFF");

                textBox.Foreground = SolidBrush("#EEEEEE");
                textBox.CaretBrush = SolidBrush("#FFFFFF");
            }
        }

        /// <summary>Create a compact, theme-aware numeric text input for the context menu.</summary>
        private (Border Root, TextBox TextBox, Button ClearButton, Action<double> SetValue, Func<double?> GetValue, Action ApplyTheme)
        CreateNumericInput(
            double initialValue,
            double minimum,
            double maximum,
            double step,
            double? width,
            string numberFormat,
            bool showStepButtons = false,
            Action<double>? onValueCommitted = null,
            bool stretchToFill = false)
        {

            var border = new Border
            {
                Width = stretchToFill
                    ? double.NaN
                    : (width ?? throw new ArgumentNullException(nameof(width)))
                        + MenuUI.NumericInputRightInset,

                Height = MenuUI.NumericInputHeight,
                CornerRadius = new CornerRadius(MenuUI.NumericInputCornerRadius),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 0, MenuUI.NumericInputRightInset, 0),
                HorizontalAlignment = stretchToFill ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                SnapsToDevicePixels = true
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MenuUI.NumericClearButtonWidth) });

            var textBox = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 0, 0, 0),
                FontSize = MenuUI.ItemFontSize,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null
            };

            Grid.SetColumn(textBox, 0);

            var clearGlyph = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            var clearButton = new Button
            {
                Content = clearGlyph,

                Width = MenuUI.NumericClearButtonWidth - 2,
                Height = MenuUI.NumericClearButtonWidth - 2,

                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),

                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,

                Focusable = false,
                FocusVisualStyle = null,
                Cursor = Cursors.Hand,

                Visibility = Visibility.Hidden
            };

            var stepPanel = new Grid
            {
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,

                Visibility = showStepButtons
                    ? Visibility.Visible
                    : Visibility.Hidden
            };

            stepPanel.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star)
                });

            stepPanel.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star)
                });

            (Button Button, System.Windows.Shapes.Path Glyph)
                CreateStepButton(bool pointsUp)
            {
                var glyph = new System.Windows.Shapes.Path
                {
                    Data = pointsUp
                        ? Geometry.Parse("M 1,5 L 5,1 L 9,5")
                        : Geometry.Parse("M 1,1 L 5,5 L 9,1"),

                    StrokeThickness = 1.0,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,

                    Width = 10,
                    Height = 6,
                    Stretch = Stretch.None,

                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,

                    IsHitTestVisible = false
                };

                var button = new Button
                {
                    Style = new Style(typeof(Button)),
                    Content = glyph,

                    Padding = new Thickness(0),
                    Margin = new Thickness(0),

                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),

                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,

                    Focusable = true,
                    FocusVisualStyle = null,
                    Cursor = Cursors.Hand
                };

                return (button, glyph);
            }

            var (stepUpButton, stepUpGlyph) = CreateStepButton(pointsUp: true);
            var (stepDownButton, stepDownGlyph) = CreateStepButton(pointsUp: false);

            Grid.SetRow(stepUpButton, 0);
            Grid.SetRow(stepDownButton, 1);

            stepPanel.Children.Add(stepUpButton);
            stepPanel.Children.Add(stepDownButton);

            var buttonHost = new Grid();

            buttonHost.Children.Add(stepPanel);
            buttonHost.Children.Add(clearButton);

            Grid.SetColumn(buttonHost, 1);

            grid.Children.Add(textBox);
            grid.Children.Add(buttonHost);

            border.Child = grid;

            var iconButtonTemplate = CreateContextMenuIconButtonTemplate();

            clearButton.Template = iconButtonTemplate;
            stepUpButton.Template = iconButtonTemplate;
            stepDownButton.Template = iconButtonTemplate;

            double NormalizeValue(double value)
            {
                value = Math.Clamp(value, minimum, maximum);

                if (step > 0)
                    value = Math.Round(value / step) * step;

                return Math.Clamp(value, minimum, maximum);
            }

            string FormatValue(double value) =>
                value.ToString(numberFormat, System.Globalization.CultureInfo.CurrentCulture);

            double lastValidValue = NormalizeValue(initialValue);

            double? ReadValue()
            {
                if (!double.TryParse(textBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double value))
                {
                    return null;
                }

                return NormalizeValue(value);
            }

            void SetValue(double value)
            {
                value = NormalizeValue(value);
                lastValidValue = value;
                textBox.Text = FormatValue(value);
            }

            void StepValue(double direction)
            {
                double currentValue =
                    ReadValue() ?? lastValidValue;

                double newValue =
                    NormalizeValue(currentValue + (step * direction));

                SetValue(newValue);
                onValueCommitted?.Invoke(newValue);
            }

            stepUpButton.Click += (_, __) =>
            {
                StepValue(1);
            };

            stepDownButton.Click += (_, __) =>
            {
                StepValue(-1);
            };

            void RestoreLastValid()
            {
                textBox.Text = FormatValue(lastValidValue);
            }

            void CommitCurrentValue()
            {
                if (ReadValue() is double value)
                {
                    SetValue(value);
                    onValueCommitted?.Invoke(value);
                }
                else
                {
                    RestoreLastValid();
                }
            }

            bool isHovering = false;

            void ApplyTheme()
            {
                ApplyContextMenuInputTheme(border, textBox, isHovering);

                if (AppTheme.IsLight)
                {
                    clearGlyph.Foreground = SolidBrush("#606060");
                    stepUpGlyph.Stroke = SolidBrush("#606060");
                    stepDownGlyph.Stroke = SolidBrush("#606060");
                }
                else
                {
                    clearGlyph.Foreground = SolidBrush("#BDBDBD");
                    stepUpGlyph.Stroke = SolidBrush("#BDBDBD");
                    stepDownGlyph.Stroke = SolidBrush("#BDBDBD");
                }
            }

            textBox.GotKeyboardFocus += (_, __) =>
            {
                if (showStepButtons)
                    stepPanel.Visibility = Visibility.Hidden;

                clearButton.Visibility = Visibility.Visible;

                textBox.SelectAll();
                ApplyTheme();
            };

            textBox.LostKeyboardFocus += (_, __) =>
            {
                clearButton.Visibility = Visibility.Hidden;

                if (showStepButtons)
                    stepPanel.Visibility = Visibility.Visible;

                CommitCurrentValue();
                ApplyTheme();
            };

            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    RestoreLastValid();
                    Keyboard.ClearFocus();
                    e.Handled = true;
                }
            };

            clearButton.Click += (_, __) =>
            {
                textBox.Clear();
                textBox.Focus();
            };

            border.MouseEnter += (_, __) =>
            {
                isHovering = true;
                ApplyTheme();
            };

            border.MouseLeave += (_, __) =>
            {
                isHovering = false;
                ApplyTheme();
            };

            SetValue(initialValue);
            ApplyTheme();

            return (border, textBox, clearButton, SetValue, ReadValue, ApplyTheme);
        }

        /// <summary>
        /// Creates a rounded, theme-aware text input for editing names inside the context menu.
        /// </summary>
        private static (Border Root, TextBox TextBox, Action ApplyTheme)
            CreateContextMenuTextInput(string text, double minWidth = 200)
        {
            var border = new Border
            {
                MinWidth = minWidth,
                Height = MenuUI.NumericInputHeight,
                CornerRadius = new CornerRadius(MenuUI.NumericInputCornerRadius),
                BorderThickness = new Thickness(1),
                SnapsToDevicePixels = true
            };

            var textBox = new TextBox
            {
                Text = text,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 0, 5, 0),
                FontSize = MenuUI.ItemFontSize,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null
            };

            border.Child = textBox;

            bool isHovering = false;

            void ApplyTheme()
            {
                ApplyContextMenuInputTheme(border, textBox, isHovering);
            }

            border.MouseEnter += (_, __) =>
            {
                isHovering = true;
                ApplyTheme();
            };

            border.MouseLeave += (_, __) =>
            {
                isHovering = false;
                ApplyTheme();
            };

            textBox.GotKeyboardFocus += (_, __) =>
            {
                ApplyTheme();
            };

            textBox.LostKeyboardFocus += (_, __) =>
            {
                ApplyTheme();
            };

            ApplyTheme();

            return (border, textBox, ApplyTheme);
        }

        /// <summary>Build a (label + slider + custom numeric input) menu row with safe two-way sync.</summary>
        private (MenuItem item, Slider slider, Border box, Action Sync) CreateFactorRow(
            string title,
            double min, double max,
            Func<double> getValue,      // Read current factor
            Action<double> setValue,    // Apply new factor
            double step = 0.05)         // Rounding step (matches slider tick)
        {
            // Minimal/no-hover template for a “host only” MenuItem
            var item = new MenuItem
            {
                StaysOpenOnClick = true,
                Focusable = false,
                Template = CreateContextMenuHostItemTemplate()
            };

            // Grid: 2 rows (title; controls) × 2 cols (slider | numeric input)
            var grid = new Grid
            {
                Margin = new Thickness(8, 4, 8, 6)
            };

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = title,
                Margin = new Thickness(0, 0, 0, 6),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetRow(label, 0);
            Grid.SetColumnSpan(label, 2);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = getValue(),
                TickFrequency = step,
                IsSnapToTickEnabled = false, // Snap manually
                Margin = new Thickness(0, 0, 8, 0),
                Width = 170
            };

            slider.SetResourceReference(FrameworkElement.StyleProperty, "ContextMenuSliderStyle");

            Grid.SetRow(slider, 1);
            Grid.SetColumn(slider, 0);

            bool updating = false;

            var numericInput = CreateNumericInput(
                initialValue: getValue(),
                minimum: min,
                maximum: max,
                step: step,
                width: MenuUI.FactorInputWidth,
                numberFormat: "0.00",
                onValueCommitted: value =>
                {
                    if (updating)
                        return;

                    updating = true;

                    slider.Value = value;
                    setValue(value);

                    updating = false;
                });

            var box = numericInput.Root;

            box.HorizontalAlignment = HorizontalAlignment.Right;
            box.Margin = new Thickness(0, 0, MenuUI.RowRightInset, 0);

            Grid.SetRow(box, 1);
            Grid.SetColumn(box, 1);

            grid.Children.Add(label);
            grid.Children.Add(slider);
            grid.Children.Add(box);

            item.Header = grid;

            // Slider → numeric input
            slider.ValueChanged += (_, args) =>
            {
                if (updating)
                    return;

                updating = true;

                double value =
                    Math.Round(args.NewValue / step) * step;

                value = Math.Clamp(value, min, max);

                slider.Value = value;
                numericInput.SetValue(value);
                setValue(value);

                updating = false;
            };

            // Sync delegate to call when the context menu opens
            void Sync()
            {
                updating = true;

                double value = Math.Clamp(getValue(), min, max);

                slider.Value = value;
                numericInput.SetValue(value);
                numericInput.ApplyTheme();

                updating = false;
            }

            return (item, slider, box, Sync);
        }

        /// <summary>Create the Window size section of the context menu.</summary>
        private (MenuItem titleItem, MenuItem editorItem, Action Sync)
        CreateWindowSizeRow(string title, string svgPath)
        {
            var titleItem = new MenuItem
            {
                Header = title,
                StaysOpenOnClick = true,
                Focusable = false,
                IsHitTestVisible = false
            };

            if (File.Exists(svgPath))
                titleItem.Icon =
                    CreateThemedSvgIcon(svgPath, MenuBrush, MenuUI.IconSize);

            var editorItem = new MenuItem
            {
                StaysOpenOnClick = true,
                Focusable = false,
                Template = CreateContextMenuHostItemTemplate()
            };

            var grid = new Grid
            {
                Margin = new Thickness(8, 2, 8 + MenuUI.RowRightInset, 6)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "GeoLabel1" });   // W: / X:
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });             // first box — grows to fill
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "GeoLabel2" });   // H: / Y:
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });             // second box — grows to fill

            var widthLabel = new TextBlock
            {
                Text = "W:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = MenuUI.ItemFontSize
            };

            Grid.SetColumn(widthLabel, 0);

            var heightLabel = new TextBlock
            {
                Text = "H:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(MenuUI.GeometryLabelGap, 0, 8, 0),
                FontSize = MenuUI.ItemFontSize
            };

            Grid.SetColumn(heightLabel, 2);

            bool updating = false;

            double ReadCurrentWidth()
            {
                double width =
                    !double.IsNaN(ActualWidth) && ActualWidth > 0
                        ? ActualWidth
                        : Width;

                return Math.Max(MinWidth, width);
            }

            double ReadCurrentHeight()
            {
                double height =
                    !double.IsNaN(ActualHeight) && ActualHeight > 0
                        ? ActualHeight
                        : Height;

                return Math.Max(MinHeight, height);
            }

            void ApplySize(double width, double height)
            {
                Width = Math.Max(MinWidth, width);
                Height = Math.Max(MinHeight, height);

                ClampWindowToVirtualScreen();
                QueueFullRelayout();
            }

            var widthInput = CreateNumericInput(
                initialValue: ReadCurrentWidth(),
                minimum: Math.Max(1, MinWidth),
                maximum: 10000,
                step: 1,
                width: null,
                numberFormat: "0",
                showStepButtons: true,
                stretchToFill: true,
                onValueCommitted: value =>
                {
                    if (updating)
                        return;

                    updating = true;
                    ApplySize(value, ReadCurrentHeight());
                    updating = false;
                });

            Grid.SetColumn(widthInput.Root, 1);

            var heightInput = CreateNumericInput(
                initialValue: ReadCurrentHeight(),
                minimum: Math.Max(1, MinHeight),
                maximum: 10000,
                step: 1,
                width: null,
                numberFormat: "0",
                showStepButtons: true,
                stretchToFill: true,
                onValueCommitted: value =>
                {
                    if (updating)
                        return;

                    updating = true;
                    ApplySize(ReadCurrentWidth(), value);
                    updating = false;
                });

            Grid.SetColumn(heightInput.Root, 3);

            widthInput.Root.ToolTip = "Window width (DIPs).";
            heightInput.Root.ToolTip = "Window height (DIPs).";

            ApplyToolTipTiming(widthInput.Root);
            ApplyToolTipTiming(heightInput.Root);

            widthLabel.ToolTip = widthInput.Root.ToolTip;
            heightLabel.ToolTip = heightInput.Root.ToolTip;

            ApplyToolTipTiming(widthLabel);
            ApplyToolTipTiming(heightLabel);

            grid.Children.Add(widthLabel);
            grid.Children.Add(widthInput.Root);
            grid.Children.Add(heightLabel);
            grid.Children.Add(heightInput.Root);

            editorItem.Header = grid;

            void Sync()
            {
                updating = true;

                widthInput.SetValue(Math.Round(ReadCurrentWidth()));
                heightInput.SetValue(Math.Round(ReadCurrentHeight()));

                widthInput.ApplyTheme();
                heightInput.ApplyTheme();

                updating = false;
            }

            Sync();

            return (titleItem, editorItem, Sync);
        }

        /// <summary>Create the Window position section of the context menu.</summary>
        private (MenuItem titleItem, MenuItem editorItem, Action Sync)
        CreateWindowBottomLeftRow(string title, string svgPath)
        {
            var titleItem = new MenuItem
            {
                Header = title,
                StaysOpenOnClick = true,
                Focusable = false,
                IsHitTestVisible = false
            };

            if (File.Exists(svgPath))
                titleItem.Icon =
                    CreateThemedSvgIcon(svgPath, MenuBrush, MenuUI.IconSize);

            var editorItem = new MenuItem
            {
                StaysOpenOnClick = true,
                Focusable = false,
                Template = CreateContextMenuHostItemTemplate(),
                ToolTip =
                    "Bottom-left corner position in screen coordinates (DIPs)."
            };

            var grid = new Grid
            {
                Margin = new Thickness(8, 2, 8 + MenuUI.RowRightInset, 6)
            };

            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "GeoLabel1" });   // W: / X:
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });             // first box — grows to fill
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "GeoLabel2" });   // H: / Y:
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });             // second box — grows to fill

            var xLabel = new TextBlock
            {
                Text = "X:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = MenuUI.ItemFontSize
            };

            Grid.SetColumn(xLabel, 0);

            var yLabel = new TextBlock
            {
                Text = "Y:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(MenuUI.GeometryLabelGap, 0, 8, 0),
                FontSize = MenuUI.ItemFontSize
            };

            Grid.SetColumn(yLabel, 2);

            bool updating = false;

            double ReadCurrentHeight()
            {
                double height =
                    !double.IsNaN(ActualHeight) && ActualHeight > 0
                        ? ActualHeight
                        : Height;

                return Math.Max(MinHeight, height);
            }

            double GetBottomLeftX() => Left;

            double GetBottomLeftY() =>
                Top + ReadCurrentHeight();

            void ApplyBottomLeft(double x, double y)
            {
                Left = x;
                Top = y - ReadCurrentHeight();

                ClampWindowToVirtualScreen();
                QueueFullRelayout();
            }

            var xInput = CreateNumericInput(
                initialValue: GetBottomLeftX(),
                minimum: -100000,
                maximum: 100000,
                step: 1,
                width: null,
                numberFormat: "0",
                showStepButtons: true,
                stretchToFill: true,
                onValueCommitted: value =>
                {
                    if (updating)
                        return;

                    updating = true;
                    ApplyBottomLeft(value, GetBottomLeftY());
                    updating = false;
                });

            Grid.SetColumn(xInput.Root, 1);

            var yInput = CreateNumericInput(
                initialValue: GetBottomLeftY(),
                minimum: -100000,
                maximum: 100000,
                step: 1,
                width: null,
                numberFormat: "0",
                showStepButtons: true,
                stretchToFill: true,
                onValueCommitted: value =>
                {
                    if (updating)
                        return;

                    updating = true;
                    ApplyBottomLeft(GetBottomLeftX(), value);
                    updating = false;
                });

            Grid.SetColumn(yInput.Root, 3);

            grid.Children.Add(xLabel);
            grid.Children.Add(xInput.Root);
            grid.Children.Add(yLabel);
            grid.Children.Add(yInput.Root);

            editorItem.Header = grid;

            void Sync()
            {
                updating = true;

                xInput.SetValue(Math.Round(GetBottomLeftX()));
                yInput.SetValue(Math.Round(GetBottomLeftY()));

                xInput.ApplyTheme();
                yInput.ApplyTheme();

                updating = false;
            }

            Sync();

            return (titleItem, editorItem, Sync);
        }


        // === Background color dialog (PixiEditor picker + eyedropper) ===

        /// <summary>
        /// Applies WPF-UI resources to one dialog only and updates its local theme.
        /// </summary>
        private static void ApplyWpfUiDialogTheme(FrameworkElement dialog)
        {
            var theme = AppTheme.IsDark
                ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                : Wpf.Ui.Appearance.ApplicationTheme.Light;

            // Keep one ThemesDictionary for the lifetime of the dialog.
            var themeDictionary = dialog.Resources.MergedDictionaries
                .OfType<ThemesDictionary>()
                .FirstOrDefault();

            if (themeDictionary is null)
            {
                themeDictionary = new ThemesDictionary
                {
                    Theme = theme
                };

                dialog.Resources.MergedDictionaries.Add(themeDictionary);
            }
            else
            {
                themeDictionary.Theme = theme;
            }

            // ControlsDictionary must only be added once.
            // Removing/re-adding it after the Window has been shown can cause
            // WPF window properties such as AllowsTransparency to be reapplied.
            if (!dialog.Resources.MergedDictionaries.OfType<ControlsDictionary>().Any())
            {
                dialog.Resources.MergedDictionaries.Add(new ControlsDictionary());
            }
        }

        /// <summary>
        /// Creates the shared WPF-UI dialog layout with a title bar and content area.
        /// </summary>
        private (Grid Root, WpfTitleBar TitleBar) CreateDialogRoot(FluentWindow dlg, UIElement content, Brush background, bool stretchContent = false)
        {
            var titleBar = new WpfTitleBar
            {
                Title = dlg.Title,
                ShowMinimize = false,
                ShowMaximize = false,
                ShowClose = true,
                Height = 40,
                Padding = new Thickness(18, 0, 0, 0),
                Background = background
            };

            var root = new Grid
            {
                Background = background
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = stretchContent ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });

            Grid.SetRow(titleBar, 0);
            Grid.SetRow(content, 1);

            root.Children.Add(titleBar);
            root.Children.Add(content);

            dlg.Content = root;

            return (root, titleBar);
        }

        /// <summary>
        /// Creates a WPF-UI FluentWindow configured as a KeyClickOverlay dialog shell.
        /// </summary>
        private FluentWindow CreateDialogWindow(string title, Brush background, double minWidth = 0, double minHeight = 0, bool centerOnScreen = false)
        {
            var dlg = new FluentWindow
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Owner = this,
                Background = background,
                WindowBackdropType = WindowBackdropType.None,

                WindowStartupLocation = centerOnScreen
                    ? WindowStartupLocation.CenterScreen
                    : WindowStartupLocation.Manual
            };

            if (minWidth > 0)
                dlg.MinWidth = minWidth;

            if (minHeight > 0)
                dlg.MinHeight = minHeight;

            ApplyWpfUiDialogTheme(dlg);

            dlg.SourceInitialized += (_, __) =>
            {
                try
                {
                    NativeMethods.TryApplyWin11RoundedCorners(dlg);
                    NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);
                }
                catch
                {
                }
            };

            BindDynamicResource(dlg, Control.ForegroundProperty, "TextFillColorPrimaryBrush", "SystemControlForegroundBaseHighBrush");

            return dlg;
        }

        /// <summary>
        /// Creates the shared icon-and-message content layout used by standard dialogs.
        /// </summary>
        private Grid CreateDialogMessageContent(string message, DialogIcon icon, Brush background)
        {
            var contentGrid = new Grid
            {
                Margin = new Thickness(20, 18, 20, 8),
                Background = background,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            if (icon != DialogIcon.None)
            {
                contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int messageColumn = 0;

            if (icon != DialogIcon.None)
            {
                FrameworkElement iconElement = BuildDialogIcon(icon);

                Grid.SetColumn(iconElement, 0);

                contentGrid.Children.Add(iconElement);

                messageColumn = 1;
            }

            var messageText = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 330,
                Margin = icon != DialogIcon.None
                    ? new Thickness(16, 2, 0, 0)
                    : new Thickness(0, 2, 0, 0),
                FontSize = 14
            };

            Grid.SetColumn(messageText, messageColumn);

            contentGrid.Children.Add(messageText);

            return contentGrid;
        }

        /// <summary>Create a themed dialog shell hosting PixiEditor’s color picker.</summary>
        private (FluentWindow dlg, StandardColorPicker picker) CreateColorDialogShell(Brush fallbackSurface)
        {
            var dlg = CreateDialogWindow("Select background color", fallbackSurface, minWidth: 300, minHeight: 520);

            // Create StandardColorPicker
            byte initA = (byte)Math.Round(Math.Clamp(_backgroundOpacity, 0, 1) * 255);

            var picker = new StandardColorPicker
            {
                Margin = new Thickness(10),
                SelectedColor = Color.FromArgb(initA, _backgroundColorRgb.R, _backgroundColorRgb.G, _backgroundColorRgb.B),
                ShowAlpha = true,
                MinWidth = 360,
                MinHeight = 440,
            };

            // Load the Pixi style that matches the current theme.
            var rd = new ResourceDictionary
            {
                Source = AppTheme.IsDark
                    ? new Uri("pack://application:,,,/KeyClickOverlay;component/Styles/DarkColorPickerStyle.xaml", UriKind.RelativeOrAbsolute)
                    : new Uri("pack://application:,,,/KeyClickOverlay;component/Styles/LightColorPickerStyle.xaml", UriKind.RelativeOrAbsolute)
            };

            _pixiColorPickerStyle =
                (Style)rd["DefaultColorPickerStyle"];

            picker.Style = _pixiColorPickerStyle;

            // React when theme changes (switching Dark/Light while dialog is open)
            void OnPickerThemeChanged(object? sender, EventArgs e)
            {
                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                var rd = new ResourceDictionary
                {
                    Source = AppTheme.IsDark
                        ? new Uri("pack://application:,,,/KeyClickOverlay;component/Styles/DarkColorPickerStyle.xaml", UriKind.RelativeOrAbsolute)
                        : new Uri("pack://application:,,,/KeyClickOverlay;component/Styles/LightColorPickerStyle.xaml", UriKind.RelativeOrAbsolute)
                };

                _pixiColorPickerStyle = (Style)rd["DefaultColorPickerStyle"];

                picker.Style = _pixiColorPickerStyle;
            }

            AppTheme.Changed += OnPickerThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnPickerThemeChanged;
            };

            return (dlg, picker);
        }

        /// <summary>Show a themed Pixi color dialog with eyedropper and live preview.</summary>
        private void ShowThemedColorDialog(
            string? title,               // dialog title (can be null to keep default)
            bool showAlpha,             // show alpha slider (true for background, false for mouse)
            Color initial,              // initial color (alpha respected when showAlpha = true)
            Action<Color> applyLive,    //  called on every ColorChanged (use for live preview)
            Action<Color> onOk,         // called on OK with the final color
            Action onCancelRevert)      // called when user cancels/closes without OK
        {
            // ---------- Dialog surface & themed shell ----------

            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249))
                {
                    Opacity = 0.96
                };

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            var (dlg, picker) = CreateColorDialogShell(fallbackSurface);
            if (!string.IsNullOrEmpty(title)) dlg.Title = title;

            picker.ShowAlpha = showAlpha;
            picker.SelectedColor = initial;

            AttachPixiComboBoxTopmostGuard(picker);

            bool didRevertOnCancel = false;

            // Live preview
            picker.ColorChanged += (_, __) =>
            {
                applyLive(picker.SelectedColor);
            };

            // ---------- Buttons ----------------------------------
            var ok = new Button
            {
                Content = "OK",
                MinWidth = 88,
                Margin = new Thickness(0, 12, 8, 12),
                IsDefault = true
            };

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                Margin = new Thickness(0, 12, 12, 12),
                IsCancel = true
            };

            // Eyedropper UX identical to your background dialog
            var eye = AttachEyedropperButton(dlg, picker, ok, cancel);

            ok.Click += (_, __) =>
            {
                onOk(picker.SelectedColor);
                dlg.DialogResult = true;
                dlg.Close();
            };

            cancel.Click += (_, __) =>
            {
                if (eye.IsActive())
                {
                    eye.EndEyedropper(true);    // Stop eyedropper, keep dialog open
                    return;
                }
                didRevertOnCancel = true;
                onCancelRevert();
                dlg.DialogResult = false;
                dlg.Close();
            };

            // ---------- Layout ----------------------------------
            var bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            bottom.Children.Add(ok);
            bottom.Children.Add(cancel);

            var outer = new Grid();

            outer.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = new GridLength(1, GridUnitType.Star)
                });

            outer.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            // Style island: isolate Pixi from implicit styles in the surrounding dialog.
            var styleIsland = new Grid();

            styleIsland.Resources.MergedDictionaries.Add(BuildPixiStyleResetDictionary());

            styleIsland.Children.Add(picker);

            Grid.SetRow(styleIsland, 0);
            outer.Children.Add(styleIsland);

            Grid.SetRow(bottom, 1);
            outer.Children.Add(bottom);

            outer.HorizontalAlignment = HorizontalAlignment.Stretch;
            outer.VerticalAlignment = VerticalAlignment.Stretch;

            picker.HorizontalAlignment = HorizontalAlignment.Stretch;
            picker.VerticalAlignment = VerticalAlignment.Stretch;

            // ---------- WPF-UI window shell ----------------------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, outer, fallbackSurface, stretchContent: true);

            // ---------- Theme updates -----------------------------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                outer.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnThemeChanged;
            };

            // Initial surface
            dlg.Background = fallbackSurface;
            dialogRoot.Background = fallbackSurface;
            titleBar.Background = fallbackSurface;
            outer.Background = fallbackSurface;

            // ---------- Window behavior & position -------------------
            dlg.Owner = this;
            dlg.ShowInTaskbar = false;
            dlg.ResizeMode = ResizeMode.NoResize;
            dlg.WindowStartupLocation = WindowStartupLocation.Manual;
            dlg.ApplyTemplate();
            PositionDialogAtCursor(dlg, outer, this);

            // Hand over "always-on-top" enforcement to the color dialog
            _topmostTarget = dlg;
            this.Topmost = false;
            dlg.Topmost = true;
            ReassertTopmost();

            bool? result = dlg.ShowDialog();

            // Hand enforcement back to the main window
            _suspendTopmostForMenu = false;
            _topmostTarget = null;
            dlg.Topmost = false;
            this.Topmost = true;
            ReassertTopmost();

            if (result != true && !didRevertOnCancel)
            {
                // ESC/close: revert (Cancel already reverted)
                onCancelRevert();
            }
        }

        /// <summary>Attach an eyedropper button to the Pixi color dialog for screen color picking.</summary>
        private (Action<bool> EndEyedropper, Func<bool> IsActive)
        AttachEyedropperButton(Window dlg, StandardColorPicker picker, Button ok, Button cancel)
        {
            // ---------- Hide Pixi preview swatches ----------
            var hidePreviewStyle = new Style(typeof(ColorPicker.ColorDisplay));
            hidePreviewStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden));
            hidePreviewStyle.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
            hidePreviewStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
            picker.Resources[typeof(ColorPicker.ColorDisplay)] = hidePreviewStyle;

            // ---------- Eyedropper state ----------
            Gma.System.MouseKeyHook.IKeyboardMouseEvents? eyedropHook = null;
            DispatcherTimer? sampleTimer = null;
            int latestX = 0, latestY = 0;
            bool hasLatestPoint = false;
            bool eyedropperActive = false;
            Color savedDropperColor = default;          // Restore color on cancel
            Action<bool>? setEyedropperToggle = null;   // Update button visuals + OK/Cancel

            // ---------- Cleanup ----------
            void EndEyedropper(bool revert)
            {
                if (!eyedropperActive) return;
                eyedropperActive = false;

                if (revert && picker.SelectedColor != savedDropperColor)
                    picker.SelectedColor = savedDropperColor;

                eyedropHook?.Dispose();
                eyedropHook = null;

                // restore UI + button visuals
                setEyedropperToggle?.Invoke(false);

                sampleTimer?.Stop();
                sampleTimer = null;
                hasLatestPoint = false;

                dlg.Activate();
            }

            // ---------- Build the button once the picker template is ready ----------
            picker.Loaded += (_, __) =>
            {
                var colorDisplay = FindDescendant<ColorPicker.ColorDisplay>(picker);
                if (colorDisplay == null) return;

                if (VisualTreeHelper.GetParent(colorDisplay) is Panel parent)
                {
                    int row = Grid.GetRow(colorDisplay);
                    int col = Grid.GetColumn(colorDisplay);
                    int rs = Grid.GetRowSpan(colorDisplay);
                    int cs = Grid.GetColumnSpan(colorDisplay);

                    // Brushes that swap with theme
                    SolidColorBrush baseBrush = Brushes.Transparent,
                        hoverBrush = Brushes.Transparent,
                        pressedBrush = Brushes.Transparent,
                        activeBrush = Brushes.Transparent;

                    // THEME-AWARE update helper
                    void ApplyEyedropperTheme(bool dark)
                    {
                        if (dark)
                        {
                            baseBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                            hoverBrush = new SolidColorBrush(Color.FromRgb(0x41, 0x41, 0x41));
                            pressedBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
                            activeBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
                        }
                        else
                        {
                            baseBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                            hoverBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7));
                            pressedBrush = new SolidColorBrush(Color.FromRgb(0x95, 0x95, 0x95));
                            activeBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7));
                        }
                    }

                    ApplyEyedropperTheme(AppTheme.IsDark);

                    // Button shell (rounded, uses its Background)
                    var eyedropperBtn = new Button
                    {
                        Width = 48,
                        Height = 48,
                        ToolTip = "Pick from screen",
                        Padding = new Thickness(0),
                        BorderThickness = new Thickness(0),
                        BorderBrush = Brushes.Transparent,
                        FocusVisualStyle = null,
                        Cursor = Cursors.Hand,
                        Background = baseBrush
                    };

                    var tpl = new ControlTemplate(typeof(Button));
                    var root = new FrameworkElementFactory(typeof(Border));
                    root.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
                    root.SetBinding(Border.BackgroundProperty,
                             new Binding(nameof(Button.Background)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                    root.SetBinding(Border.BorderBrushProperty,
                             new Binding(nameof(Button.BorderBrush)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                    root.SetBinding(Border.BorderThicknessProperty,
                             new Binding(nameof(Button.BorderThickness)) { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                    var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                    cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                    cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                    root.AppendChild(cp);
                    tpl.VisualTree = root;
                    eyedropperBtn.Template = tpl;

                    // Eyedropper icon that swaps with theme + active state
                    var svg = new SvgViewbox { Stretch = Stretch.Uniform, Width = 18, Height = 18 };

                    void LoadEyedropperIcon(bool active)
                    {
                        bool dark = AppTheme.IsDark;
                        string fileName =
                                active
                                ? (dark ? "eyedropper_pressed_dark.svg" : "eyedropper_pressed_light.svg")
                                : (dark ? "eyedropper_dark.svg" : "eyedropper_light.svg");

                        string iconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", fileName);
                        if (File.Exists(iconPath))
                        {
                            svg.Load(new Uri(iconPath));
                        }
                        else
                        {
                            string fallback = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "eyedropper.svg");
                            if (File.Exists(fallback)) svg.Load(new Uri(fallback));
                        }
                    }

                    LoadEyedropperIcon(false);
                    eyedropperBtn.Content = svg;

                    // Theme updates for the eyedropper button (and icon) while dialog is open
                    void OnDropperThemeChanged(object? sender, EventArgs e)
                    {
                        ApplyEyedropperTheme(AppTheme.IsDark);
                        LoadEyedropperIcon(eyedropperActive);
                        setEyedropperToggle?.Invoke(eyedropperActive);
                    }

                    AppTheme.Changed += OnDropperThemeChanged;

                    dlg.Closed += (_, __) =>
                    {
                        if (eyedropperActive)
                            EndEyedropper(revert: true);

                        AppTheme.Changed -= OnDropperThemeChanged;
                    };

                    // Match ColorDisplay layout, then nudge it a bit
                    const double dx = 8, dy = 86;
                    eyedropperBtn.HorizontalAlignment = colorDisplay.HorizontalAlignment;
                    eyedropperBtn.VerticalAlignment = colorDisplay.VerticalAlignment;
                    var cdM = colorDisplay.Margin;
                    eyedropperBtn.Margin = new Thickness(cdM.Left + dx, cdM.Top + dy, cdM.Right, cdM.Bottom);
                    Panel.SetZIndex(eyedropperBtn, Panel.GetZIndex(colorDisplay) + 1);

                    // Hover/press visuals when not active
                    eyedropperBtn.MouseEnter += (_, __2) => { if (!eyedropperActive) eyedropperBtn.Background = hoverBrush; };
                    eyedropperBtn.MouseLeave += (_, __2) => { if (!eyedropperActive) eyedropperBtn.Background = baseBrush; };
                    eyedropperBtn.PreviewMouseLeftButtonDown += (_, __2) => { if (!eyedropperActive) eyedropperBtn.Background = pressedBrush; };
                    eyedropperBtn.PreviewMouseLeftButtonUp += (_, __2) => { if (!eyedropperActive) eyedropperBtn.Background = hoverBrush; };

                    // Visual toggle helper (also toggles dialog buttons)
                    setEyedropperToggle = (on) =>
                    {
                        eyedropperActive = on;
                        ok.IsEnabled = !on;
                        cancel.IsCancel = !on; // Make Enter not close while picking
                        eyedropperBtn.Background = on ? activeBrush : baseBrush;

                        SolidColorBrush borderDark = new(Color.FromRgb(0x8f, 0x8f, 0x8f));
                        SolidColorBrush borderLight = new(Color.FromRgb(0x9a, 0x9a, 0x9a));
                        eyedropperBtn.BorderBrush = on
                            ? (AppTheme.IsDark
                                ? borderDark
                                : borderLight)
                            : Brushes.Transparent;
                        eyedropperBtn.BorderThickness = on ? new Thickness(1.5) : new Thickness(0);

                        LoadEyedropperIcon(on);
                    };

                    // Toggle behavior
                    eyedropperBtn.Click += (s, e) =>
                    {
                        if (eyedropperActive) { EndEyedropper(revert: true); return; } // Cancel if already on

                        // 0) Turn on + prime state
                        savedDropperColor = picker.SelectedColor;
                        setEyedropperToggle(true);
                        var startPx = WinForms.Control.MousePosition;
                        const int moveThresholdPx = 6;
                        bool samplingStarted = false;
                        bool armed = !NativeMethods.IsLeftButtonDown() && !NativeMethods.IsRightButtonDown();

                        eyedropHook = Gma.System.MouseKeyHook.Hook.GlobalEvents();

                        // 1) Hook thread: track latest screen point (no UI work here)
                        eyedropHook.MouseMove += (ms2, me2) =>
                        {
                            if (!armed && !NativeMethods.IsLeftButtonDown() && !NativeMethods.IsRightButtonDown())
                                armed = true;

                            int dx2 = me2.X - startPx.X, dy2 = me2.Y - startPx.Y;
                            if (!samplingStarted && (dx2 * dx2 + dy2 * dy2) >= (moveThresholdPx * moveThresholdPx))
                                samplingStarted = true; // Begin sampling after threshold

                            if (samplingStarted)
                            {
                                latestX = me2.X;
                                latestY = me2.Y;
                                hasLatestPoint = true;
                            }
                        };

                        // 2) UI thread: ~30 Hz sample; update picker color
                        sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                        sampleTimer.Tick += (_, __2) =>
                        {
                            if (!hasLatestPoint) return;
                            hasLatestPoint = false;

                            var a = picker.SelectedColor.A; // Preserve current alpha
                            var rgb = NativeMethods.GetColorAtScreenPixel(latestX, latestY);
                            var withA = Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
                            if (picker.SelectedColor != withA)
                                picker.SelectedColor = withA;
                        };
                        sampleTimer.Start();

                        // 3) Accept/cancel clicks while active
                        eyedropHook.MouseDownExt += (ms2, me2) =>
                        {
                            if (!armed) { me2.Handled = true; return; }

                            // Click on Cancel → cancel eyedropper (keep dialog open)
                            if (IsScreenPointOverElement(cancel, me2.X, me2.Y))
                            { EndEyedropper(revert: true); me2.Handled = true; return; }

                            // Click on the eyedropper button while ON → cancel
                            if (IsScreenPointOverElement(eyedropperBtn, me2.X, me2.Y))
                            { EndEyedropper(revert: true); me2.Handled = true; return; }

                            if (me2.Button == MouseButtons.Left)
                            {
                                if (!samplingStarted) { me2.Handled = true; return; } // Ignore immediate click
                                EndEyedropper(revert: false); // Accept
                                me2.Handled = true;
                            }
                            else if (me2.Button == MouseButtons.Right)
                            {
                                EndEyedropper(revert: true); // Cancel
                                me2.Handled = true;
                            }
                        };

                        // 4) ESC cancels
                        eyedropHook.KeyDown += (ks, ke) =>
                        {
                            if (ke.KeyCode == Keys.Escape)
                            {
                                ke.Handled = true;
                                EndEyedropper(revert: true);
                            }
                        };
                    };

                    // Place the button into the same grid cell as ColorDisplay
                    if (parent is Grid gridHost)
                    {
                        Grid.SetRow(eyedropperBtn, row);
                        Grid.SetColumn(eyedropperBtn, col);
                        Grid.SetRowSpan(eyedropperBtn, rs);
                        Grid.SetColumnSpan(eyedropperBtn, cs);
                    }

                    // Add the button to the same parent as ColorDisplay
                    parent.Children.Add(eyedropperBtn);

                    // Normalize Pixi UI chrome widths (mode combo + hex box); harmless if template differs
                    try
                    {
                        var modeCombo = FindDescendant<ComboBox>(picker);
                        if (modeCombo != null) modeCombo.MinWidth = 55;

                        var hexBox = FindDescendant<TextBox>(picker);
                        if (hexBox != null) hexBox.MinWidth = 80;
                    }
                    catch { /* template changes: ignore */ }
                }
            };

            // Return control surface: EndEyedropper(revert) + IsActive()
            return (EndEyedropper, () => eyedropperActive);
        }

        /// <summary>Show the color-picker dialog for the Mouse SVG.</summary>
        private void PickMouseColorViaDialog()
        {
            var original = _mouseColorRgb;

            void ApplyLive(Color c)
            {
                var rgb = Color.FromRgb(c.R, c.G, c.B);
                if (rgb != _mouseColorRgb)
                {
                    _mouseColorRgb = rgb;
                    // Live preview
                    SetMouseSvg(_currentMouseImage ?? "mouse_idle.svg");
                }
            }

            void Revert()
            {
                _mouseColorRgb = original;
                SetMouseSvg(_currentMouseImage ?? "mouse_idle.svg");
            }

            ShowThemedColorDialog(
                title: "Select mouse color",
                showAlpha: false,
                initial: Color.FromRgb(_mouseColorRgb.R, _mouseColorRgb.G, _mouseColorRgb.B),
                applyLive: ApplyLive,
                onOk: _ => { PrecacheAllMouseSvgsForColor(_mouseColorRgb); },
                onCancelRevert: Revert
            );
        }

        /// <summary>Show the color-picker dialog for the key font/icons (RGB only).</summary>
        private void PickFontColorViaDialog()
        {
            var original = _fontColorRgb;

            void ApplyLive(Color c)
            {
                var rgb = Color.FromRgb(c.R, c.G, c.B);
                if (rgb != _fontColorRgb)
                {
                    _fontColorRgb = rgb;
                    _keyTextBrush.Color = rgb;           // Text updates live
                    RetintAllKeyIconsForFontColor();     // Icons update live (includes preview)
                }
            }

            void Revert()
            {
                _fontColorRgb = original;
                _keyTextBrush.Color = original;
                RetintAllKeyIconsForFontColor();
            }

            // Show the sample keys while the dialog is open
            ShowSampleKeysPreview();

            ShowThemedColorDialog(
                title: "Select font color",
                showAlpha: false,
                initial: Color.FromRgb(_fontColorRgb.R, _fontColorRgb.G, _fontColorRgb.B),
                applyLive: ApplyLive,
                onOk: _ => { PrecacheAllKeySvgsForColor(_fontColorRgb); },
                onCancelRevert: Revert
            );

            // Always remove the preview row after the dialog closes
            HideSampleKeysPreview();
        }

        /// <summary>Show the color-picker dialog for the key tiles’ background color (RGB only).</summary>
        private void PickKeyFillColorViaDialog()
        {
            var original = _keyFillRgb;

            void ApplyLive(Color c)
            {
                var rgb = Color.FromRgb(c.R, c.G, c.B);
                if (rgb != _keyFillRgb)
                {
                    _keyFillRgb = rgb;
                    _keyFillBrush.Color = rgb; // Live update all existing keys (and preview) via shared brush
                }
            }

            void Revert()
            {
                _keyFillRgb = original;
                _keyFillBrush.Color = original;
            }

            ShowSampleKeysPreview();

            ShowThemedColorDialog(
                title: "Select key color",
                showAlpha: false,
                initial: Color.FromRgb(_keyFillRgb.R, _keyFillRgb.G, _keyFillRgb.B),
                applyLive: ApplyLive,
                onOk: _ => { /* nothing extra needed */ },
                onCancelRevert: Revert
            );

            HideSampleKeysPreview();
        }

        /// <summary>Show the color-picker dialog for the background pill.</summary>
        private void PickBackgroundColorViaDialog()
        {
            var originalRgb = _backgroundColorRgb;
            var originalOpacity = _backgroundOpacity;

            void ApplyLive(Color c)
            {
                _backgroundColorRgb = Color.FromRgb(c.R, c.G, c.B);
                _backgroundOpacity = Math.Clamp(c.A / 255.0, 0, 1);
                ApplyBackgroundBrushFromState();
            }

            void Revert()
            {
                _backgroundColorRgb = originalRgb;
                _backgroundOpacity = originalOpacity;
                ApplyBackgroundBrushFromState();
            }

            // Compose initial ARGB using current state
            var initial = Color.FromArgb((byte)Math.Round(_backgroundOpacity * 255.0), _backgroundColorRgb.R, _backgroundColorRgb.G, _backgroundColorRgb.B);

            ShowThemedColorDialog(
                title: "Select background color",
                showAlpha: true,
                initial: initial,
                applyLive: ApplyLive,
                onOk: _ => { /* nothing extra on OK; live state already applied */ },
                onCancelRevert: Revert
            );
        }

        /// <summary>Create a live preview row: [Shift] s e t [Space] c o l o r [ArrowLeft].</summary>
        private void ShowSampleKeysPreview()
        {
            if (_previewFontKeysActive) return;
            if (_horizontalContainer == null || _keysOutsideContainer == null) return;

            Panel target = _mouseOnlyBackground ? _keysOutsideContainer : _horizontalContainer;

            double squareSize = GetSquareSize();
            double scaleFactor = GetScaleFactor();

            void AddPreviewKey((FrameworkElement outer, ScaleTransform scale) built)
            {
                double units = MeasureUnitsOffTree(built.outer);
                var host = new Border
                {
                    Width = units * KeyReleasePopScale,
                    Height = double.NaN,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0),
                    Child = built.outer
                };
                built.outer.HorizontalAlignment = HorizontalAlignment.Center;
                built.outer.VerticalAlignment = VerticalAlignment.Center;

                _previewFontKeyHosts.Add(host);
                target.Children.Add(host);
                Panel.SetZIndex(host, 1);
            }

            (FrameworkElement outer, ScaleTransform scale) b;

            // [Shift] (label + icon)
            if (_specialKeyIconMap.TryGetValue("Shift", out var shiftIcon))
            {
                b = BuildSpecialKey("Shift", shiftIcon, squareSize, squareSize, scaleFactor, _keyTextBrush, _fontColorRgb, _keyFillBrush);
                AddPreviewKey(b);
            }

            // s e t
            foreach (var ch in new[] { "S", "E", "T" })
            {
                b = BuildNormalKey(ch, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
                AddPreviewKey(b);
            }

            // [Space] (icon only)
            b = BuildSpaceKey(squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);
            AddPreviewKey(b);

            // c o l o r
            foreach (var ch in new[] { "C", "O", "L", "O", "R" })
            {
                b = BuildNormalKey(ch, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
                AddPreviewKey(b);
            }

            // [ArrowLeft] (icon only)
            if (_specialKeyIconMap.TryGetValue("ArrowLeft", out var upIcon))
            {
                b = BuildIconOnlyKey(upIcon, squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);
                AddPreviewKey(b);
            }

            _previewFontKeysActive = true;

            if (_stripBackground != null)
                _stripBackground.Width = double.NaN;

            ApplyContentScaleToChildren();
            NormalizeInterItemSpacing();
            UpdateBackgroundWidth();
            UpdatePillVisibility();
        }

        /// <summary>Remove the preview row and restore normal sizing.</summary>
        private void HideSampleKeysPreview()
        {
            if (!_previewFontKeysActive) return;

            foreach (var host in _previewFontKeyHosts)
                (host.Parent as Panel)?.Children.Remove(host);

            _previewFontKeyHosts.Clear();
            _previewFontKeysActive = false;

            NormalizeInterItemSpacing();
            RefitContentToPill();
            UpdateBackgroundWidth();
            UpdatePillVisibility();
        }


        // === Theme/brush utilities used by menu, dialog and overlay ===

        /// <summary>Single-click grace: a little shorter than OS double-click, but not too short.</summary>
        static TimeSpan SingleClickGrace =>
            TimeSpan.FromMilliseconds(Math.Max(140,
                System.Windows.Forms.SystemInformation.DoubleClickTime - 160));

        /// <summary>Always returns a non-null brush for menu foreground (falls back to system menu text color)</summary>
        private Brush MenuBrush => (_globalContextMenu?.Foreground as Brush) ?? SystemColors.MenuTextBrush;

        /// <summary>Create a SolidColorBrush from a hex string, optionally forcing alpha.</summary>
        private static SolidColorBrush SolidBrush(string hex, byte? overrideAlpha = null)
        {
            // ColorConverter returns object?; handle null safely and fall back to Black.
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                if (overrideAlpha is byte a) c = Color.FromArgb(a, c.R, c.G, c.B);
                return new SolidColorBrush(c);
            }
            return new SolidColorBrush(Colors.Black);
        }

        /// <summary>Create a SolidColorBrush from a hex string; returned brush is mutable (not frozen).</summary>
        private static SolidColorBrush BuildWin11WindowOutline()
        {
            var brush = new SolidColorBrush(AppTheme.WindowOutlineColor);

            if (brush.CanFreeze)
                brush.Freeze();

            return brush;
        }

        private void UpdateWindowBorderChrome()
        {
            if (RoundedVisualFrame == null) return;

            if (_transparentToMouse)
            {
                // No visible stroke, but keep the SAME inner space:
                RoundedVisualFrame.BorderThickness = new Thickness(0);
                RoundedVisualFrame.Padding = new Thickness(FrameTotalInset); // 0 + 2 = 2
                RoundedVisualFrame.BorderBrush = Brushes.Transparent;
            }
            else
            {
                // Show 1px stroke and keep same inner space:
                RoundedVisualFrame.BorderThickness = new Thickness(FrameStroke);
                RoundedVisualFrame.Padding = new Thickness(FrameTotalInset - FrameStroke); // 1 + 1 = 2
                RoundedVisualFrame.BorderBrush = BuildWin11WindowOutline();
            }
        }

        private void UpdateWindowChromeTheme()
        {
            UpdateWindowBorderChrome();

            Resources["ChromeButtonForegroundBrush"] =
                new SolidColorBrush(AppTheme.ChromeButtonForegroundColor);

            Resources["ChromeButtonHoverBrush"] =
                new SolidColorBrush(AppTheme.ChromeButtonHoverColor);

            Resources["ChromeButtonPressedBrush"] =
                new SolidColorBrush(AppTheme.ChromeButtonPressedColor);
        }

        /// <summary>Try binding a property to the first resource key that exists (theme-aware).</summary>
        private static bool BindDynamicResource(DependencyObject target, DependencyProperty dp, params string[] keys)
        {
            if (target == null) return false;

            object? Probe(string key) => target switch
            {
                FrameworkElement fe => fe.TryFindResource(key),
                FrameworkContentElement fce => fce.TryFindResource(key),
                _ => Application.Current.TryFindResource(key)
            };

            foreach (var key in keys)
            {
                if (Probe(key) != null)
                {
                    switch (target)
                    {
                        case FrameworkElement fe:
                            fe.SetResourceReference(dp, key);
                            return true;
                        case FrameworkContentElement fce:
                            fce.SetResourceReference(dp, key);
                            return true;
                    }
                    break;
                }
            }
            return false;
        }

        /// <summary>Load an SVG as a Drawing and tint it to match the menu foreground.</summary>
        private static FrameworkElement CreateThemedSvgIcon(string svgPath, Brush tintBrush, double size = MenuUI.IconSize)
        {
            try
            {
                var settings = new WpfDrawingSettings
                {
                    IncludeRuntime = false,
                    TextAsGeometry = true,
                    OptimizePath = true
                };

                var reader = new FileSvgReader(settings);
                var drawing = reader.Read(svgPath);
                if (drawing == null)
                    return new System.Windows.Shapes.Rectangle { Width = size, Height = size, Fill = Brushes.Transparent };

                // Clone so we can safely retint fills/strokes
                var clone = drawing.Clone();
                TintDrawingRecursive(clone, tintBrush);

                // Wrap the tinted vector drawing as an Image source
                var di = new DrawingImage(clone);

                var img = new Image
                {
                    Width = size,
                    Height = size,
                    Source = di,
                    SnapsToDevicePixels = false, // Keep curves smooth
                    UseLayoutRounding = true,
                    Stretch = Stretch.Uniform
                };

                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(img, EdgeMode.Unspecified);

                return img;
            }

            // Returns a transparent rectangle on failure.
            catch
            {
                return new System.Windows.Shapes.Rectangle { Width = size, Height = size, Fill = Brushes.Transparent };
            }
        }

        /// <summary>Recursively tints all brushes in a Drawing tree with the given brush.</summary>
        private static void TintDrawingRecursive(Drawing drawing, Brush brush)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    foreach (var child in group.Children)
                        TintDrawingRecursive(child, brush);
                    break;

                case GeometryDrawing geo:
                    if (geo.Brush != null) geo.Brush = brush;
                    if (geo.Pen?.Brush != null) geo.Pen.Brush = brush;
                    break;

                case GlyphRunDrawing text:
                    if (text.ForegroundBrush != null) text.ForegroundBrush = brush;
                    break;
            }
        }

        /// <summary>Creates a wide shortcut-menu row with the action name on the left and the currently assigned shortcut on the right.</summary>
        private static Grid CreateShortcutMenuHeader(string label, TextBlock shortcutTextBlock)
        {
            var grid = new Grid
            {
                MinWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = MenuUI.ItemFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 24, 0)
            };

            shortcutTextBlock.FontSize = MenuUI.ItemFontSize;
            shortcutTextBlock.VerticalAlignment = VerticalAlignment.Center;
            shortcutTextBlock.HorizontalAlignment = HorizontalAlignment.Right;
            shortcutTextBlock.Opacity = 0.75;

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(shortcutTextBlock, 1);

            grid.Children.Add(labelBlock);
            grid.Children.Add(shortcutTextBlock);

            return grid;
        }

        /// <summary>
        /// Builds a resource dictionary that isolates PixiEditor controls
        /// from implicit styles applied by the surrounding dialog.
        /// </summary>
        private static ResourceDictionary BuildPixiStyleResetDictionary()
        {
            return new ResourceDictionary
            {
                // Keep this list focused on controls whose surrounding implicit styles
                // could interfere with PixiEditor's own templates.
                [typeof(TextBox)] = new Style(typeof(TextBox)),
                [typeof(ComboBox)] = new Style(typeof(ComboBox)),
                [typeof(ComboBoxItem)] = new Style(typeof(ComboBoxItem)),
                [typeof(Button)] = new Style(typeof(Button)),
                [typeof(ToggleButton)] = new Style(typeof(ToggleButton)),
                [typeof(RepeatButton)] = new Style(typeof(RepeatButton)),
                [typeof(ScrollBar)] = new Style(typeof(ScrollBar)),
                [typeof(ScrollViewer)] = new Style(typeof(ScrollViewer)),
            };
        }


        // === Positioning, measurements & visuals ===

        /// <summary>Return the size of the content host that contains the pill (parent of _stripBackground).</summary>
        private (double w, double h) GetPillHostSize()
        {
            if (_stripBackground?.Parent is FrameworkElement host)
                return (host.ActualWidth, host.ActualHeight);

            // Fallback to window size if the parent isn't measured yet
            return (ActualWidth, ActualHeight);
        }

        /// <summary>Return the *inner* height (window minus frame inset) used as the baseline for key/mouse scaling.</summary>
        private double GetHeightRef()
        {
            double insetTB = 2 * FrameTotalInset; // top+bottom

            // Prefer measured height; fall back to declared Height if layout isn’t ready
            double h = ActualHeight;
            if (double.IsNaN(h) || h <= 0) h = Height;

            // Use inner content height so the 1px outline is never overlapped at padding=0
            h -= insetTB;

            return Math.Max(1.0, h);
        }

        /// <summary>Reserve horizontal width for the mouse at the pressed baseline.</summary>
        private void ApplyMouseReserveWidth()
        {
            if (_mouseBorder == null) return;
            double pressedBase = GetScaleFactor();
            double baseWidth = pressedBase * _mouseAspectRatio;
            _mouseBorder.Width = baseWidth;
            _mouseBorder.MinWidth = baseWidth; // Never let it collapse during layout churn
        }

        /// <summary>Lock the mouse aspect ratio after the first reliable measure.</summary>
        private void LockMouseAspectIfReady()
        {
            if (_mouseAspectLocked || _mouseBorder == null || _mouseSvgDisplay == null) return;

            double h = _mouseBorder.ActualHeight;       // Pressed baseline height (pre-pop)
            double w = _mouseSvgDisplay.ActualWidth;    // SVG’s width at that baseline

            if (h > 0 && w > 0)
            {
                _mouseAspectRatio = w / h;
                _mouseAspectLocked = true;
                ApplyMouseReserveWidth();
            }
        }

        /// <summary>Apply the current content scale to mouse and key visuals.</summary>
        private void ApplyContentScaleToChildren()
        {
            if (_stripBackground == null) return;

            // ---------- Baseline height ----------
            // Pressed baseline so that POP (×1.35) reaches the inner height
            double pressedBase = GetScaleFactor(); // Uses GetStripInnerHeight()

            // ---------- Mouse ----------
            if (_mouseBorder != null && _mouseSvgDisplay != null)
            {
                // Outer container takes the pressed baseline; SVG auto-fits.
                _mouseBorder.Height = pressedBase;
                _mouseBorder.VerticalAlignment = VerticalAlignment.Center;

                _mouseSvgDisplay.Stretch = Stretch.Uniform;
                _mouseSvgDisplay.StretchDirection = StretchDirection.Both;
                _mouseSvgDisplay.Height = double.NaN;
                _mouseSvgDisplay.Width = double.NaN;

                // Reserve width once aspect is known to avoid left-shift during typing.
                if (_mouseAspectLocked) ApplyMouseReserveWidth();

                // Keep mouse at released-pop scale (matches key-up look).
                _mouseBorder.RenderTransformOrigin = new Point(0.5, 0.5);
                if (_mouseBorder.RenderTransform is not ScaleTransform mScale)
                {
                    mScale = new ScaleTransform(1.0, 1.0);
                    _mouseBorder.RenderTransform = mScale;
                }
                mScale.ScaleX = KeyReleasePopScale;   // 1.35
                mScale.ScaleY = KeyReleasePopScale;   // 1.35
            }

            // ---------- Keys ----------
            void ScaleKeysIn(Panel? panel)
            {
                if (panel == null) return;

                foreach (var child in panel.Children)
                {
                    // The key Viewbox may be the child or inside a Border host.
                    Viewbox? vb = child as Viewbox;
                    if (vb == null && child is Border b && b.Child is Viewbox vb2) vb = vb2;
                    if (vb == null) continue;

                    vb.Stretch = Stretch.Uniform;
                    vb.StretchDirection = StretchDirection.Both;

                    // All keys share the same pressed baseline height
                    vb.Height = pressedBase;
                    vb.VerticalAlignment = VerticalAlignment.Center;

                    // Wide keys keep Width = NaN (Space/Special/WideText).
                    vb.Width = double.NaN;

                    // Pop animation centers on the key.
                    if (vb.RenderTransform is not ScaleTransform kScale)
                    {
                        kScale = new ScaleTransform(1.0, 1.0);
                        vb.RenderTransform = kScale;
                        vb.RenderTransformOrigin = new Point(0.5, 0.5);
                    }
                }
            }

            ScaleKeysIn(_horizontalContainer);
            ScaleKeysIn(_keysOutsideContainer);
        }

        /// <summary>Normalize inter-item spacing after layout or scale changes.</summary>
        private void NormalizeInterItemSpacing()
        {
            if (_horizontalContainer == null) return;

            double standardGap = GetScaleFactor() * StandardGapFactor;

            // In mouse-only mode, provide a gap between the pill and first outside key
            if (_mouseOnlyBackground && _keysOutsideContainer != null)
            {
                bool pillVisible = _stripBackground != null && _stripBackground.Visibility == Visibility.Visible;
                double gapFromPill = pillVisible ? standardGap : 0.0;

                var curr = _keysOutsideContainer.Margin;
                var desired = new Thickness(gapFromPill, 0, 0, 0);
                if (Math.Abs(curr.Left - desired.Left) > 0.01 ||
                    Math.Abs(curr.Top - desired.Top) > 0.01 ||
                    Math.Abs(curr.Right - desired.Right) > 0.01 ||
                    Math.Abs(curr.Bottom - desired.Bottom) > 0.01)
                {
                    _keysOutsideContainer.Margin = desired;
                }
            }
            else if (_keysOutsideContainer != null)
            {
                if (_keysOutsideContainer.Margin.Left != 0 ||
                    _keysOutsideContainer.Margin.Top != 0 ||
                    _keysOutsideContainer.Margin.Right != 0 ||
                    _keysOutsideContainer.Margin.Bottom != 0)
                {
                    _keysOutsideContainer.Margin = new Thickness(0);
                }
            }

            // Collect keys in order from the active container
            var keys = new List<FrameworkElement>();
            if (_mouseOnlyBackground)
            {
                if (_keysOutsideContainer != null)
                    foreach (var child in _keysOutsideContainer.Children)
                        if (child is FrameworkElement fe) keys.Add(fe);
            }
            else
            {
                if (_horizontalContainer != null)
                    foreach (var child in _horizontalContainer.Children)
                        if (child is FrameworkElement fe && !ReferenceEquals(fe, _mouseBorder)) keys.Add(fe);
            }

            // Apply margins (left-only)
            bool mouseVisibleInPill =
                _mouseEnabled &&
                _mouseBorder != null &&
                _mouseBorder.Visibility == Visibility.Visible &&
                !_mouseOnlyBackground;

            for (int i = 0; i < keys.Count; i++)
            {
                var fe = keys[i];
                double desiredLeft = (i == 0 && !mouseVisibleInPill)
                    ? 0.0
                    : standardGap;

                var m = fe.Margin;
                var desired = new Thickness(desiredLeft, 0.0, 0.0, 0.0);

                if (Math.Abs(m.Left - desired.Left) > 0.01 ||
                    Math.Abs(m.Right - desired.Right) > 0.01 ||
                    Math.Abs(m.Top - desired.Top) > 0.01 ||
                    Math.Abs(m.Bottom - desired.Bottom) > 0.01)
                {
                    fe.Margin = desired;
                }
            }
        }

        /// <summary>Return true if the mouse should be shown inside the pill in this mode.</summary>
        private bool MouseVisibleInPill()
        {
            return _mouseEnabled
                   && _mouseBorder != null
                   && _mouseBorder.Visibility == Visibility.Visible
                   && !_mouseOnlyBackground;
        }

        /// <summary>Estimate the mouse width at the pressed baseline (unscaled).</summary>
        private double GetMouseWidthAtPressed()
        {
            if (!MouseVisibleInPill()) return 0.0;
            double h = GetScaleFactor();               // Pressed-baseline key height
            double aspect = _mouseAspectRatio;         // Captured by LockMouseAspectIfReady()
            if (!_mouseAspectLocked && _mouseBorder != null)
            {
                // One-time best effort if not locked yet
                _mouseBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (_mouseBorder.DesiredSize.Height > 0)
                    aspect = _mouseBorder.DesiredSize.Width / _mouseBorder.DesiredSize.Height;
            }
            return Math.Max(0, h * aspect);
        }

        /// <summary>Measure a key host off the visual tree to get its unit width.</summary>
        private static double MeasureUnitsOffTree(FrameworkElement fe)
        {
            // Measure off-tree at infinite size to get the control's desired width for the current baseline.
            fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // DesiredSize includes Margin; subtract it so we store pure content width as the unit.
            double w = Math.Max(0, fe.DesiredSize.Width);
            var m = fe.Margin;
            double contentWidth = w - Math.Max(0, m.Left) - Math.Max(0, m.Right);

            return Math.Max(0, contentWidth);
        }

        /// <summary>Sum projected content width for the given left-to-right key order.</summary>
        private double SumProjectedContentWidth(List<string> order)
        {
            double total = 0.0;
            double gap = GetScaleFactor() * StandardGapFactor;

            // Mouse at PRESSED width (no extra reserve)
            double mouseW = GetMouseWidthAtPressed();
            total += mouseW;

            // Keys at POPPED widths
            for (int i = 0; i < order.Count; i++)
            {
                string id = order[i];

                double wPressed = _keyUnitsAtPressed.TryGetValue(id, out double w) ? w : 0.0;
                double wPopped = wPressed * KeyReleasePopScale;

                // First key gets no left gap only if there is no mouse
                double left = (i == 0 && mouseW == 0) ? 0.0 : gap;

                total += left + wPopped;
            }

            return total;
        }

        /// <summary>Fit content to the pill by updating content scale and margins.</summary>
        private void RefitContentToPill()
        {
            var pill = _stripBackground;
            var container = _horizontalContainer;
            if (pill == null || container == null) return;

            // Available inner width (inside the 1px outline + 1px padding on each side)
            double available = Math.Max(1.0, ActualWidth - 2 * FrameTotalInset);

            // Predict content width at the current pressed baseline
            double baseContentW = SumProjectedContentWidth(_pillOrder);

            // Padding participates in fit (scales with the baseline)
            Thickness pillPad = pill.Padding;
            double basePadLR = pillPad.Left + pillPad.Right;

            // Solve scale: (content + padding) * scale <= available
            double denom = baseContentW + basePadLR;
            double targetScale = denom > 0 ? Math.Min(1.0, available / denom) : 1.0;

            // Hold scale steady while any key is mid "release-pop" animation to avoid jitter
            bool anyPop = _activeKeyBoxes.Any(kvp => kvp.Value.scale.ScaleX > 1.001 || kvp.Value.scale.ScaleY > 1.001);
            if (anyPop) targetScale = _contentScale;

            if (Math.Abs(targetScale - _contentScale) > 0.001)
            {
                _contentScale = targetScale;

                ApplyContentScaleToChildren();
                container.Height = double.NaN; // Let container re-measure at new baseline

                // Recompute pill padding at new square size
                double newSquare = GetSquareSize();
                double newPad = newSquare * _pillPadFactor;
                pill.Padding = new Thickness(newPad);

                NormalizeInterItemSpacing();

                // Refresh cached widths at new baseline, then recompute pill width
                SyncKeyUnitCacheFromTree();

                UpdateBackgroundWidth();

                // One more pass after render paints fresh measurements
                Dispatcher.BeginInvoke((Action)UpdateBackgroundWidth, DispatcherPriority.Render);
            }
        }

        /// <summary>Set the pill’s Width from projected content + padding for the given key order.</summary>
        private void SetPillWidthForOrder(List<string> order)
        {
            // Preview row: let WPF size to content.
            if (_previewFontKeysActive)
            {
                if (_stripBackground != null)
                    _stripBackground.Width = double.NaN;
                return;
            }

            if (_stripBackground == null) return;

            // Mouse-only mode: let WPF size to content.
            if (_mouseOnlyBackground)
            {
                _stripBackground.Width = double.NaN;
                return;
            }

            // No keys but mouse is shown: let WPF size to content.
            if (order.Count == 0 && MouseVisibleInPill())
            {
                _stripBackground.Width = double.NaN;
                return;
            }

            // Neither mouse nor keys: nothing to size.
            if (order.Count == 0 && !MouseVisibleInPill())
            {
                _stripBackground.Width = double.NaN;
                return;
            }
        }

        /// <summary>Compute and apply pill width from the current order (accounts for pop).</summary>
        private void UpdateBackgroundWidth() => SetPillWidthForOrder(_pillOrder);

        /// <summary>Queue a coalesced pill layout refresh on the dispatcher.</summary>
        private void InvalidatePillLayout()
        {
            if (_pillLayoutRefreshQueued) return;
            _pillLayoutRefreshQueued = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _pillLayoutRefreshQueued = false;

                // Horizontal fit factor, then heights, then spacing, then width
                RefitContentToPill();
                ApplyContentScaleToChildren();
                NormalizeInterItemSpacing();
                UpdateBackgroundWidth();
            }), DispatcherPriority.Render);
        }

        /// <summary>Show or hide the pill background based on current content.</summary>
        private void UpdatePillVisibility()
        {
            if (_stripBackground == null || _horizontalContainer == null || _mouseBorder == null)
                return;

            // Mouse counts only if enabled, visible, and not fully transparent.
            bool mouseVisible =
                _mouseEnabled &&
                _mouseBorder.Visibility == Visibility.Visible &&
                _mouseBorder.Opacity > 0.001;

            // Keys count only when they live inside the pill (i.e., not mouse-only).
            bool keysInsidePill = !_mouseOnlyBackground && _pillOrder.Count > 0;

            // Font preview row also lives inside the pill unless mouse-only is active.
            bool previewInsidePill = _previewFontKeysActive && !_mouseOnlyBackground;

            bool hasContentInPill = mouseVisible || keysInsidePill || previewInsidePill;

            _stripBackground.Visibility = hasContentInPill ? Visibility.Visible : Visibility.Collapsed;

            // Toggle per-frame culling whenever there are active keys, regardless of where they live
            bool shouldCull = _activeKeyBoxes.Count > 0;
            if (shouldCull && !_cullSubscribed)
            {
                CompositionTarget.Rendering += OnFrameCullKeys;
                _cullSubscribed = true;
            }
            else if (!shouldCull && _cullSubscribed)
            {
                CompositionTarget.Rendering -= OnFrameCullKeys;
                _cullSubscribed = false;
            }
        }

        /// <summary>Cull released keys whose deadlines elapsed on each render frame.</summary>
        private void OnFrameCullKeys(object? sender, EventArgs e)
        {

            if (_activeKeyBoxes.Count == 0) return; // Nothing active

            var now = DateTime.UtcNow;
            var toRemove = new List<string>(); // Collect keys to remove

            foreach (var kvp in _activeKeyBoxes)
            {
                var deadlineUtc = kvp.Value.deadlineUtc; // Read scheduled removal time
                if (deadlineUtc.HasValue && deadlineUtc.Value <= now) toRemove.Add(kvp.Key); // expired
            }

            if (toRemove.Count == 0) return; // No expirations this frame

            foreach (var key in toRemove)
            {
                if (!_activeKeyBoxes.TryGetValue(key, out var entry)) continue; // Already gone

                var (element, _, _) = entry; // Grab visual

                (element.Parent as Panel)?.Children.Remove(element); // Remove from visual tree
                _activeKeyBoxes.Remove(key); // Drop from active set

                var modelId = (element.Tag as string) == SpaceKeyTag ? key + "|SPACE" : key; // Resolve model id
                _pillOrder.Remove(modelId); // Update order model
                _keyUnitsAtPressed.Remove(modelId); // Update width model
            }

            SetPillWidthForOrder(_pillOrder); // Recompute pill width
            UpdatePillVisibility(); // Show/hide container
            InvalidatePillLayout(); // Request one coalesced layout pass
        }

        /// <summary>Immediately clear all visible key tiles and reset key state. Used by the taskbar "Clear keys" button.</summary>
        private void ClearAllKeysFromOverlay()
        {
            StopPausePulse();

            if (_activeKeyBoxes.Count == 0 && _pillOrder.Count == 0)
                return; // Nothing to clear

            // Remove all active key visuals
            foreach (var kvp in _activeKeyBoxes.ToList())
            {
                var key = kvp.Key;
                var (element, _, _) = kvp.Value;

                (element.Parent as Panel)?.Children.Remove(element);

                var modelId = (element.Tag as string) == SpaceKeyTag ? key + "|SPACE" : key;
                _pillOrder.Remove(modelId);
                _keyUnitsAtPressed.Remove(modelId);
            }

            _activeKeyBoxes.Clear();
            _downKeys.Clear();
            _pendingModUps.Clear();
            _pendingModSince.Clear();
            _pendingRegularUps.Clear();

            SetPillWidthForOrder(_pillOrder);
            UpdatePillVisibility();
            InvalidatePillLayout();
        }

        /// <summary>
        /// Display a persistent Pause tile while overlay input display is paused.
        /// The tile has no removal deadline, so the normal frame culler leaves it visible.
        /// </summary>
        private void ShowPersistentPauseKey()
        {
            if (_horizontalContainer == null || _keysOutsideContainer == null)
                return;

            // Do not create a second Pause tile.
            if (_activeKeyBoxes.ContainsKey(PauseOverlayKeyId))
                return;

            double squareSize = GetSquareSize();
            double scaleFactor = GetScaleFactor();

            // Use the same icon-only key style as media and navigation keys.
            var (pauseViewbox, pauseScale) = BuildIconOnlyKey("pause.svg", squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);

            string modelId = PauseOverlayKeyId;

            // Measure the tile at its normal pressed scale.
            double units = MeasureUnitsOffTree(pauseViewbox);

            // Register its width before adding it to the layout model.
            _keyUnitsAtPressed[modelId] = units;

            var host = new Border
            {
                // Reserve enough room for the later pop/pulse scale.
                Width = units * KeyReleasePopScale,
                Height = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = false,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Opacity = 0.0,
                Child = pauseViewbox
            };

            pauseViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            pauseViewbox.VerticalAlignment = VerticalAlignment.Center;

            // Respect the current Mouse-only background setting.
            Panel targetPanel = _mouseOnlyBackground
                ? _keysOutsideContainer
                : _horizontalContainer;

            targetPanel.Children.Add(host);
            Panel.SetZIndex(host, 1);

            // A null deadline makes this tile persistent.
            _activeKeyBoxes[PauseOverlayKeyId] =
                (host, pauseScale, deadlineUtc: null);

            // Only keys inside the pill belong in _pillOrder.
            if (!_mouseOnlyBackground)
                _pillOrder.Add(modelId);

            UpdatePillVisibility();
            InvalidatePillLayout();

            // Reveal only after the layout pass, matching your regular-key logic.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                host.Opacity = 1.0;
                StartPausePulse(pauseScale);
            }), DispatcherPriority.Render);
        }

        /// <summary>
        /// Continuously pulse the Pause indicator between the normal pressed-key size
        /// and the maximum released-key size.
        /// </summary>
        private void StartPausePulse(ScaleTransform scale)
        {
            _pauseIndicatorScale = scale;

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;

            // Smooth movement while growing.
            var growEase = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            };

            // Fast at first, then gently slows down into the pressed size.
            var shrinkEase = new QuinticEase
            {
                EasingMode = EasingMode.EaseOut
            };

            var pulseX = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever
            };

            pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));

            // Grow
            pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(
                KeyReleasePopScale,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000)))
            {
                EasingFunction = growEase
            });

            // Shrink with a soft landing
            pulseX.KeyFrames.Add(new EasingDoubleKeyFrame(
                1.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400)))
            {
                EasingFunction = shrinkEase
            });

            // Rest at the pressed size
            pulseX.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3200))));

            var pulseY = pulseX.Clone();

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseX, HandoffBehavior.SnapshotAndReplace);

            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseY, HandoffBehavior.SnapshotAndReplace);
        }

        /// <summary>
        /// Stop the Pause indicator pulse and release its animation references.
        /// </summary>
        private void StopPausePulse()
        {
            if (_pauseIndicatorScale == null)
                return;

            _pauseIndicatorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);

            _pauseIndicatorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            _pauseIndicatorScale = null;
        }

        /// <summary>
        /// Replace the existing Pause indicator with a pressed Play indicator.
        /// The existing host and model entry are reused, so the Play key appears
        /// in exactly the same position instead of being added beside Pause.
        /// </summary>
        private void ReplacePauseWithPressedPlayKey()
        {
            if (!_activeKeyBoxes.TryGetValue(PauseOverlayKeyId, out var entry))
                return;

            var (hostElement, oldScale, _) = entry;

            if (hostElement is not Border host)
                return;

            // Stop the Pause pulse before replacing its visual.
            StopPausePulse();

            oldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            oldScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            double squareSize = GetSquareSize();
            double scaleFactor = GetScaleFactor();

            var (playViewbox, playScale) = BuildIconOnlyKey("play.svg", squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);

            playViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            playViewbox.VerticalAlignment = VerticalAlignment.Center;

            // The Play icon begins in the normal held-down key state.
            playScale.ScaleX = 1.0;
            playScale.ScaleY = 1.0;

            // Replace the Pause visual inside the existing host.
            host.Child = playViewbox;
            host.Opacity = 1.0;

            // Keep the same persistent entry while the shortcut is held.
            _activeKeyBoxes[PauseOverlayKeyId] =
                (host, playScale, deadlineUtc: null);

            InvalidatePillLayout();
        }

        /// <summary>
        /// Release the pressed Play indicator using the same pop and delayed-removal
        /// behavior as an ordinary released key.
        /// </summary>
        private void ReleasePlayIndicator()
        {
            if (!_activeKeyBoxes.TryGetValue(PauseOverlayKeyId, out var entry))
                return;

            var (host, scale, _) = entry;

            var releaseAnimation = new DoubleAnimation
            {
                To = KeyReleasePopScale,
                Duration = TimeSpan.FromMilliseconds(100),
                FillBehavior = FillBehavior.HoldEnd
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, releaseAnimation, HandoffBehavior.SnapshotAndReplace);

            scale.BeginAnimation(ScaleTransform.ScaleYProperty, releaseAnimation, HandoffBehavior.SnapshotAndReplace);

            // Let the normal frame culler remove it after the standard key hang time.
            _activeKeyBoxes[PauseOverlayKeyId] =
                (host, scale, DateTime.UtcNow.AddMilliseconds(KeyHangMs));

            SetPillWidthForOrder(_pillOrder);
            UpdatePillVisibility();
            InvalidatePillLayout();
        }

        /// <summary>Immediately remove the persistent Pause tile.</summary>
        private void RemovePersistentPauseKey()
        {
            StopPausePulse();

            if (!_activeKeyBoxes.TryGetValue(PauseOverlayKeyId, out var entry))
                return;

            var (host, scale, _) = entry;

            // Stop any animation that may be added later.
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            (host.Parent as Panel)?.Children.Remove(host);

            _activeKeyBoxes.Remove(PauseOverlayKeyId);
            _pillOrder.Remove(PauseOverlayKeyId);
            _keyUnitsAtPressed.Remove(PauseOverlayKeyId);

            SetPillWidthForOrder(_pillOrder);
            UpdatePillVisibility();
            InvalidatePillLayout();
        }

        /// <summary>Pause or resume the overlay's display of mouse and keyboard input.</summary>
        private void SetOverlayPaused(bool paused)
        {
            if (paused == _overlayPaused)
                return;

            _overlayPaused = paused;

            if (paused)
            {
                _scrollTimer?.Stop();

                // Remove all existing input tiles before displaying the status tile.
                ClearAllKeysFromOverlay();

                if (_mouseEnabled)
                    SetMouseSvg("mouse_idle.svg");

                ShowPersistentPauseKey();
            }
            else
            {
                RemovePersistentPauseKey();
            }

            ShowModernInfoAuto(
                paused ? "Overlay paused" : "Overlay resumed",
                paused
                    ? "Mouse and keyboard input will no longer be displayed."
                    : "Mouse and keyboard input display has resumed.",
                milliseconds: 1500,
                icon: DialogIcon.Info);
        }

        /// <summary>Toggle paused/resumed state for overlay input display (Ctrl+Shift+F11).</summary>
        private void TogglePauseOverlay() => SetOverlayPaused(!_overlayPaused);

        /// <summary>Queue one full layout recompute on the dispatcher (coalesced to once per frame).</summary>
        private void QueueFullRelayout()
        {
            if (_relayoutQueued) return;
            _relayoutQueued = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _relayoutQueued = false;
                UpdateStripBackgroundMetrics(); // The actual heavy work
            }), DispatcherPriority.Background);
        }

        /// <summary>Window SizeChanged: only trigger full recompute when HEIGHT really changed.</summary>
        private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Height - _lastWindowHeight) < 0.5) return; // Ignore width-only changes
            _lastWindowHeight = e.NewSize.Height;
            QueueFullRelayout();
        }

        /// <summary>Call this when user geometry (padding/corner/spacing) changes.</summary>
        private void InvalidateGeometry()
        {
            QueueFullRelayout();
        }


        /// <summary>Recalculate padding, corner radius, and dependent layout metrics.</summary>
        private void UpdateStripBackgroundMetrics()
        {
            if (_stripBackground == null) return;

            // Re-run only if the scale factor (height-driven) changed OR the user changed geometry inputs (padding/radius).
            double newScale = GetScaleFactor();
            bool scaleChanged = Math.Abs(newScale - _lastScaleFactor) > 0.001;
            bool padChanged = Math.Abs(_pillPadFactor - _lastPadFactor) > 0.000001;
            bool cornerChanged = Math.Abs(_pillCornerFactor - _lastCornerFactor) > 0.000001;

            if (!(scaleChanged || padChanged || cornerChanged))
                return;

            // Update caches so subsequent calls can short-circuit correctly
            _lastScaleFactor = newScale;
            _lastPadFactor = _pillPadFactor;
            _lastCornerFactor = _pillCornerFactor;

            // Geometry from your square baseline
            double square = GetSquareSize();
            double r = square * _pillCornerFactor;
            double pad = square * _pillPadFactor;

            _stripBackground.CornerRadius = new CornerRadius(r);
            _stripBackground.Padding = new Thickness(pad);

            // When the pill's padding equals 0 make the pill height exactly the host inner height. Otherwise, let it stretch normally (Height = NaN)8
            if (pad <= 0.000001)
            {
                var (_, hostH) = GetPillHostSize();
                // Force a precise match to the host so there is zero vertical slack.
                _stripBackground.Height = Math.Max(1.0, hostH);

                if (_horizontalContainer != null)
                {
                    _horizontalContainer.Height = _stripBackground.Height; // Keep children aligned to the same height
                    _horizontalContainer.Margin = new Thickness(0);
                    _horizontalContainer.VerticalAlignment = VerticalAlignment.Stretch;
                }
            }
            else
            {
                // Stretch normally; pressed-baseline + pop math will fill the height including padding.
                _stripBackground.Height = double.NaN;

                if (_horizontalContainer != null)
                {
                    _horizontalContainer.Height = double.NaN;
                    _horizontalContainer.Margin = new Thickness(0);
                    _horizontalContainer.VerticalAlignment = VerticalAlignment.Stretch;
                }
            }

            // Recompute dependent layout
            RefitContentToPill();          // updates _contentScale for horizontal fit
            ApplyContentScaleToChildren(); // baseline for keys + constant pop for mouse
            LockMouseAspectIfReady();      // reserve mouse width once aspect is known
            NormalizeInterItemSpacing();   // margins depend on baseline
            UpdateBackgroundWidth();       // includes right-edge pop
            UpdatePillVisibility();
        }

        /// <summary>Toggle “mouse-only” mode and refresh content visibility.</summary>
        private void ApplyMouseOnlyMode(bool mouseOnly)
        {
            _mouseOnlyBackground = mouseOnly;

            if (_horizontalContainer == null || _keysOutsideContainer == null || _mouseBorder == null)
                return;

            if (mouseOnly)
            {
                // Move all keys (everything except the mouse) OUT of the pill
                var toMove = new List<UIElement>();
                foreach (var child in _horizontalContainer.Children)
                {
                    if (child is UIElement ui && !ReferenceEquals(ui, _mouseBorder))
                        toMove.Add(ui);
                }
                foreach (var ui in toMove)
                {
                    _horizontalContainer.Children.Remove(ui);
                    _keysOutsideContainer.Children.Add(ui);
                }
                _keysOutsideContainer.Visibility = Visibility.Visible;
            }
            else
            {
                // Move keys back into the pill (after the mouse)
                var toMove = new List<UIElement>();
                foreach (var child in _keysOutsideContainer.Children)
                    if (child is UIElement ui) toMove.Add(ui);

                foreach (var ui in toMove)
                {
                    _keysOutsideContainer.Children.Remove(ui);
                    _horizontalContainer.Children.Add(ui);
                }
                _keysOutsideContainer.Visibility = Visibility.Collapsed;
            }

            // Recompute margins and sizes
            NormalizeInterItemSpacing();
            RefitContentToPill();
            UpdateBackgroundWidth();
            UpdatePillVisibility();
        }

        /// <summary>Enable or disable mouse visuals and input handling.</summary>
        private void ApplyMouseEnabled(bool enable)
        {
            _mouseEnabled = enable;

            if (_mouseBorder != null && _horizontalContainer != null)
            {
                // Keep layout stable when ON; truly remove from measure when OFF.
                if (enable)
                {
                    _mouseBorder.Visibility = Visibility.Visible;
                    _mouseBorder.Opacity = 1.0;
                    _mouseBorder.Margin = new Thickness(0);

                    // Preserve existing behavior with mouse: content hugs the left.
                    _horizontalContainer.HorizontalAlignment = HorizontalAlignment.Left;

                    // Ensure idle icon shows immediately
                    SetMouseSvg("mouse_idle.svg");
                }
                else
                {
                    // Remove the mouse from layout so it stops skewing internal origin.
                    _mouseBorder.Visibility = Visibility.Collapsed;
                    _mouseBorder.Opacity = 0.0;
                    _mouseBorder.Margin = new Thickness(0);

                    // Anchor keys to the left so adding a new key doesn’t shift existing ones.
                    _horizontalContainer.HorizontalAlignment = HorizontalAlignment.Left;
                }
            }

            if (!enable)
            {
                _scrollTimer?.Stop();
                _currentMouseImage = "mouse_idle.svg";
            }

            // Recompute sizes/margins once after the change.
            NormalizeInterItemSpacing();
            RefitContentToPill();
            UpdateBackgroundWidth();
            UpdatePillVisibility();
        }

        /// <summary>Set background padding factor and refresh geometry.</summary>
        private void SetPillPaddingFactor(double factor)
        {
            _pillPadFactor = Math.Clamp(factor, PillBounds.PaddingMin, PillBounds.PaddingMax);
            InvalidateGeometry(); // queue reflow + refit + width update on the next frame
        }

        /// <summary>Set background corner radius factor and refresh geometry.</summary>
        private void SetPillCornerFactor(double factor)
        {
            // Sensible range: 0..1.20 (1.0 ≈ full pill ends at typical heights)
            _pillCornerFactor = Math.Clamp(factor, PillBounds.CornerMin, PillBounds.CornerMax);
            InvalidateGeometry();
        }

        /// <summary>Enable or disable the rounded background pill.</summary>
        private void SetBackgroundEnabled(bool enabled)
        {
            _backgroundEnabled = enabled;
            if (_stripBackground == null) return;

            _stripBackground.Background = enabled
                    ? (_stripBackgroundBrushOn ?? new SolidColorBrush(Color.FromArgb(112, 0, 0, 0)))
                    : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)); // Fully transparent
            UpdatePillVisibility();
        }

        /// <summary>Apply the live background brush from RGB and opacity state.</summary>
        private void ApplyBackgroundBrushFromState()
        {
            if (_stripBackground == null) return;

            byte a = (byte)Math.Round(Math.Clamp(_backgroundOpacity, 0, 1) * 255);
            var c = Color.FromArgb(a, _backgroundColorRgb.R, _backgroundColorRgb.G, _backgroundColorRgb.B);

            if (_stripBackgroundBrushOn is not SolidColorBrush sb || sb.IsFrozen)
            {
                sb = new SolidColorBrush(c);
                _stripBackgroundBrushOn = sb;
            }
            else
            {
                sb.Color = c; // In-place update → instant repaint
            }

            if (_backgroundEnabled && !ReferenceEquals(_stripBackground.Background, _stripBackgroundBrushOn))
                _stripBackground.Background = _stripBackgroundBrushOn;
        }


        // === Predictive width helpers (mouse + keys) ===

        /// <summary>Compute pressed-baseline scale so pop (×1.35) + vertical padding fits window height.</summary>
        private double GetScaleFactor()
        {
            // H: safe window height; K: pop scale; c: padding-per-side as baseline factor
            double H = GetHeightRef();                  // ActualHeight (or Height fallback)
            double K = KeyReleasePopScale;              // 1.35
            double c = SquarePerScale * _pillPadFactor; // padding factor per side

            // S = H / (K + 2c)
            double S = H / (K + 2.0 * c);
            return Math.Max(0.01, S);
        }

        /// <summary>Return the pressed-baseline square size (scale × SquarePerScale).</summary>
        private double GetSquareSize() => GetScaleFactor() * SquarePerScale; // SquarePerScale: preserves legacy proportion (0.390625 / 0.64 ≈ 0.6103515625)

        /// <summary>Refresh cached per-key widths at the pressed baseline from live elements.</summary>
        private void SyncKeyUnitCacheFromTree()
        {
            // Walk pill order; for each id, read the live element width (sans margins)
            for (int i = 0; i < _pillOrder.Count; i++)
            {
                string id = _pillOrder[i];

                // Map "internalKey|SPACE" → "internalKey" used in _activeKeyBoxes
                int pipe = id.IndexOf('|');
                string activeKey = pipe >= 0 ? id[..pipe] : id;

                if (_activeKeyBoxes.TryGetValue(activeKey, out var tuple))
                {
                    var fe = tuple.element;

                    double w = fe.RenderSize.Width; // RenderSize excludes margins
                    if (w <= 0)
                    {
                        // Fallback: measure off-tree if not arranged yet
                        fe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        w = fe.DesiredSize.Width - fe.Margin.Left - fe.Margin.Right;
                    }

                    // Stored cache is "pressed" width; RenderSize reflects "pop" → normalize by KeyReleasePopScale
                    _keyUnitsAtPressed[id] = Math.Max(0, w) / KeyReleasePopScale;
                }
            }
        }

        /// <summary>Show/hide the top-right chrome buttons based on mode + hover, with a small fade.</summary>
        private void UpdateChromeButtons()
        {
            if (ChromeButtons == null) return;

            bool shouldShow = !_transparentToMouse && _isMouseOverWindow;

            if (shouldShow)
            {
                if (ChromeButtons.Visibility != Visibility.Visible)
                    ChromeButtons.Visibility = Visibility.Visible;

                // Fade in
                var fadeIn = new DoubleAnimation
                {
                    From = ChromeButtons.Opacity,
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(120),
                    FillBehavior = FillBehavior.HoldEnd
                };
                ChromeButtons.BeginAnimation(OpacityProperty, fadeIn);
            }
            else
            {
                // Fade out then collapse
                var fadeOut = new DoubleAnimation
                {
                    From = ChromeButtons.Opacity,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(120),
                    FillBehavior = FillBehavior.HoldEnd
                };
                fadeOut.Completed += (_, __) =>
                {
                    if (!_transparentToMouse && _isMouseOverWindow)
                        return; // Race guard: mouse came back during fade

                    ChromeButtons.Visibility = Visibility.Collapsed;
                };
                ChromeButtons.BeginAnimation(OpacityProperty, fadeOut);
            }
        }

        /// <summary>Depth-first search for the first descendant of type <typeparamref name="T"/> starting at <paramref name="root"/>.</summary>
        private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
        {
            if (root is null) return null;

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;

                var sub = FindDescendant<T>(child);
                if (sub is not null) return sub;
            }
            return null;
        }

        /// <summary>
        /// Suspends the topmost heartbeat while PixiEditor's mode dropdown is open
        /// so its separate popup window is not disturbed by z-order reassertion.
        /// </summary>
        private void AttachPixiComboBoxTopmostGuard(StandardColorPicker picker)
        {
            void Attach()
            {
                var modeCombo = FindDescendant<ComboBox>(picker);
                if (modeCombo is null)
                    return;

                modeCombo.DropDownOpened += (_, __) =>
                {
                    _suspendTopmostForMenu = true;
                };

                modeCombo.DropDownClosed += (_, __) =>
                {
                    _suspendTopmostForMenu = false;
                    ReassertTopmost();
                };
            }

            if (picker.IsLoaded)
            {
                Attach();
            }
            else
            {
                picker.Loaded += (_, __) => Attach();
            }
        }

        /// <summary>Hit test: is a screen pixel over a given element (used by eyedropper safety)?</summary>
        private static bool IsScreenPointOverElement(FrameworkElement element, int sx, int sy)
        {
            if (!element.IsVisible) return false;
            var tl = element.PointToScreen(new Point(0, 0));
            var br = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            return sx >= tl.X && sx <= br.X && sy >= tl.Y && sy <= br.Y;
        }

        /// <summary>Sizes the dialog to its content and centers it under the mouse cursor.</summary>
        private static void PositionDialogAtCursor(Window dlg, FrameworkElement content, Visual dpiSource)
        {
            // Ensure content has a valid DesiredSize
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, content.DesiredSize.Width, content.DesiredSize.Height));
            content.UpdateLayout();

            dlg.Width = content.DesiredSize.Width;
            dlg.Height = content.DesiredSize.Height;

            // Convert screen px to DIPs based on the dpiSource visual
            var src = PresentationSource.FromVisual(dpiSource);
            var m = src?.CompositionTarget?.TransformFromDevice ?? new Matrix(1, 0, 0, 1, 0, 0);

            var mp = WinForms.Control.MousePosition;
            var dip = m.Transform(new Point(mp.X, mp.Y));

            var waPx = WinForms.Screen.FromPoint(new System.Drawing.Point(mp.X, mp.Y)).WorkingArea;
            var waTL = m.Transform(new Point(waPx.Left, waPx.Top));
            var waBR = m.Transform(new Point(waPx.Right, waPx.Bottom));

            // Create under mouse cursor
            dlg.Left = Math.Min(Math.Max(dip.X - dlg.Width * 0.5, waTL.X), waBR.X - dlg.Width);
            dlg.Top = Math.Min(Math.Max(dip.Y - dlg.Height * 0.5, waTL.Y), waBR.Y - dlg.Height);
        }

        /// <summary>Dialog icon variants for modern message boxes.</summary>
        private enum DialogIcon
        {
            None,
            Info,
            Success,
            Warning,
            Error,
            Question
        }

        /// <summary>
        /// Creates the shared icon used by KeyClickOverlay dialogs.
        /// </summary>
        private static FrameworkElement BuildDialogIcon(DialogIcon icon)
        {
            if (icon == DialogIcon.None)
            {
                return new FrameworkElement
                {
                    Width = 0,
                    Height = 0
                };
            }

            string glyph = icon switch
            {
                DialogIcon.Info => "i",
                DialogIcon.Success => "✓",
                DialogIcon.Warning => "!",
                DialogIcon.Error => "×",
                DialogIcon.Question => "?",
                _ => "i"
            };

            Brush foreground = icon switch
            {
                DialogIcon.Success => Brushes.SeaGreen,
                DialogIcon.Warning => Brushes.Goldenrod,
                DialogIcon.Error => Brushes.IndianRed,
                DialogIcon.Question => Brushes.DodgerBlue,
                _ => Brushes.DodgerBlue
            };

            return new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(2),
                BorderBrush = foreground,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = glyph,
                    Foreground = foreground,
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        /// <summary>
        /// Plays the standard Windows system sound that corresponds to the given dialog icon.
        /// </summary>
        private static void PlayDialogSound(DialogIcon kind)
        {
            uint type = kind switch
            {
                DialogIcon.Info => NativeMethods.MB_ICONASTERISK,
                DialogIcon.Success => NativeMethods.MB_ICONASTERISK,
                DialogIcon.Warning => NativeMethods.MB_ICONEXCLAMATION,
                DialogIcon.Error => NativeMethods.MB_ICONHAND,
                DialogIcon.Question => NativeMethods.MB_ICONQUESTION,
                _ => 0
            };

            if (type != 0)
                _ = NativeMethods.MessageBeep(type);
        }

        /// <summary>
        /// Shows a themed confirmation dialog using the shared WPF-UI dialog shell.
        /// </summary>
        private bool ShowModernYesNo(string title, string message, string yes = "Yes", string no = "Cancel", DialogIcon icon = DialogIcon.Question)
        {
            bool result = false;

            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface =
                CreateFallbackSurface();

            var dlg = CreateDialogWindow(title, fallbackSurface, centerOnScreen: true);

            dlg.Width = 440;
            dlg.MinWidth = 440;
            dlg.MaxWidth = 440;
            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            var outerGrid = new Grid
            {
                Background = fallbackSurface
            };

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            // Message
            var contentGrid = CreateDialogMessageContent(message, icon, fallbackSurface);

            Grid.SetRow(contentGrid, 0);
            outerGrid.Children.Add(contentGrid);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 8, 20, 16)
            };

            var noButton = new Button
            {
                Content = no,
                MinWidth = 88,
                Height = 34,
                IsCancel = true,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var yesButton = new Button
            {
                Content = yes,
                MinWidth = 88,
                Height = 34,
                IsDefault = true
            };

            noButton.Click += (_, __) =>
            {
                result = false;
                dlg.DialogResult = false;
                dlg.Close();
            };

            yesButton.Click += (_, __) =>
            {
                result = true;
                dlg.DialogResult = true;
                dlg.Close();
            };

            buttonPanel.Children.Add(noButton);
            buttonPanel.Children.Add(yesButton);

            Grid.SetRow(buttonPanel, 1);
            outerGrid.Children.Add(buttonPanel);

            var (dialogRoot, titleBar) =
                CreateDialogRoot(dlg, outerGrid, fallbackSurface);

            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface =
                    CreateFallbackSurface();

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                outerGrid.Background = surface;
                contentGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -=
                    OnThemeChanged;
            };

            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            PlayDialogSound(icon);

            dlg.ShowDialog();

            _topmostTarget = null;

            dlg.Topmost = false;
            this.Topmost = true;

            ReassertTopmost();

            return result;
        }

        /// <summary>
        /// Shows a compact WPF-UI information dialog using the shared KeyClickOverlay dialog shell.
        /// </summary>
        private void ShowModernInfo(string title, string message, string ok = "OK", DialogIcon icon = DialogIcon.Info)
        {
            // ---------- Dialog surface ----------
            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            // ---------- WPF-UI dialog window ----------
            var dlg = CreateDialogWindow(title, fallbackSurface, centerOnScreen: true);

            dlg.Width = 440;
            dlg.MinWidth = 440;
            dlg.MaxWidth = 440;

            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            // ---------- Main dialog content ----------
            var outerGrid = new Grid
            {
                Background = fallbackSurface
            };

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            // ---------- Shared icon + message content ----------
            var contentGrid = CreateDialogMessageContent(message, icon, fallbackSurface);

            Grid.SetRow(contentGrid, 0);
            outerGrid.Children.Add(contentGrid);

            // ---------- Button row ----------
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 8, 20, 16)
            };

            var okButton = new Button
            {
                Content = ok,
                MinWidth = 88,
                Height = 34,
                IsDefault = true
            };

            buttonPanel.Children.Add(okButton);

            Grid.SetRow(buttonPanel, 1);
            outerGrid.Children.Add(buttonPanel);

            // ---------- Shared WPF-UI shell ----------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, outerGrid, fallbackSurface);

            // ---------- Theme switching ----------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                outerGrid.Background = surface;
                contentGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnThemeChanged;
            };

            // ---------- Button ----------
            okButton.Click += (_, __) =>
            {
                dlg.DialogResult = true;
                dlg.Close();
            };

            // ---------- Topmost handling ----------
            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            PlayDialogSound(icon);

            dlg.ShowDialog();

            // ---------- Restore overlay ----------
            _topmostTarget = null;

            dlg.Topmost = false;
            this.Topmost = true;

            ReassertTopmost();
        }

        /// <summary>
        /// Shows a themed acknowledgement dialog using the shared WPF-UI dialog shell.
        /// Returns true only when the acknowledgement button is clicked.
        /// Closing the window or pressing Escape returns false.
        /// </summary>
        private bool ShowModernAcknowledgement(string title, string message, string acknowledgeText = "OK", DialogIcon icon = DialogIcon.Info)
        {
            bool acknowledged = false;

            // ---------- Dialog surface ----------
            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            // ---------- WPF-UI dialog window ----------
            var dlg = CreateDialogWindow(title, fallbackSurface, centerOnScreen: true);

            dlg.Width = 440;
            dlg.MinWidth = 440;
            dlg.MaxWidth = 440;

            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            // ---------- Main dialog content ----------
            var outerGrid = new Grid
            {
                Background = fallbackSurface
            };

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            outerGrid.RowDefinitions.Add(
                new RowDefinition
                {
                    Height = GridLength.Auto
                });

            // ---------- Shared icon + message content ----------
            var contentGrid = CreateDialogMessageContent(message, icon, fallbackSurface);

            Grid.SetRow(contentGrid, 0);
            outerGrid.Children.Add(contentGrid);

            // ---------- Button row ----------
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 8, 20, 16)
            };

            var acknowledgeButton = new Button
            {
                Content = acknowledgeText,
                MinWidth = 88,
                Height = 34,
                IsDefault = true
            };

            buttonPanel.Children.Add(acknowledgeButton);

            Grid.SetRow(buttonPanel, 1);
            outerGrid.Children.Add(buttonPanel);

            // ---------- Shared WPF-UI shell ----------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, outerGrid, fallbackSurface);

            // ---------- Theme switching ----------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                outerGrid.Background = surface;
                contentGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnThemeChanged;
            };

            // ---------- Acknowledge ----------
            acknowledgeButton.Click += (_, __) =>
            {
                acknowledged = true;
                dlg.DialogResult = true;
                dlg.Close();
            };

            // Escape means "not acknowledged".
            dlg.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    dlg.Close();
                }
            };

            // ---------- Topmost handling ----------
            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            PlayDialogSound(icon);

            dlg.ShowDialog();

            // ---------- Restore overlay ----------
            _topmostTarget = null;

            dlg.Topmost = false;
            this.Topmost = true;

            ReassertTopmost();

            return acknowledged;
        }


        /// <summary>
        /// Shows a themed auto-closing information dialog using the shared WPF-UI dialog shell.
        /// </summary>
        private void ShowModernInfoAuto(string title, string message, int milliseconds = 2500, DialogIcon icon = DialogIcon.Info)
        {
            // ---------- Dialog surface ----------
            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            // ---------- WPF-UI dialog window ----------
            var dlg = CreateDialogWindow(title, fallbackSurface, centerOnScreen: true);

            dlg.Width = 440;
            dlg.MinWidth = 440;
            dlg.MaxWidth = 440;

            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            // ---------- Shared icon + message content ----------
            var contentGrid = CreateDialogMessageContent(message, icon, fallbackSurface);

            contentGrid.Margin = new Thickness(20, 18, 20, 28);

            // ---------- Shared WPF-UI shell ----------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, contentGrid, fallbackSurface);

            // ---------- Theme switching ----------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                contentGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            // ---------- Auto close ----------
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds)
            };

            timer.Tick += (_, __) =>
            {
                timer.Stop();

                if (dlg.IsVisible)
                    dlg.Close();
            };

            dlg.Closed += (_, __) =>
            {
                timer.Stop();
                AppTheme.Changed -= OnThemeChanged;

                if (ReferenceEquals(_topmostTarget, dlg))
                    _topmostTarget = null;

                dlg.Topmost = false;
                this.Topmost = true;

                ReassertTopmost();
            };

            // ---------- Topmost handling ----------
            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            PlayDialogSound(icon);

            // Non-modal: show and start countdown.
            dlg.Show();

            timer.Start();
        }

        /// <summary>Show a one-time tip about “Transparent-mode”.</summary>
        private bool ShowTransparentInfoDialog()
        {
            if (_prefs.HideTransparentInfo) return true;

            // ---------- Dialog surface ----------
            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            // ---------- WPF-UI dialog window ----------
            var dlg = CreateDialogWindow("Transparent-mode", fallbackSurface, centerOnScreen: true);

            dlg.Width = 500;
            dlg.MinWidth = 500;
            dlg.MaxWidth = 500;

            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            // ---------- Main content ----------
            var outerGrid = new Grid
            {
                Background = fallbackSurface
            };

            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var contentGrid = new Grid
            {
                Margin = new Thickness(20, 18, 20, 8)
            };

            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = BuildDialogIcon(DialogIcon.Info);
            Grid.SetColumn(icon, 0);
            contentGrid.Children.Add(icon);

            var messageGrid = new Grid
            {
                Margin = new Thickness(16, 2, 0, 0)
            };

            messageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            messageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var body = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 388,
                FontSize = 14
            };

            body.Inlines.Add(new Run("When enabled, the window ignores mouse input.\n"));
            body.Inlines.Add(new Run("To turn it off, press "));
            body.Inlines.Add(new Run(GetTransparentHotkeyLabel()) { FontWeight = FontWeights.SemiBold });
            body.Inlines.Add(new Run(" or click the "));
            body.Inlines.Add(new Run("“Transparent-mode”") { FontStyle = FontStyles.Italic });
            body.Inlines.Add(new Run(" button on the app’s taskbar icon."));

            var dontShow = new CheckBox
            {
                Content = "Don't show this again",
                Margin = new Thickness(0, 14, 0, 0)
            };

            Grid.SetRow(body, 0);
            Grid.SetRow(dontShow, 1);

            messageGrid.Children.Add(body);
            messageGrid.Children.Add(dontShow);

            Grid.SetColumn(messageGrid, 1);
            contentGrid.Children.Add(messageGrid);

            Grid.SetRow(contentGrid, 0);
            outerGrid.Children.Add(contentGrid);

            // ---------- Button row ----------
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(20, 8, 20, 16)
            };

            var ok = new Button
            {
                Content = "OK",
                MinWidth = 88,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };

            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 88,
                Height = 34,
                IsCancel = true
            };

            buttonPanel.Children.Add(ok);
            buttonPanel.Children.Add(cancel);

            Grid.SetRow(buttonPanel, 1);
            outerGrid.Children.Add(buttonPanel);

            // ---------- Shared WPF-UI shell ----------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, outerGrid, fallbackSurface);

            // ---------- Theme switching ----------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                outerGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnThemeChanged;
            };

            // ---------- Result ----------
            bool result = false;

            ok.Click += (_, __) =>
            {
                result = true;
                dlg.Close();
            };

            cancel.Click += (_, __) =>
            {
                result = false;
                dlg.Close();
            };

            dlg.Loaded += (_, __) => ok.Focus();

            // ---------- Topmost handling ----------
            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            dlg.ShowDialog();

            // ---------- Restore overlay ----------
            _topmostTarget = null;

            dlg.Topmost = false;
            this.Topmost = true;

            ReassertTopmost();

            if (dontShow.IsChecked == true)
            {
                _prefs.HideTransparentInfo = true;
                SavePrefs();
            }

            return result;
        }


        // === Global input hooks & state guards ===

        /// <summary>Open the shortcut picker for the Transparent-mode shortcut.</summary>
        private void ChangeTransparentHotkeyViaDialog()
        {
            ChangeShortcutViaDialog(
                "Transparent-mode shortcut",
                GetTransparentHotkeyLabel(),
                (key, mods) =>
                {
                    _prefs.TransparentHotkeyKey = key;
                    _prefs.TransparentHotkeyModifiers = mods;
                },
                RefreshTaskbarHotkeyUiLabels);
        }

        /// <summary>Open the shortcut picker for the visible preset-switch shortcut.</summary>
        private void ChangePresetSwitchHotkeyViaDialog()
        {
            ChangeShortcutViaDialog(
                "Preset switch shortcut",
                GetPresetSwitchHotkeyLabel(),
                (key, mods) =>
                {
                    _prefs.PresetSwitchHotkeyKey = key;
                    _prefs.PresetSwitchHotkeyModifiers = mods;
                });
        }

        /// <summary>Open the shortcut picker for the hidden preset-switch enable/disable shortcut.</summary>
        private void ChangePresetSwitchToggleHotkeyViaDialog()
        {
            ChangeShortcutViaDialog(
                "Preset-switch toggle shortcut",
                GetPresetSwitchToggleHotkeyLabel(),
                (key, mods) =>
                {
                    _prefs.PresetSwitchToggleHotkeyKey = key;
                    _prefs.PresetSwitchToggleHotkeyModifiers = mods;
                });
        }

        /// <summary>Open the shortcut picker for the hidden clear-overlay shortcut.</summary>
        private void ChangeClearOverlayHotkeyViaDialog()
        {
            ChangeShortcutViaDialog(
                "Clear KeyClickOverlay shortcut",
                GetClearOverlayHotkeyLabel(),
                (key, mods) =>
                {
                    _prefs.ClearOverlayHotkeyKey = key;
                    _prefs.ClearOverlayHotkeyModifiers = mods;
                },
                RefreshTaskbarHotkeyUiLabels);
        }

        /// <summary>Open the shortcut picker for the pause/resume overlay shortcut.</summary>
        private void ChangePauseOverlayHotkeyViaDialog()
        {
            ChangeShortcutViaDialog(
                "Pause KeyClickOverlay shortcut",
                GetPauseOverlayHotkeyLabel(),
                (key, mods) =>
                {
                    _prefs.PauseOverlayHotkeyKey = key;
                    _prefs.PauseOverlayHotkeyModifiers = mods;
                });
        }

        /// <summary>
        /// Generic shortcut picker dialog used by all configurable shortcuts.
        /// Requires at least one modifier key to reduce accidental global shortcut conflicts.
        /// </summary>
        private void ChangeShortcutViaDialog(string titleText, string currentShortcutLabel, Action<Keys, ModifierKeys> applyShortcut, Action? afterSave = null)
        {
            // ---------- Dialog surface ----------
            SolidColorBrush CreateFallbackSurface()
            {
                var brush = new SolidColorBrush(AppTheme.IsDark ? Color.FromRgb(43, 43, 43) : Color.FromRgb(249, 249, 249));

                if (brush.CanFreeze)
                    brush.Freeze();

                return brush;
            }

            var fallbackSurface = CreateFallbackSurface();

            // ---------- WPF-UI dialog window ----------
            var dlg = CreateDialogWindow(titleText, fallbackSurface, centerOnScreen: true);

            dlg.Width = 440;
            dlg.MinWidth = 440;
            dlg.MaxWidth = 440;

            dlg.MinHeight = 0;
            dlg.SizeToContent = SizeToContent.Height;

            // ---------- Content ----------
            var contentGrid = new Grid
            {
                Background = fallbackSurface,
                Margin = new Thickness(20, 18, 20, 28)
            };

            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = BuildDialogIcon(DialogIcon.Info);
            Grid.SetColumn(icon, 0);
            contentGrid.Children.Add(icon);

            var messageGrid = new Grid
            {
                Margin = new Thickness(16, 2, 0, 0)
            };

            messageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            messageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var body = new TextBlock
            {
                Text = "Press your new shortcut (e.g. Ctrl+Alt+R or Shift+S).\n" +
                       "Use at least one of Ctrl, Shift or Alt.\n" +
                       "Press Esc to cancel.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340
            };

            var current = new TextBlock
            {
                Text = "Current shortcut: " + currentShortcutLabel,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Grid.SetRow(body, 0);
            Grid.SetRow(current, 1);

            messageGrid.Children.Add(body);
            messageGrid.Children.Add(current);

            Grid.SetColumn(messageGrid, 1);
            contentGrid.Children.Add(messageGrid);

            // ---------- Shared WPF-UI shell ----------
            var (dialogRoot, titleBar) = CreateDialogRoot(dlg, contentGrid, fallbackSurface);

            // ---------- Theme switching ----------
            void OnThemeChanged(object? sender, EventArgs e)
            {
                var surface = CreateFallbackSurface();

                ApplyWpfUiDialogTheme(dlg);

                NativeMethods.TryApplyImmersiveDarkTitleBar(dlg, AppTheme.IsDark);

                dlg.Background = surface;
                dialogRoot.Background = surface;
                titleBar.Background = surface;
                contentGrid.Background = surface;
            }

            AppTheme.Changed += OnThemeChanged;

            dlg.Closed += (_, __) =>
            {
                AppTheme.Changed -= OnThemeChanged;
            };

            // ---------- Shortcut capture ----------
            Keys pickedKey = Keys.None;
            ModifierKeys pickedMods = ModifierKeys.None;

            dlg.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    dlg.Close();
                    return;
                }

                Key key = e.Key == Key.System ? e.SystemKey : e.Key;

                if (key is Key.LeftCtrl or Key.RightCtrl or
                    Key.LeftShift or Key.RightShift or
                    Key.LeftAlt or Key.RightAlt or
                    Key.LWin or Key.RWin)
                {
                    e.Handled = true;
                    return;
                }

                ModifierKeys mods =
                    Keyboard.Modifiers &
                    (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt);

                if (mods == ModifierKeys.None)
                {
                    e.Handled = true;
                    return;
                }

                pickedKey = (Keys)KeyInterop.VirtualKeyFromKey(key);
                pickedMods = mods;

                e.Handled = true;
                dlg.DialogResult = true;
                dlg.Close();
            };

            // ---------- Topmost handling ----------
            _topmostTarget = dlg;

            this.Topmost = false;
            dlg.Topmost = true;

            ReassertTopmost();

            dlg.ShowDialog();

            // ---------- Restore overlay ----------
            _topmostTarget = null;

            dlg.Topmost = false;
            this.Topmost = true;

            ReassertTopmost();

            // ---------- Save shortcut ----------
            if (pickedKey == Keys.None || pickedMods == ModifierKeys.None)
                return;

            applyShortcut(pickedKey, pickedMods);
            SavePrefs();
            afterSave?.Invoke();
        }

        /// <summary>Connect global mouse/keyboard hooks and timers that drive the overlay.</summary>
        private void SetupHooks()
        {
            globalHook = Hook.GlobalEvents();
            globalHook.MouseDown += GlobalHook_MouseDown;
            globalHook.MouseUp += GlobalHook_MouseUp;
            globalHook.MouseWheel += GlobalHook_MouseWheel;
            globalHook.KeyDown += GlobalHook_KeyDown;
            globalHook.KeyUp += GlobalHook_KeyUp;

            // Unified hotkeys handler with Transparent-mode allowlist
            globalHook.KeyDown += (_, e) =>
            {
                var mods = GetHeldModifiersFromState();

                // Preset switch shortcut
                if (_prefs.PresetToggleHotkeyEnabled &&
                    mods == _prefs.PresetSwitchHotkeyModifiers &&
                    e.KeyCode == _prefs.PresetSwitchHotkeyKey)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        TogglePreviousPreset();
                    }), System.Windows.Threading.DispatcherPriority.Background);

                    return;
                }

                // Toggle the preset-switch shortcut on/off.
                if (mods == _prefs.PresetSwitchToggleHotkeyModifiers &&
                    e.KeyCode == _prefs.PresetSwitchToggleHotkeyKey)
                {
                    Dispatcher.Invoke(TogglePresetToggleHotkeyEnabled);
                    return;
                }

                // Transparent-mode hotkey is handled directly inside GlobalHook_KeyDown.
                // Other normal-mode shortcuts should not run while transparent mode is active.
                if (_transparentToMouse)
                    return;

                // Normal mode shortcuts below
                if (mods.HasFlag(ModifierKeys.Control) && e.KeyCode == Keys.S) // Ctrl+S = overwrite active preset
                {
                    Dispatcher.Invoke(SaveCurrentStateToActivePreset);
                    return;
                }

                // (If more app shortcuts need to be added later, put them here.
                // They'll be ignored automatically while transparent-to-mouse is ON.)
            };
        }

        /// <summary>Show mouse-down state/icon and update Numpad/Shift guard if needed.</summary>
        private void GlobalHook_MouseDown(object? _, System.Windows.Forms.MouseEventArgs e)
        {
            if (!_mouseEnabled || _overlayPaused)
            {
                return;
            }

            string image = e.Button switch
            {
                System.Windows.Forms.MouseButtons.Left => "mouse_leftclick.svg",
                System.Windows.Forms.MouseButtons.Middle => "mouse_middleclick.svg",
                System.Windows.Forms.MouseButtons.Right => "mouse_rightclick.svg",
                _ => "mouse_idle.svg"
            };

            if (Dispatcher.CheckAccess())
                SetMouseSvg(image);
            else
                Dispatcher.Invoke(() => SetMouseSvg(image), System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>Show mouse-up state/icon; clear guards when appropriate.</summary>
        private void GlobalHook_MouseUp(object? _, System.Windows.Forms.MouseEventArgs e)
        {
            if (!_mouseEnabled) return;

            // Handle RMB globally (for mapped buttons like tablet/keypad)
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    OpenGlobalContextMenuFromCurrentPointer();
                }, DispatcherPriority.Input);
            }

            // While paused, keep the right-click menu working but don't touch the mouse visual.
            if (_overlayPaused) return;

            SetMouseSvg("mouse_idle.svg");
        }

        /// <summary>Show scroll state/icon briefly, then revert to idle.</summary>
        private void GlobalHook_MouseWheel(object? _, System.Windows.Forms.MouseEventArgs e)
        {
            if (!_mouseEnabled || _overlayPaused)
            {
                return;
            }

            SetMouseSvg(e.Delta > 0 ? "mouse_scrollup.svg" : "mouse_scrolldown.svg");
            _scrollTimer?.Stop();
            _scrollTimer?.Start();
        }

        /// <summary>Track physical key-down, build a key tile once, and prep sizing/guards (Shift/Numpad).</summary>
        private void GlobalHook_KeyDown(object? _, System.Windows.Forms.KeyEventArgs e)
        {
            // Transparent hotkey should never create a visible key tile.
            if (MatchesTransparentHotkey(e))
            {
                Dispatcher.Invoke(() =>
                {
                    ClearAllKeysFromOverlay();
                    SetTransparentMode(!_transparentToMouse, withPrompt: true);
                });
                return;
            }

            // Clear all currently displayed overlay keys before key drawing.
            if (GetHeldModifiersFromState() == _prefs.ClearOverlayHotkeyModifiers &&
                e.KeyCode == _prefs.ClearOverlayHotkeyKey)
            {
                Dispatcher.Invoke(() =>
                {
                    ClearAllKeysFromOverlay();
                });
                return;
            }

            // Pause/resume the overlay's display of mouse and keyboard input.
            // Exact modifier matching prevents shortcuts with extra modifiers from triggering it.
            if (GetHeldModifiersFromState() == _prefs.PauseOverlayHotkeyModifiers &&
                e.KeyCode == _prefs.PauseOverlayHotkeyKey)
            {
                // Ignore keyboard auto-repeat while the pause shortcut remains held.
                if (_pauseShortcutHeld)
                    return;

                _pauseShortcutHeld = true;

                Dispatcher.Invoke(() =>
                {
                    if (_overlayPaused)
                    {
                        // Resume immediately and replace the pulsing Pause icon
                        // with a Play icon in its pressed state.
                        _overlayPaused = false;
                        ReplacePauseWithPressedPlayKey();

                        ShowModernInfoAuto("Overlay resumed", "Mouse and keyboard input display has resumed.", milliseconds: 1500, icon: DialogIcon.Info);
                    }
                    else
                    {
                        SetOverlayPaused(true);
                    }
                });

                return;
            }

            // Track physical key-down using the raw key code; ignore auto-repeat while held
            if (!_downKeys.Add(e.KeyCode))
                return;

            // While paused, keep tracking physical key state (for modifier-based hotkeys)
            // but don't build or show any key tiles.
            if (_overlayPaused)
                return;

            // Modifier went down → cancel any pending release for *that* modifier
            if (IsModifierKey(e.KeyCode))
            {
                string modId = NormalizeModifierId(e.KeyCode); // "ShiftKey" / "CtrlKey" / "AltKey"
                _pendingModUps.Remove(modId);
                _pendingModSince.Remove(modId);
            }

            // NumPad active → extend guard and cancel pending releases for any modifiers currently held
            if (IsNumPadKey(e.KeyCode))
            {
                // If user starts a combo (NumPad while a modifier is pressed), keep those modifiers latched
                foreach (var heldMod in _downKeys.Where(IsModifierKey).ToList())
                {
                    string modId = NormalizeModifierId(heldMod);
                    _pendingModUps.Remove(modId);
                    _pendingModSince.Remove(modId);
                }

                ExtendNumpadGuard(NumpadGuardMs);
                StartWatchdogIfNeeded();
            }

            // Collapse modifiers to a single id so L/R variants don't fight each other
            string internalKey = IsModifierKey(e.KeyCode) ? NormalizeModifierId(e.KeyCode) : e.KeyCode.ToString();

            // If this modifier had a pending-up, cancel it (we're pressing it again)
            if (IsModifierKey(e.KeyCode))
            {
                _pendingModUps.Remove(internalKey);
                _pendingModSince.Remove(internalKey);
            }

            // Use normalized/friendly name for display and icon lookup
            string displayKey = NormalizeKeyName(e.KeyCode);

            // If the same key is already showing, remove it now (retrigger support)
            if (_activeKeyBoxes.TryGetValue(internalKey, out var existing))
            {
                // Modifiers held: keep existing tile (prevents flicker and preserves KeyUp pop)
                if (IsModifierKey(e.KeyCode) && _downKeys.Contains(e.KeyCode))
                    return;

                var (existingHost, _, _) = existing;

                // Remove current visual before rebuilding
                (existingHost.Parent as Panel)?.Children.Remove(existingHost);
                _activeKeyBoxes.Remove(internalKey);

                // Remove from model + stored widths
                string existingModelId = (existingHost.Tag as string) == SpaceKeyTag
                    ? internalKey + "|SPACE"
                    : internalKey;

                _pillOrder.Remove(existingModelId);
                _keyUnitsAtPressed.Remove(existingModelId);
                // No early return — build a fresh tile below
            }

            // Prune released keys that already have a removal deadline scheduled
            var toRemove = _activeKeyBoxes
                .Where(kvp => kvp.Value.deadlineUtc.HasValue)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var oldKey in toRemove)
            {
                if (!_activeKeyBoxes.TryGetValue(oldKey, out var old)) continue;
                var (oldViewbox, _, _) = old; // 3rd item is now deadlineUtc, not a timer

                (oldViewbox.Parent as Panel)?.Children.Remove(oldViewbox);
                _activeKeyBoxes.Remove(oldKey);

                // Remove from predictive model too
                string oldModelId = (oldViewbox.Tag as string) == SpaceKeyTag ? oldKey + "|SPACE" : oldKey;
                _pillOrder.Remove(oldModelId);
                _keyUnitsAtPressed.Remove(oldModelId);
            }

            // Re-size pill after pruning (model-driven)
            SetPillWidthForOrder(_pillOrder);

            // Sizing inputs (baseline & scale)
            double squareSize = GetSquareSize();
            double specialKeyHeight = squareSize;
            double scaleFactor = GetScaleFactor(); // keep this for your builders

            // Build the key UI
            FrameworkElement outerViewbox;
            ScaleTransform scale;

            if (displayKey.Equals("Space", StringComparison.OrdinalIgnoreCase)) // or Key.Space from raw input
            {
                // Space bar
                (outerViewbox, scale) = BuildSpaceKey(squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);
            }
            else if (_iconOnlyKeys.Contains(displayKey) && _specialKeyIconMap.TryGetValue(displayKey, out string? iconFile) && iconFile is not null)
            {
                // Icon-only
                (outerViewbox, scale) = BuildIconOnlyKey(iconFile, squareSize, scaleFactor, _fontColorRgb, _keyFillBrush);
            }
            else if (IsNumPadKey(e.KeyCode))
            {
                // NumPad: digits use the centered NumPad layout; operators keep baseline layout
                if (IsNumPadOperatorKey(e.KeyCode))
                {
                    (outerViewbox, scale) = BuildNumPadOperatorKey(displayKey, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
                }
                else
                {
                    (outerViewbox, scale) = BuildNumPadKey(displayKey, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
                }
            }
            else if (_specialKeyIconMap.TryGetValue(displayKey.ToLowerInvariant(), out string? specialIconFile) && specialIconFile is not null)
            {
                // Special key
                (outerViewbox, scale) = BuildSpecialKey(displayKey, specialIconFile, squareSize, specialKeyHeight, scaleFactor, _keyTextBrush, _fontColorRgb, _keyFillBrush);
            }
            else
            {
                if (ShouldUseWideText(displayKey))
                    // Wide text key
                    (outerViewbox, scale) = BuildWideTextKey(displayKey, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
                else
                    // Normal key
                    (outerViewbox, scale) = BuildNormalKey(displayKey, squareSize, scaleFactor, _keyTextBrush, _keyFillBrush);
            }

            // Predictive pre-size before inserting into the tree
            bool isSpaceKey = displayKey.Equals("Space", StringComparison.OrdinalIgnoreCase);
            string modelId = isSpaceKey ? internalKey + "|SPACE" : internalKey;

            // 1) Measure at pressed baseline (host width + predictive model)
            double units = MeasureUnitsOffTree(outerViewbox);

            // 2) If pill is active, update predictive width (projected order)
            if (!_mouseOnlyBackground && _stripBackground != null)
            {
                var projected = new List<string>(_pillOrder) { modelId };
                _keyUnitsAtPressed[modelId] = units;

                // Pre-size including the new key (use the projected list)
                SetPillWidthForOrder(projected);
            }

            // 3) Host reserves popped width to avoid reflow during scale
            var host = new Border
            {
                Width = units * KeyReleasePopScale,   // Reserve final popped width up front
                Height = double.NaN,                  // Height driven by your scaling routine
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                ClipToBounds = false,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Child = outerViewbox,                 // The actual key Viewbox lives inside
            };

            // Keep the key centered within its reserved box so pop grows symmetrically
            outerViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            outerViewbox.VerticalAlignment = VerticalAlignment.Center;
            host.Tag = outerViewbox.Tag;    // Preserve tag for spacing

            double standardGap = GetScaleFactor() * StandardGapFactor;
            bool mouseVisible = MouseVisibleInPill();                     // Live mouse-in-pill state
            bool firstKey = _pillOrder.Count == 0;                        // Keys already in pill

            double left = _mouseOnlyBackground
                ? 0.0                                                     // Gap owned by outside container
                : ((firstKey && !mouseVisible) ? 0.0 : standardGap);      // First key touches when mouse hidden

            host.Margin = new Thickness(left, 0, 0, 0);
            host.Opacity = 0.0; // hide until final scale/spacing are applied

            // 4) Add the HOST (not the raw Viewbox) to the correct panel
            Panel? targetPanel = _mouseOnlyBackground ? _keysOutsideContainer : _horizontalContainer;
            targetPanel?.Children.Add(host);

            Panel.SetZIndex(host, 1);   // keep keys above mouse

            // Commit runtime/state (store HOST)
            _activeKeyBoxes[internalKey] = (host, scale, null);

            if (!_mouseOnlyBackground)
                _pillOrder.Add(modelId);

            // Ensure pill is visible now that a key is inside
            UpdatePillVisibility();

            // Coalesce layout to next render tick
            InvalidatePillLayout();

            // Reveal the key after final scale/spacing were applied
            Dispatcher.BeginInvoke(new Action(() =>
            {
                host.Opacity = 1.0;
            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>Release key tiles and manage delayed Shift-up debounce.</summary>
        private void GlobalHook_KeyUp(object? _, System.Windows.Forms.KeyEventArgs e)
        {
            // Finish the Pause/Play shortcut when its main key is released.
            if (e.KeyCode == _prefs.PauseOverlayHotkeyKey &&
                _pauseShortcutHeld)
            {
                _pauseShortcutHeld = false;

                // While paused, the visible Pause indicator must remain persistent.
                // Only release the tile after the shortcut has resumed the overlay
                // and changed that tile into the pressed Play indicator.
                if (!_overlayPaused)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ReleasePlayIndicator();
                    });
                }

                return;
            }

            string key = IsModifierKey(e.KeyCode) ? NormalizeModifierId(e.KeyCode) : e.KeyCode.ToString();

            // For non-modifier keys: mark as no longer physically held.
            // We still keep a watchdog fallback in case future hook state drifts.
            if (!IsModifierKey(e.KeyCode))
            {
                _downKeys.Remove(e.KeyCode);
                _pendingRegularUps[key] = DateTime.UtcNow;
                StartWatchdogIfNeeded();
            }

            // NumPad release extends the guard a little (prevents flicker on release)
            if (IsNumPadKey(e.KeyCode))
            {
                ExtendNumpadGuard(ShiftDebounceMs);
                StartWatchdogIfNeeded();
            }

            // Generalized modifier handling (Shift/Ctrl/Alt)
            if (IsModifierKey(e.KeyCode))
            {
                bool stillPhysicallyDown = IsPhysicallyDown(e.KeyCode);
                bool guardActive = GuardActive() || AnyNumPadPhysicallyDown();

                if (stillPhysicallyDown || guardActive)
                {
                    _pendingModUps.Add(key);                   // "ShiftKey", "CtrlKey", "AltKey"
                    _pendingModSince[key] = DateTime.UtcNow;
                    StartWatchdogIfNeeded();
                    return; // DO NOT remove from _downKeys/UI yet
                }

                // Safe path: really up and no guard → release now
                _downKeys.Remove(e.KeyCode);                   // remove the raw L/R code
                ReleaseKeyUI(key);                             // normalized id
                return;
            }

            if (_activeKeyBoxes.TryGetValue(key, out var entry))
            {
                var (viewbox, scale, _) = entry; // 3rd item is now deadlineUtc; no timer to stop

                // Predict width for the *current* order with a pop on this key *only if it’s rightmost*
                SetPillWidthForOrder(_pillOrder);

                // Animate scale to 1.35x on release
                var up = new DoubleAnimation
                {
                    To = KeyReleasePopScale,
                    Duration = TimeSpan.FromMilliseconds(100),
                    FillBehavior = FillBehavior.HoldEnd
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);

                // Coalesce layout updates once (prevents racing with layout)
                InvalidatePillLayout();

                // Schedule removal via global frame culler
                _activeKeyBoxes[key] = (viewbox, scale, DateTime.UtcNow.AddMilliseconds(KeyHangMs));
                UpdatePillVisibility();
            }
        }

        /// <summary>Query the real hardware state (GetKeyState) for a specific key.</summary>
        private static bool IsPhysicallyDown(Keys k)
        {
            // High-order bit set means key is down
            return (NativeMethods.GetAsyncKeyState((int)k) & 0x8000) != 0;
        }

        /// <summary>Return true if any Numpad key is physically held down.</summary>
        private static bool AnyNumPadPhysicallyDown()
        {
            // Digits
            for (var k = Keys.NumPad0; k <= Keys.NumPad9; k++)
                if (IsPhysicallyDown(k)) return true;

            // Operators
            return IsPhysicallyDown(Keys.Add)
                || IsPhysicallyDown(Keys.Subtract)
                || IsPhysicallyDown(Keys.Multiply)
                || IsPhysicallyDown(Keys.Divide)
                || IsPhysicallyDown(Keys.Decimal);
        }

        /// <summary>True if the current UTC time is ≤ the given UTC deadline.</summary>
        private static bool IsWithin(DateTime until) => DateTime.UtcNow <= until;

        /// <summary>Extend the Numpad guard so transient key-ups don’t flicker the UI.</summary>
        private void ExtendNumpadGuard(int ms = NumpadGuardMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            if (until > _numpadGuardUntil) _numpadGuardUntil = until;
        }

        /// <summary>Start a short watchdog timer to coalesce/guard Shift/Numpad transitions.</summary>	
        private void StartWatchdogIfNeeded()
        {
            if (!_keyStateWatch.IsEnabled) _keyStateWatch.Start();
        }

        /// <summary>Is any guard preventing Shift/Numpad state changes currently active?</summary>
        private bool GuardActive() => IsWithin(_numpadGuardUntil) || _downKeys.Any(IsNumPadKey) || AnyNumPadPhysicallyDown();

        /// <summary>Toggle “Transparent-mode” and enable/disable resize borders.</summary>
        private void SetTransparentMode(bool enabled, bool withPrompt = true)
        {
            if (enabled == _transparentToMouse) return;

            if (enabled && withPrompt)
            {
                if (!ShowTransparentInfoDialog())
                    return; // User cancelled
            }

            _transparentToMouse = enabled;
            NativeMethods.SetWindowClickThrough(this, enabled);

            // Make sure style flips didn’t remove our taskbar icon
            NativeMethods.EnsureAppWindow(this);

            // Stay assertive on top while transparent mode is active
            _topmostPulse.Interval = TimeSpan.FromMilliseconds(_transparentToMouse ? 750 : 2000);
            ReassertTopmost(); // once immediately after toggling

            IsHitTestVisible = !_transparentToMouse;
            ResizeMode = _transparentToMouse ? ResizeMode.NoResize : ResizeMode.CanResize;

            // Enable/disable invisible resize borders (same list you use now)
            foreach (var border in new[]
            {
                TopResize, BottomResize, LeftResize, RightResize,
                TopLeftResize, TopRightResize, BottomLeftResize, BottomRightResize
            })
            {
                border.IsHitTestVisible = !_transparentToMouse;
            }

            if (_transparentToMouse)
            {
                // Transparent-to-mouse mode: no visuals, no interaction
                RoundedVisualFrame.CornerRadius = new CornerRadius(0);
                RoundedVisualFrame.Background = _fullyTransparentBrush;
                RoundedVisualFrame.Effect = null;
            }
            else
            {
                // Normal overlay mode: rounded corners + subtle solid background
                RoundedVisualFrame.CornerRadius = new CornerRadius(12);
                var frameBrush = new SolidColorBrush(Color.FromArgb(WindowAlpha, 0, 0, 0));
                if (frameBrush.CanFreeze) frameBrush.Freeze();
                RoundedVisualFrame.Background = frameBrush;
                RoundedVisualFrame.Effect = null;
            }

            // Apply/remove the 1px outline depending on mode
            UpdateWindowBorderChrome();

            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;

            // Redraw chrome/frame and refresh the buttons
            InvalidateVisual();
            UpdateChromeButtons();

            // After changing extended styles, immediately re-assert TopMost without activation.
            Topmost = true;
            ReassertTopmost();
        }


        // === Mouse SVG + key tile composition ===

        /// <summary>Swap the mouse SVG visual and arm a timer to return to idle/neutral.</summary>
        private void SetMouseSvg(string filename)
        {
            if (!_mouseEnabled) return;

            string srcPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", filename);
            if (!File.Exists(srcPath)) return;

            // Build/lookup the tinted copy for the *current* color first
            string tintedPath = EnsureMouseSvgWithColor(srcPath, _mouseColorRgb);

            // Only skip if BOTH the image AND the tinted path are identical to what already is shown
            if (_mouseInitialized &&
                _currentMouseImage == filename &&
                string.Equals(_lastMouseTintPath, tintedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentMouseImage = filename;
            _mouseInitialized = true;
            _lastMouseTintPath = tintedPath;

            if (File.Exists(tintedPath))
            {
                void DoLoad()
                {
                    _mouseSvgDisplay?.Load(new Uri(tintedPath));
                }

                if (Dispatcher.CheckAccess())
                    DoLoad();
                else
                    Dispatcher.Invoke(DoLoad, System.Windows.Threading.DispatcherPriority.Render);
            }
        }

        /// <summary>Match CSS inline style fill (#282828) in SVG markup (case-insensitive, invariant).</summary>
        [GeneratedRegex(@"(fill\s*:\s*)#282828\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FillStyle282828Regex();

        /// <summary>Match SVG fill attribute (#282828) in markup (case-insensitive, invariant).</summary>
        [GeneratedRegex(@"(fill\s*=\s*[""'])#282828([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FillAttr282828Regex();

        /// <summary>Return a cached path to srcSvg recolored where fill == #282828 → rgb (no strokes/gradients/‘fill:none’).</summary>
        private static string EnsureMouseSvgWithColor(string srcSvg, Color rgb)
        {
            try
            {
                // Cache folder (%TEMP%\KeyClickOverlay\mouse_tint_cache)
                string cacheDir = IOPath.Combine(Path.GetTempPath(), "KeyClickOverlay", "mouse_tint_cache");
                Directory.CreateDirectory(cacheDir);

                // Precompute color-keyed filename
                string baseName = IOPath.GetFileName(srcSvg);
                string hex = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";             // target color
                string destName = $"{IOPath.GetFileNameWithoutExtension(baseName)}.{hex.TrimStart('#').ToLowerInvariant()}.svg";
                string destPath = IOPath.Combine(cacheDir, destName);

                // Reuse if already generated
                if (File.Exists(destPath)) return destPath;

                string text = File.ReadAllText(srcSvg);

                // Replace style-based fills exactly matching #282828 (case-insensitive)
                text = FillStyle282828Regex().Replace(text, $"$1{hex}");

                // Replace attribute-based fills exactly matching #282828
                text = FillAttr282828Regex().Replace(text, $"$1{hex}$2");

                // ‘fill:none’ and non-#282828 values are untouched by design
                File.WriteAllText(destPath, text);
                return destPath;
            }
            catch
            {
                // On error, fall back to original (untinted)
                return srcSvg;
            }
        }

        /// <summary>Generate (or reuse) tinted copies for all mouse SVG states so clicks load instantly.</summary>
        private static void PrecacheAllMouseSvgsForColor(Color rgb)
        {
            string root = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg");
            foreach (var name in MouseSvgNames)
            {
                string src = IOPath.Combine(root, name);
                if (File.Exists(src))
                    _ = EnsureMouseSvgWithColor(src, rgb);
            }
        }

        /// <summary>Match style-based fills exactly "#e5e5e5" (captures 'fill:' as $1).</summary>
        [GeneratedRegex(@"(fill\s*:\s*)#e5e5e5\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FillStyleE5Regex();

        /// <summary>Match attribute-based fills exactly "#e5e5e5" (captures prefix as $1 and closing quote as $2).</summary>
        [GeneratedRegex(@"(fill\s*=\s*[""'])#e5e5e5([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex FillAttrE5Regex();

        /// <summary>Return a cached path with <paramref name="srcSvg"/> rewritten so only fill:#e5e5e5 becomes the chosen RGB.</summary>
        private static string EnsureKeySvgWithTextColor(string srcSvg, Color rgb)
        {
            try
            {
                string cacheDir = IOPath.Combine(Path.GetTempPath(), "KeyClickOverlay", "key_tint_cache");
                Directory.CreateDirectory(cacheDir);

                string baseName = IOPath.GetFileName(srcSvg);
                string hex = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
                string destName = $"{IOPath.GetFileNameWithoutExtension(baseName)}.{hex.TrimStart('#').ToLowerInvariant()}.svg";
                string destPath = IOPath.Combine(cacheDir, destName);

                if (File.Exists(destPath)) return destPath;

                string text = File.ReadAllText(srcSvg);

                // Replace ONLY exact #e5e5e5 (case-insensitive); do not touch fill:none, strokes, or other colors.
                text = FillStyleE5Regex().Replace(text, $"$1{hex}");
                text = FillAttrE5Regex().Replace(text, $"$1{hex}$2");

                File.WriteAllText(destPath, text);
                return destPath;
            }
            catch
            {
                return srcSvg;
            }
        }

        /// <summary>Reload all visible key icons (SvgViewbox) using the current _fontColorRgb.</summary>
        private void RetintAllKeyIconsForFontColor()
        {
            static IEnumerable<SvgViewbox> FindIcons(DependencyObject root)
            {
                if (root is SvgViewbox sv) { yield return sv; }
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                    foreach (var s in FindIcons(VisualTreeHelper.GetChild(root, i))) yield return s;
            }

            // Hosts from active keys + preview row
            var hosts = new List<FrameworkElement>(_activeKeyBoxes.Values.Select(v => v.element));
            hosts.AddRange(_previewFontKeyHosts);

            foreach (var host in hosts)
            {
                foreach (var svg in FindIcons(host))
                {
                    if (svg.Tag is string file && !string.IsNullOrWhiteSpace(file))
                    {
                        string src = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", file);
                        if (File.Exists(src))
                        {
                            string tinted = EnsureKeySvgWithTextColor(src, _fontColorRgb);
                            svg.Load(new Uri(tinted));
                        }
                    }
                }
            }
        }

        /// <summary>Warm the cache by pre-tinting all key SVGs (specials + space) for the given text color.</summary>
        private void PrecacheAllKeySvgsForColor(Color rgb)
        {
            var root = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg");
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // All mapped special/icon-only SVGs
            foreach (var f in _specialKeyIconMap.Values) files.Add(f);

            // Space bar icon
            files.Add("space.svg");

            foreach (var f in files)
            {
                string src = IOPath.Combine(root, f);
                if (File.Exists(src))
                    _ = EnsureKeySvgWithTextColor(src, rgb);
            }
        }

        /// <summary>Fade out/remove the UI element for a released key.</summary>
        private void ReleaseKeyUI(string keyString)
        {
            if (_activeKeyBoxes.TryGetValue(keyString, out var entry))
            {
                var (viewbox, scale, _) = entry; // 3rd item is deadlineUtc now—no timer to stop

                // Pop to 1.35x (same as in KeyUp)
                var up = new DoubleAnimation
                {
                    To = KeyReleasePopScale,
                    Duration = TimeSpan.FromMilliseconds(100),
                    FillBehavior = FillBehavior.HoldEnd
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, up);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, up);

                // Schedule a single, coalesced layout pass (fit/heights/spacing/width) at Render
                InvalidatePillLayout();

                SetPillWidthForOrder(_pillOrder); // Recompute predicted width for current order

                // Schedule removal via global frame culler
                _activeKeyBoxes[keyString] = (viewbox, scale, DateTime.UtcNow.AddMilliseconds(KeyHangMs));
            }
        }

        /// <summary>Map raw key codes to overlay-friendly display names.</summary>
        private static string NormalizeKeyName(System.Windows.Forms.Keys key)
        {
            string raw = key.ToString().ToLowerInvariant();

            // Detect if Shift is held
            bool shiftHeld = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift;

            // Modifiers (combine left/right into one name)
            if (raw.Contains("shift")) return "Shift";
            if (raw.Contains("control") || raw.Contains("ctrl")) return "Ctrl";
            if (raw.Contains("menu") || raw.Contains("alt")) return "Alt"; // Menu = Alt
            if (raw.Contains("win")) return "Win";

            // Space & Enter
            if (raw == "space") return "Space";
            if (raw == "return") return "Enter";

            // Escape & System Keys
            if (raw == "escape") return "Esc";
            if (raw == "back") return "Backspace";
            if (raw == "capital") return "CapsLock";
            if (raw == "snapshot" || raw == "printscreen" || raw == "prtsc") return "Prt Sc";
            if (raw == "prior" || raw == "pageup") return "PgUp";
            if (raw == "next") return "PgDn";
            if (raw == "scroll") return "ScrollLock";
            if (raw == "pause" || raw == "break") return "Pause";
            if (raw == "insert") return "Insert";
            if (raw == "delete") return "Delete";
            if (raw == "home") return "Home";
            if (raw == "end") return "End";
            if (raw == "tab") return "Tab";
            if (raw == "apps") return "Menu"; // Context menu key

            // Numpad Math Operations
            if (raw == "divide") return "/";
            if (raw == "multiply") return "*";
            if (raw == "subtract") return "-";
            if (raw == "add") return "+";
            if (raw == "decimal") return ".";

            // Arrow Keys
            if (raw == "up") return "ArrowUp";
            if (raw == "down") return "ArrowDown";
            if (raw == "left") return "ArrowLeft";
            if (raw == "right") return "ArrowRight";

            // Launch Keys
            if (raw == "browserhome") return "HomePage";
            if (raw == "launchmail") return "Mail";
            if (raw == "launchapplication2") return "Calculator";

            // Media Keys
            if (raw == "volumeup") return "Vol+";
            if (raw == "volumedown") return "Vol-";
            if (raw == "volumemute" || raw == "mute") return "Mute";
            if (raw == "mediaplaypause") return "Play/Pause";
            if (raw == "medianexttrack") return "Next";
            if (raw == "mediaprevioustrack") return "Prev";
            if (raw == "mediastop") return "Stop";

            // Top-row digits (D0..D9)
            if (raw.Length == 2 && raw[0] == 'd' && char.IsDigit(raw[1]))
            {
                return raw[1] switch
                {
                    '1' => shiftHeld ? "!" : "1",
                    '2' => shiftHeld ? "@" : "2",
                    '3' => shiftHeld ? "#" : "3",
                    '4' => shiftHeld ? "$" : "4",
                    '5' => shiftHeld ? "%" : "5",
                    '6' => shiftHeld ? "^" : "6",
                    '7' => shiftHeld ? "&" : "7",
                    '8' => shiftHeld ? "*" : "8",
                    '9' => shiftHeld ? "(" : "9",
                    '0' => shiftHeld ? ")" : "0",
                    _ => raw.ToUpperInvariant()
                };
            }

            // OEM Keys (punctuation)
            return raw switch
            {
                "oemperiod" or "oemdot" => shiftHeld ? ">" : ".",
                "oemcomma" => shiftHeld ? "<" : ",",
                "oem2" or "oemslash" => shiftHeld ? "?" : "/",
                "oem1" or "oemsemicolon" => shiftHeld ? ":" : ";",
                "oem7" or "oemquotes" => shiftHeld ? "\"" : "'",
                "oem6" or "oemclosebrackets" => shiftHeld ? "}" : "]",
                "oem4" or "oemopenbrackets" => shiftHeld ? "{" : "[",
                "oem5" or "oempipe" or "oembackslash" => shiftHeld ? "|" : "\\",
                "oemminus" or "oemdash" => shiftHeld ? "_" : "-",
                "oemplus" or "oemequal" => shiftHeld ? "+" : "=",
                "oemtilde" or "oem3" or "oemgrave" => shiftHeld ? "~" : "`",
                _ => char.ToUpper(raw[0]) + raw[1..] // Fallback
            };
        }

        /// <summary>Return true if the key is a sign/symbol (e.g., +, −).</summary>
        private static bool IsSignKey(string key)
        {
            return _signKeys.Contains(key);
        }

        /// <summary>Decide whether this key needs a wider text tile (e.g., “Enter”).</summary>
        private static bool ShouldUseWideText(string displayKey)
        {
            // Keep single characters, digits, and sign keys square
            if (displayKey.Length <= 1) return false;
            if (IsSignKey(displayKey)) return false;
            if (displayKey.Length == 1 && char.IsLetterOrDigit(displayKey[0])) return false;

            // Treat function keys (F1..F24) as wide so they can grow horizontally
            if (displayKey.Length >= 2 && displayKey[0] == 'F')
            {
                if (int.TryParse(displayKey.AsSpan(1), out int fn) && fn >= 1 && fn <= 24)
                    return true;
            }

            // Everything else (multi-char words / identifiers) → wide
            return displayKey.Length > 1;
        }

        /// <summary>Treat NumPad math operators as NumPad.</summary>
        private static readonly HashSet<Keys> _numPadExtras =
        [
            Keys.Add,
            Keys.Subtract,
            Keys.Multiply,
            Keys.Divide,
            Keys.Decimal,
            Keys.Separator,
        ];

        /// <summary>Return true for any NumPad key (digits + operators).</summary>
        private static bool IsNumPadKey(Keys k)
        {
            return (k >= Keys.NumPad0 && k <= Keys.NumPad9) || _numPadExtras.Contains(k);
        }

        /// <summary>Return true for NumPad operator keys (+, −, ×, ÷, .).</summary>
        private static bool IsNumPadOperatorKey(Keys k) => _numPadExtras.Contains(k);

        /// <summary>Return true for any modifier (Shift/Ctrl/Alt, left or right).</summary>
        private static bool IsModifierKey(Keys k) =>
            k is Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey
             or Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
             or Keys.Menu or Keys.LMenu or Keys.RMenu;

        /// <summary>Collapse left/right variants to a single id.</summary>
        private static string NormalizeModifierId(Keys k) => k switch
        {
            Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey => "ShiftKey",
            Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => "CtrlKey",
            Keys.Menu or Keys.LMenu or Keys.RMenu => "AltKey",
            _ => k.ToString()
        };


        // === Builders for individual key tiles ===

        /// <summary>Maps normalized key label to corresponding SVG icon filenames in <c>assets/svg/</c>.</summary>
        private readonly Dictionary<string, string> _specialKeyIconMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Modifiers
            { "Alt", "alt.svg" },
            { "Ctrl", "ctrl.svg" },
            { "Shift", "shift.svg" },
            { "Win", "win.svg" },

            // Navigation
            { "ArrowUp", "arrowup.svg" },
            { "ArrowDown", "arrowdown.svg" },
            { "ArrowLeft", "arrowleft.svg" },
            { "ArrowRight", "arrowright.svg" },

            // System Keys
            { "Backspace", "backspace.svg" },
            { "CapsLock", "capslock.svg" },
            { "Delete", "backspace.svg" },
            { "End", "end.svg" },
            { "Enter", "enter.svg" },
            { "Esc", "escape.svg" },
            { "Home", "home.svg" },
            { "Insert", "insert.svg" },
            { "NumLock", "numlock.svg" },
            { "PgDn", "pagedown.svg" },
            { "PgUp", "pageup.svg" },
            { "Pause", "pause.svg" },
            { "Prt Sc", "printscreen.svg" },
            { "ScrollLock", "scrolllock.svg" },
            { "Space", "space.svg" },
            { "Tab", "tab.svg" },
            { "Menu", "menu.svg" },

			// Launch Keys
			{ "HomePage", "browser.svg" },
            { "Mail", "mail.svg" },
            { "Calculator", "calculator.svg" },

            // Media Keys
            { "Vol+", "volumeup.svg" },
            { "Vol-", "volumedown.svg" },
            { "Mute", "mute.svg" },
            { "Play/Pause", "playpause.svg" },
            { "Next", "forward.svg" },
            { "Prev", "backward.svg" },
            { "Stop", "stop.svg" }
        };

        /// <summary>Keys that should display only an icon (no text label) in the overlay.</summary>
        private readonly HashSet<string> _iconOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "HomePage", "Mail", "Calculator",
            "Vol+", "Vol-", "Mute", "Play/Pause", "Next", "Prev", "Stop"
        };

        /// <summary>Set of common sign/punctuation keys that need special horizontal alignment when rendered in the overlay.</summary>
        private static readonly HashSet<string> _signKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "+", "=", "-", "_", "~", "<", ">", ".", ",", ":", ";", "`","\"", "~", "'", "|", "^", "*", "$", "@", "#",
        };


        /// <summary>Build the special wide “Space” tile.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildSpaceKey(double squareSize, double scaleFactor, Color iconColor, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            // Same SVG icon setup as in BuildIconOnlyKey
            var icon = new SvgViewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = KeyStyle.IconInnerMargin(squareSize)
            };

            string iconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", "space.svg");
            if (File.Exists(iconPath))
            {
                string tinted = EnsureKeySvgWithTextColor(iconPath, iconColor);
                icon.Load(new Uri(tinted));
                icon.Tag = "space.svg"; // Tag so we can retint live later
            }

            // Non-stretch centering host prevents rounding/stretch bias
            var centeredHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            centeredHost.Children.Add(icon);

            var keyContent = new Border
            {
                MinWidth = KeyStyle.SpaceMinWidth(squareSize), // Wider box
                Width = double.NaN,                             // Auto width
                Height = squareSize,                            // Same height as other keys
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Padding = new Thickness(0),
                Child = centeredHost,                           // Non-visual host
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            TextOptions.SetTextFormattingMode(keyContent, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(keyContent, TextRenderingMode.Auto);

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent,
                Tag = SpaceKeyTag
            };

            return (outerViewbox, scale);
        }

        /// <summary>Build a key tile that shows only an icon (no text).</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildIconOnlyKey(string iconFile, double squareSize, double scaleFactor, Color iconColor, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            var icon = new SvgViewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = KeyStyle.IconInnerMargin(squareSize)
            };

            string iconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", iconFile);
            if (File.Exists(iconPath))
            {
                string tinted = EnsureKeySvgWithTextColor(iconPath, iconColor);
                icon.Load(new Uri(tinted));
                icon.Tag = iconFile; // Remember which SVG for live retint
            }

            // Non-stretch centering host prevents rounding/stretch bias
            var centeredHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            centeredHost.Children.Add(icon);

            var keyContent = new Border
            {
                Width = squareSize,
                Height = squareSize,
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Padding = new Thickness(0),
                Child = centeredHost,          // Non-visual host
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }

        /// <summary>Build a numeric keypad tile with consistent sizing.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildNumPadKey(string displayKey, double squareSize, double scaleFactor, Brush textBrush, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            var grid = new Grid { Height = squareSize, Width = squareSize };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.25, GridUnitType.Star) }); // header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.75, GridUnitType.Star) }); // main digit

            // Small "NumPad" label
            var header = new TextBlock
            {
                Text = "NumPad",
                FontFamily = KeyStyle.Font,
                Foreground = textBrush,
                FontSize = KeyStyle.NormalFontSize(squareSize),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = KeyStyle.NumPadHeaderMargin(squareSize)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            string digit = displayKey.Replace("Numpad", "", StringComparison.OrdinalIgnoreCase);
            var centeredDigit = BuildCenteredGlyphElement(digit, squareSize * 0.75 * KeyStyle.GlyphHeightFactor_NumPad, textBrush);

            // ↑ move the digit slightly up within the bottom row
            double rowHeight = squareSize * 0.75;
            double nudge = rowHeight * 0.06; // 6% feels like a real keyboard
            centeredDigit.Margin = new Thickness(0, -nudge, 0, nudge);

            Grid.SetRow(centeredDigit, 1);
            grid.Children.Add(centeredDigit);

            var keyContent = new Border
            {
                Width = squareSize,
                Height = squareSize,
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Child = grid
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }

        /// <summary>Builds a NumPad-operator tile with a "NumPad" header and a baseline-aligned operator face.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildNumPadOperatorKey(string face, double squareSize, double scaleFactor, Brush textBrush, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            // Header (same style as BuildNumPadKey)
            var header = new TextBlock
            {
                Text = "NumPad",
                FontFamily = KeyStyle.Font,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = KeyStyle.NormalFontSize(squareSize),
                Foreground = textBrush,
                Margin = KeyStyle.NumPadHeaderMargin(squareSize)
            };

            // Baseline-aligned operator face (reuses existing baseline path for sign keys)
            var operatorFace = BuildBaselineAlignedGlyphElement(text: face, targetCapHeight: squareSize * KeyStyle.GlyphHeightFactor_Normal, textBrush: textBrush);

            // Subtle vertical nudge to match keypad composition
            double rowHeight = squareSize * 0.75;
            double nudge = rowHeight * 0.06;
            operatorFace.Margin = new Thickness(0, -nudge, 0, nudge);

            // Two-row layout: header (top) + face (bottom)
            var grid = new Grid { Height = squareSize, Width = squareSize };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.25, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.75, GridUnitType.Star) });
            Grid.SetRow(header, 0); grid.Children.Add(header);
            Grid.SetRow(operatorFace, 1); grid.Children.Add(operatorFace);

            // Background tile and outer viewbox (consistent with other builders)
            var keyContent = new Border
            {
                Width = squareSize,
                Height = squareSize,
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Child = grid
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }

        /// <summary>Build a special-shaped key tile (e.g., Enter) with a custom icon.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildSpecialKey(string displayKey, string specialIconFile, double squareSize, double specialKeyHeight, double scaleFactor, Brush textBrush, Color iconColor, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            var grid = new Grid { Height = specialKeyHeight };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.55, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.45, GridUnitType.Star) });

            // SVG Icon
            var icon = new SvgViewbox
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = KeyStyle.SpecialIconMargin(squareSize)
            };

            string iconPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "svg", specialIconFile);
            if (File.Exists(iconPath))
            {
                string tinted = EnsureKeySvgWithTextColor(iconPath, iconColor);
                icon.Load(new Uri(tinted));
                icon.Tag = specialIconFile;
            }

            Grid.SetRow(icon, 0);
            grid.Children.Add(icon);

            // Text element for key label
            var label = new TextBlock
            {
                Text = displayKey,
                FontFamily = KeyStyle.Font,
                Foreground = textBrush,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = KeyStyle.SpecialLabelMargin(squareSize),
                FontSize = KeyStyle.SpecialLabelFontSize(squareSize),
            };
            Grid.SetRow(label, 1);
            grid.Children.Add(label);

            var keyContent = new Border
            {
                MinWidth = squareSize,
                Width = Double.NaN,
                Height = squareSize,
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Child = grid
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }

        /// <summary>Build a wide text tile for long labels.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildWideTextKey(string displayKey, double squareSize, double scaleFactor, Brush textBrush, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            // Same glyph height as a normal key (keyboard-like look)
            double targetGlyphHeight = squareSize * KeyStyle.GlyphHeightFactor_Normal;

            // Horizontal breathing room for wide keys
            var pad = KeyStyle.WideText_BorderPadding(squareSize);

            // Build glyph for a WIDE key; get its natural width at the target height
            var centeredGlyph = BuildCenteredGlyphElement(
                text: displayKey,
                targetGlyphHeight: targetGlyphHeight,
                textBrush: textBrush,
                forWideKey: true,
                wideBorderPadding: pad,
                out double naturalWidth);

            // Non-stretch centering host prevents rounding/stretch bias
            var centeredHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            centeredHost.Children.Add(centeredGlyph);

            // Let the key widen if the label won’t fit at the normal glyph height
            var keyContent = new Border
            {
                MinWidth = Math.Max(squareSize, naturalWidth), // Widen when needed
                Width = double.NaN,                            // Allow growth
                Height = squareSize,                           // Same baseline height
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Padding = pad,
                Child = centeredHost,                          // Non-visual host
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }

        /// <summary>Build a standard square key tile with text.</summary>
        private static (FrameworkElement outer, ScaleTransform scale) BuildNormalKey(string displayKey, double squareSize, double scaleFactor, Brush textBrush, Brush keyFillBrush)
        {
            var scale = new ScaleTransform(1.0, 1.0);

            bool isSign = IsSignKey(displayKey);

            FrameworkElement glyphElement =
                isSign
                ? BuildBaselineAlignedGlyphElement(
                      text: displayKey,
                      targetCapHeight: squareSize * KeyStyle.GlyphHeightFactor_Normal, // match letter cap-height
                      textBrush: textBrush)
                : BuildCenteredGlyphElement(text: displayKey, targetGlyphHeight: squareSize * KeyStyle.GlyphHeightFactor_Normal, textBrush: textBrush);

            // Non-visual centering host (prevents stretch & rounding bias)
            var centeredHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
                UseLayoutRounding = false
            };
            centeredHost.Children.Add(glyphElement);

            var keyContent = new Border
            {
                Width = squareSize,
                Height = squareSize,
                CornerRadius = new CornerRadius(KeyStyle.CornerRadius(squareSize)),
                Background = keyFillBrush,
                Padding = new Thickness(0),
                Child = centeredHost,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var outerViewbox = new Viewbox
            {
                Stretch = Stretch.Uniform,
                Height = scaleFactor,
                Margin = new Thickness(0),
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = keyContent
            };

            return (outerViewbox, scale);
        }
    }
}