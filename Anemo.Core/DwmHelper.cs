using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Anemo.Core
{
    // WindowStyle="None" gives a fully custom title bar, but it also means Windows 11's
    // automatic corner rounding and dark-mode chrome no longer apply on their own - only
    // DWM can restore those, via these attributes (22000+; silently no-ops on older builds).
    public static class DwmHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        public static void ApplyDarkRoundedStyling(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;

            int enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
    }
}
