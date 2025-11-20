using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;

namespace WpfApp1
{
    public class DpiAwareWindowBase : Window
    {
        // --- Persistence Settings ---

        /// <summary>
        /// Unique ID for this window. If you have multiple windows of the same class,
        /// assign a unique ID (e.g., "MainWin", "ToolWin1") to save separate settings.
        /// </summary>
        public string WindowId { get; set; }

        /// <summary>
        /// The storage mechanism. Defaults to JSON file storage.
        /// </summary>
        public IWindowSettingsStore SettingsStore { get; set; } = new JsonFileSettingsStore();

        // --- State ---

        private Size _originalSize = Size.Empty;

        // Changed Key from IntPtr to String (DeviceName) for persistence across reboots
        private Dictionary<string, Size> _monitorSizeCache = new Dictionary<string, Size>();

        private IntPtr _currentMonitor;
        private bool _originalSizeStored;
        private bool _isAdjusting;
        private bool _pendingWpfUpdate;

        // Interaction State
        private bool _userResizing;
        private bool _isResizingOperation;
        private bool _ignoreNextSizeChange;

        private const int SafetyMargin = 30;

        public DpiAwareWindowBase()
        {
            // Default WindowId to the class name if not set
            WindowId = this.GetType().Name;

            SourceInitialized += OnSourceInitialized;
            LocationChanged += OnLocationChanged;
            SizeChanged += OnSizeChanged;
            Closing += OnClosing;
        }

        // --- Event Handlers ---

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WndProc);

            // 1. Store Original defaults
            if (!_originalSizeStored)
            {
                _originalSize = new Size(Width, Height);
                _originalSizeStored = true;
            }

            _currentMonitor = NativeMethods.GetMonitorFromWindow(new WindowInteropHelper(this).Handle);

            // 2. Try Load Settings
            RestoreWindowSettings();

