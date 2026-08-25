using System;
using System.Runtime.InteropServices;

namespace TraderPen.Overlay
{
    // Encapsula as chamadas Win32 necessárias para alternar entre
    // "clique atravessa" (Mouse Mode) e "clique é capturado" (Drawing Mode).
    internal static class NativeMethods
    {
        private const int GWL_EXSTYLE = -20;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// Torna a janela click-through. A camada da barra pode ser reativada
        /// temporariamente quando o cursor estiver sobre ela.
        public static void EnableClickThrough(IntPtr hWnd)
        {
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        }

        /// Faz a janela capturar cliques normalmente (modo desenho).
        public static void DisableClickThrough(IntPtr hWnd)
        {
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, (exStyle | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out ScreenPoint point);

        public static System.Windows.Point GetCursorPosition()
        {
            GetCursorPos(out var point);
            return new System.Windows.Point(point.X, point.Y);
        }
    }
}