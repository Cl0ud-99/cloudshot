using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cloudshot
{
    /// <summary>
    /// 右键取消后的短暂“输入缓冲”。
    /// 用低级鼠标钩子吞掉紧随其后的右键事件，不去改变窗口的可见性，
    /// 从而避免“把窗口设成透明再关”造成的黑屏闪烁。
    /// </summary>
    internal static class RightClickBuffer
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_RBUTTONDBLCLK = 0x0206;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static HookProc _proc;
        private static IntPtr _hook = IntPtr.Zero;
        private static int _deadline;
        private static Timer _timer;

        public static void Start(int milliseconds)
        {
            _deadline = Environment.TickCount + milliseconds;

            if (_hook == IntPtr.Zero)
            {
                _proc = HookCallback;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                Log.Info("right buffer: hook=" + (_hook != IntPtr.Zero));
            }

            if (_hook == IntPtr.Zero) return;

            if (_timer != null) _timer.Dispose();
            _timer = new Timer(delegate { Stop(); }, null, milliseconds + 80, Timeout.Infinite);
        }

        public static void Stop()
        {
            try
            {
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            }
            catch
            {
            }
            _hook = IntPtr.Zero;
            _proc = null;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = wParam.ToInt32();
                if (message == WM_RBUTTONDOWN || message == WM_RBUTTONUP || message == WM_RBUTTONDBLCLK)
                {
                    if (unchecked(Environment.TickCount - _deadline) <= 0)
                    {
                        return new IntPtr(1); // 吞掉
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
