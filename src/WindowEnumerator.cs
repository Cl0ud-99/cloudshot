using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cloudshot
{
    internal struct WindowHit
    {
        public IntPtr Handle;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public string Title;
        public string ClassName;
    }

    /// <summary>
    /// 截图时枚举一次顶层窗口（EnumWindows 本身按 Z 序返回），之后用矩形命中测试即可，
    /// 因为遮罩铺满屏幕、画面已经冻结，窗口不会在截图过程中移动。
    /// </summary>
    internal static class WindowEnumerator
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static List<WindowHit> Collect()
        {
            var result = new List<WindowHit>(64);
            uint ownPid = (uint)Process.GetCurrentProcess().Id;

            EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                try
                {
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid == ownPid) return true;

                    int cloaked;
                    if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                    {
                        return true;
                    }

                    var className = new StringBuilder(128);
                    GetClassName(hwnd, className, className.Capacity);
                    var cls = className.ToString();
                    if (IsExcluded(cls)) return true;

                    RECT rect;
                    if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect,
                            Marshal.SizeOf(typeof(RECT))) != 0)
                    {
                        if (!GetWindowRect(hwnd, out rect)) return true;
                    }

                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width < 8 || height < 8) return true;

                    var title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);

                    result.Add(new WindowHit
                    {
                        Handle = hwnd,
                        X = rect.Left,
                        Y = rect.Top,
                        Width = width,
                        Height = height,
                        Title = title.ToString(),
                        ClassName = cls
                    });
                }
                catch
                {
                }
                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static bool IsExcluded(string className)
        {
            // 任务栏不参与识别；桌面（Progman/WorkerW）保留，相当于“整屏”
            return className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd";
        }
    }
}
