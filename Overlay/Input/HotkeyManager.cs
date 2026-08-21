using System;
using System.Windows;
using System.Windows.Interop;

namespace TraderPen.Input
{
    // Registra um hotkey global (funciona mesmo com a janela em modo click-through,
    // já que RegisterHotKey não depende de foco de teclado).
    internal sealed class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID_TOGGLE_MODE = 9000;

        private readonly IntPtr _hwnd;
        private readonly HwndSource _source;
        public event Action? ToggleModeRequested;

        public HotkeyManager(Window window)
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(_hwnd)!;
            _source.AddHook(WndProc);

            // F9, sem modificadores (fsModifiers = 0), VK_F9 = 0x78
            Overlay.NativeMethods.RegisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MODE, 0, 0x78);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_TOGGLE_MODE)
            {
                ToggleModeRequested?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Overlay.NativeMethods.UnregisterHotKey(_hwnd, HOTKEY_ID_TOGGLE_MODE);
            _source.RemoveHook(WndProc);
        }
    }
}