            // 3. Ensure cache has entry for current monitor (if Restore didn't fill it)
            string deviceName = GetMonitorName(_currentMonitor);
            if (!_monitorSizeCache.ContainsKey(deviceName))
            {
                _monitorSizeCache[deviceName] = new Size(Width, Height);
            }
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            SaveWindowSettings();
        }

        private void OnLocationChanged(object sender, EventArgs e)
        {
            if (_userResizing || !_originalSizeStored || _isAdjusting) return;

            var newMonitor = NativeMethods.GetMonitorFromWindow(new WindowInteropHelper(this).Handle);
            if (newMonitor != _currentMonitor)
            {
                _currentMonitor = newMonitor;
                PerformAdjustment();
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_originalSizeStored || _isAdjusting) return;

            if (_ignoreNextSizeChange)
            {
                _ignoreNextSizeChange = false;
                return;
            }

            if (!_userResizing || _isResizingOperation)
            {
                string deviceName = GetMonitorName(_currentMonitor);
                _monitorSizeCache[deviceName] = e.NewSize;
                LogDebug($"Size Updated for {deviceName}: {e.NewSize}");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOVING = 0x0216;
            const int WM_SIZING = 0x0214;
            const int WM_ENTERSIZEMOVE = 0x0231;
            const int WM_EXITSIZEMOVE = 0x0232;
            const int WM_DPICHANGED = 0x02E0;

            switch (msg)
            {
                case WM_ENTERSIZEMOVE:
                    _userResizing = true;
                    _isResizingOperation = false;
                    break;

                case WM_EXITSIZEMOVE:
                    _userResizing = false;
                    PerformAdjustment();
                    break;

                case WM_SIZING:
                    _isResizingOperation = true;
                    break;

                case WM_MOVING:
                    HandleDynamicMove(lParam);
                    break;

                case WM_DPICHANGED:
                    PerformAdjustment();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        // --- Persistence Methods ---

        private void SaveWindowSettings()
        {
            if (SettingsStore == null) return;

            var settings = new WindowSettings
            {
                Top = this.Top,
                Left = this.Left,
                Width = this.Width,
                Height = this.Height,
                WindowState = this.WindowState,
                MonitorSizeCache = new Dictionary<string, Size>(_monitorSizeCache)
            };

            SettingsStore.Save(WindowId, settings);
            LogDebug("Settings Saved.");
        }

        private void RestoreWindowSettings()
        {
            if (SettingsStore == null) return;

            var settings = SettingsStore.Load(WindowId);
            if (settings != null)
            {
                // Restore Cache
                if (settings.MonitorSizeCache != null)
                {
                    _monitorSizeCache = settings.MonitorSizeCache;
                }

                // Restore Position/Size
                // Validation: Ensure we don't restore off-screen or to NaN
                if (!double.IsNaN(settings.Left) && !double.IsNaN(settings.Top))
                {
                    this.Left = settings.Left;
                    this.Top = settings.Top;
                }

                if (!double.IsNaN(settings.Width) && !double.IsNaN(settings.Height))
                {
                    this.Width = settings.Width;
                    this.Height = settings.Height;
                }

                // Restore State (Maximized, etc)
                if (settings.WindowState != WindowState.Minimized)
                {
                    this.WindowState = settings.WindowState;
                }

                LogDebug("Settings Restored.");
            }
        }

        // --- Core Adjustment Logic ---

        private void HandleDynamicMove(IntPtr lParam)
        {
            NativeMethods.RECT rect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);

            NativeMethods.GetCursorPos(out var cursorPos);
            IntPtr monitorHandle = NativeMethods.MonitorFromPoint(cursorPos, NativeMethods.MONITOR_DEFAULTTONEAREST);

            if (monitorHandle != _currentMonitor) _currentMonitor = monitorHandle;

            var (workArea, dpiScale) = GetMonitorSettings(monitorHandle);

            // Get Device Name for dictionary lookup
            string deviceName = GetMonitorName(monitorHandle);

            Size preferredSizeDip = GetPreferredSizeForMonitor(deviceName);

            int targetW = (int)(preferredSizeDip.Width * dpiScale);
            int targetH = (int)(preferredSizeDip.Height * dpiScale);

            int finalW = Math.Min(targetW, workArea.Width - SafetyMargin);
            int finalH = Math.Min(targetH, workArea.Height - SafetyMargin);

            bool modified = false;
            int currentW = rect.Right - rect.Left;
            int currentH = rect.Bottom - rect.Top;

            if (Math.Abs(currentW - finalW) > 2) { rect.Right = rect.Left + finalW; modified = true; }
            if (Math.Abs(currentH - finalH) > 2) { rect.Bottom = rect.Top + finalH; modified = true; }

            if (modified)
            {
                Marshal.StructureToPtr(rect, lParam, true);
            }
        }

        private void PerformAdjustment()
        {
            _isAdjusting = true;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var (workAreaPx, dpiScale) = GetMonitorSettings(_currentMonitor);
                var (currentRectPx, currentSizeDip) = GetWindowRect(hwnd, dpiScale);

                string deviceName = GetMonitorName(_currentMonitor);
                Size preferredSizeDip = GetPreferredSizeForMonitor(deviceName);

                if (currentSizeDip.Width > preferredSizeDip.Width) preferredSizeDip.Width = currentSizeDip.Width;
                if (currentSizeDip.Height > preferredSizeDip.Height) preferredSizeDip.Height = currentSizeDip.Height;

                double workWidthDip = (workAreaPx.Width - SafetyMargin) / dpiScale;
                double workHeightDip = (workAreaPx.Height - SafetyMargin) / dpiScale;

                double finalWidthDip = Math.Min(preferredSizeDip.Width, workWidthDip);
                double finalHeightDip = Math.Min(preferredSizeDip.Height, workHeightDip);

                // Update cache using string key
                _monitorSizeCache[deviceName] = new Size(finalWidthDip, finalHeightDip);

                if (Math.Abs(finalWidthDip - currentSizeDip.Width) > 2 ||
                    Math.Abs(finalHeightDip - currentSizeDip.Height) > 2)
                {
                    LogDebug($"Adjusting for {deviceName}: {currentSizeDip} -> {finalWidthDip:F0}x{finalHeightDip:F0}");

                    int finalPxW = (int)(finalWidthDip * dpiScale);
                    int finalPxH = (int)(finalHeightDip * dpiScale);

                    SetWindowSizeForced(hwnd, finalPxW, finalPxH);
                    EnsureTopLeftOnScreen(workAreaPx, dpiScale);
                }
            }
            finally
            {
                _isAdjusting = false;
            }
        }

        // --- Helpers ---

        private void SetWindowSizeForced(IntPtr hwnd, int w, int h)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x0016);
            _pendingWpfUpdate = true;
            Dispatcher.BeginInvoke(() => UpdateWpfProperties(), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void EnsureTopLeftOnScreen(NativeMethods.RECT workArea, double dpiScale)
        {
            double monitorLeftDip = workArea.Left / dpiScale;
            double monitorTopDip = workArea.Top / dpiScale;

            if (Left < monitorLeftDip) Left = monitorLeftDip;
            if (Top < monitorTopDip) Top = monitorTopDip;
        }

        private void UpdateWpfProperties()
        {
            if (!_pendingWpfUpdate) return;
            _pendingWpfUpdate = false;

            var hwnd = new WindowInteropHelper(this).Handle;
            var (_, currentSizeDip) = GetWindowRect(hwnd, NativeMethods.GetDpiForWindow(hwnd) / 96.0);

            if (Math.Abs(Width - currentSizeDip.Width) > 1.0 || Math.Abs(Height - currentSizeDip.Height) > 1.0)
            {
                _ignoreNextSizeChange = true;
                Width = currentSizeDip.Width;
                Height = currentSizeDip.Height;
            }
        }

        private Size GetPreferredSizeForMonitor(string deviceName)
        {
            if (_monitorSizeCache.TryGetValue(deviceName, out Size cachedSize))
            {
                return cachedSize;
            }
            return _originalSize;
        }

        // Helper to get string name from handle
        private string GetMonitorName(IntPtr hMonitor)
        {
            var mi = new NativeMethods.MONITORINFOEX();
            if (NativeMethods.GetMonitorInfoW(hMonitor, ref mi))
            {
                return mi.szDevice;
            }
            return "Unknown";
        }

        private (NativeMethods.RECT WorkArea, double DpiScale) GetMonitorSettings(IntPtr monitor)
        {
            var mi = new NativeMethods.MONITORINFO();
            NativeMethods.GetMonitorInfoW(monitor, ref mi);

            uint x, y;
            try { NativeMethods.GetDpiForMonitor(monitor, 0, out x, out y); }
            catch { x = 96; }

            return (mi.rcWork, x / 96.0);
        }

        private (NativeMethods.RECT RectPx, Size SizeDip) GetWindowRect(IntPtr hwnd, double dpiScale)
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            return (r, new Size((r.Right - r.Left) / dpiScale, (r.Bottom - r.Top) / dpiScale));
        }

        private void LogDebug(string msg) => Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] DpiWindow: {msg}");

        // --- Native Methods Region ---

        private static class NativeMethods
        {
            public const int MONITOR_DEFAULTTONEAREST = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public uint cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
                public MONITORINFO() { cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO)); rcMonitor = new RECT(); rcWork = new RECT(); dwFlags = 0; }
            }

            // Updated struct to include Device Name for persistence
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            public struct MONITORINFOEX
            {
                public int cbSize;
                public RECT rcMonitor;
                public RECT rcWork;
                public uint dwFlags;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string szDevice;

                public MONITORINFOEX()
                {
                    cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    rcMonitor = new RECT();
                    rcWork = new RECT();
                    dwFlags = 0;
                    szDevice = string.Empty;
                }
            }

            [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
            [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, int dwFlags);
            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);

            // Overloaded to support both basic and EX info
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

            [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
            [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            public static IntPtr GetMonitorFromWindow(IntPtr hwnd) => MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        }
    }
}