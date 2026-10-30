using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Cloudshot
{
    /// <summary>
    /// 托盘常驻 + 全局热键 + 截图调度。
    /// </summary>
    internal sealed class TrayController
    {
        public static readonly TrayController Instance = new TrayController();

        private const int HotkeyId = 0xC10D;
        private const string BroadcastMessageName = "Cloudshot.CaptureNow";

        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _captureItem;
        private ToolStripMenuItem _autoStartItem;
        private HwndSource _messageWindow;
        private OverlayWindow _overlay;
        private uint _broadcastMessage;
        private uint _settingsMessage;
        private DispatcherTimer _trimTimer;

        public bool HotkeyAvailable { get; private set; } = true;

        public void Initialize()
        {
            CreateMessageWindow();
            CreateNotifyIcon();
            RegisterHotkey();

            if (!Settings.Exists)
            {
                ShowBalloon("Cloudshot 已启动",
                    "按 " + Settings.HotkeyText + " 截图，右键托盘图标可打开设置或退出。",
                    ToolTipIcon.Info);
                Settings.Save();
            }
        }

        #region 消息窗口 / 热键

        private void CreateMessageWindow()
        {
            var parameters = new HwndSourceParameters("Cloudshot.MessageWindow")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = IntPtr.Zero
            };
            _messageWindow = new HwndSource(parameters);
            _messageWindow.AddHook(WndProc);
            _broadcastMessage = NativeMethods.RegisterWindowMessage(BroadcastMessageName);
            _settingsMessage = NativeMethods.RegisterWindowMessage("Cloudshot.ShowSettings");
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Log.Info("WM_HOTKEY");
                Capture();
            }
            else if (_broadcastMessage != 0 && msg == (int)_broadcastMessage)
            {
                handled = true;
                Capture();
            }
            else if (_settingsMessage != 0 && msg == (int)_settingsMessage)
            {
                handled = true;
                ShowSettings();
            }
            return IntPtr.Zero;
        }

        public bool RegisterHotkey()
        {
            UnregisterHotkey();
            bool ok = NativeMethods.RegisterHotKey(
                _messageWindow.Handle,
                HotkeyId,
                Settings.HotkeyModifiers | NativeMethods.MOD_NOREPEAT,
                Settings.HotkeyKey);

            HotkeyAvailable = ok;
            UpdateMenuText();
            if (!ok)
            {
                ShowBalloon("快捷键被占用",
                    Settings.HotkeyText + " 已被其他程序注册，可在“设置”里换一个组合键。",
                    ToolTipIcon.Warning);
            }
            return ok;
        }

        private void UnregisterHotkey()
        {
            if (_messageWindow != null && _messageWindow.Handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            }
        }

        #endregion

        #region 托盘

        private void CreateNotifyIcon()
        {
            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Font = new Font("Microsoft YaHei UI", 9f);

            _captureItem = new ToolStripMenuItem("截图");
            _captureItem.Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
            _captureItem.Click += delegate { Capture(); };
            _menu.Items.Add(_captureItem);

            _menu.Items.Add(new ToolStripSeparator());

            var settingsItem = new ToolStripMenuItem("设置...");
            settingsItem.Click += delegate { ShowSettings(); };
            _menu.Items.Add(settingsItem);

            _autoStartItem = new ToolStripMenuItem("开机自动启动");
            _autoStartItem.Click += delegate { ToggleAutoStart(); };
            _menu.Items.Add(_autoStartItem);

            _menu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { Shutdown(); };
            _menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "Cloudshot 轻截图",
                Visible = true,
                ContextMenuStrip = _menu
            };
            _notifyIcon.DoubleClick += delegate { Capture(); };

            UpdateMenuText();
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                if (icon != null) return icon;
            }
            catch
            {
            }
            return SystemIcons.Application;
        }

        private void UpdateMenuText()
        {
            if (_captureItem != null)
            {
                _captureItem.Text = "截图    " + Settings.HotkeyText;
            }
            if (_autoStartItem != null)
            {
                _autoStartItem.Checked = Settings.IsAutoStartEnabled();
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = "Cloudshot 轻截图  ·  " + Settings.HotkeyText;
            }
        }

        private void ToggleAutoStart()
        {
            bool enable = !Settings.IsAutoStartEnabled();
            if (!Settings.SetAutoStart(enable))
            {
                ShowBalloon("开机自启设置失败", "点击托盘图标 →「设置...」查看原因。", ToolTipIcon.Warning);
                var answer = System.Windows.MessageBox.Show(
                    "开机自启设置失败：" + Environment.NewLine + Environment.NewLine +
                    Settings.LastAutoStartError + Environment.NewLine + Environment.NewLine +
                    "是否打开「启动」文件夹，手动放一个 Cloudshot.exe 的快捷方式？",
                    "Cloudshot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Yes) AutoStart.OpenStartupFolder();
            }
            UpdateMenuText();
        }

        public void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_notifyIcon == null) return;
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(4000);
        }

        #endregion

        #region 截图

        /// <summary>
        /// 闲置 90 秒后再把工作集换出去：连续截图时不用每次重新换页（唤起更快），
        /// 长时间挂机时内存又能回到几 MB。
        /// </summary>
        private void ScheduleIdleTrim()
        {
            if (_trimTimer == null)
            {
                _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
                _trimTimer.Tick += delegate
                {
                    _trimTimer.Stop();
                    if (_overlay != null) return;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    try { NativeMethods.EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); }
                    catch { }
                };
            }
            _trimTimer.Stop();
            _trimTimer.Start();
        }

        public void Capture()
        {
            Log.Info("Capture() overlay=" + (_overlay != null) + " trim=" + (_trimTimer != null && _trimTimer.IsEnabled));
            if (_trimTimer != null) _trimTimer.Stop();
            if (_overlay != null) return;

            bool menuWasOpen = _menu != null && _menu.Visible;
            if (menuWasOpen) _menu.Close();

            // 从托盘菜单点“截图”时才需要等菜单淡出；热键唤起时立刻抓，少 140ms 延迟
            if (!menuWasOpen)
            {
                DoCapture();
                return;
            }

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            timer.Tick += delegate
            {
                timer.Stop();
                DoCapture();
            };
            timer.Start();
        }

        private void DoCapture()
        {
            if (_overlay != null) return;
            try
            {
                // 防御：极少数情况下 _overlay 置空了但旧遮罩窗口还在，先清掉再开新的，避免叠层
                foreach (System.Windows.Window window in System.Windows.Application.Current.Windows)
                {
                    if (window is OverlayWindow && window.IsVisible) window.Close();
                }

                int left, top, width, height;
                var shot = ScreenCapture.CaptureVirtualScreen(out left, out top, out width, out height);

                var overlay = new OverlayWindow(shot, new Int32Rect(left, top, width, height));
                _overlay = overlay;
                Log.Info("DoCapture: overlay created");
                overlay.Closed += delegate
                {
                    Log.Info("overlay closed");
                    _overlay = null;
                    overlay.Content = null;
                    // 截图用的是整屏位图（4K 下 ~20MB/张）；等这一个 dispatcher 周期把窗口真正释放后再回收
                    overlay.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        ScheduleIdleTrim();
                    }));
                };
                overlay.Show();
            }
            catch (Exception ex)
            {
                _overlay = null;
                Log.Error("截图失败", ex);
                ShowBalloon("截图失败", ex.Message, ToolTipIcon.Error);
            }
        }

        #endregion

        #region 设置 / 退出

        public void ShowSettings()
        {
            var window = new SettingsWindow();
            window.ShowDialog();
            UpdateMenuText();
        }

        public void Shutdown()
        {
            // 刚截完的图可能还在后台线程往剪贴板里写，退出前给它一点时间，
            // 否则用户「截完立刻退出」会丢图。
            ClipboardWriter.WaitForIdle(1500);
            UnregisterHotkey();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            if (_messageWindow != null)
            {
                _messageWindow.Dispose();
                _messageWindow = null;
            }
            System.Windows.Application.Current.Shutdown();
        }

        #endregion
    }

    internal static class Log
    {
        public static void Info(string message)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cloudshot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "debug.log");
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 256 * 1024) File.Delete(file);
                File.AppendAllText(file,
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        public static void Error(string message, Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cloudshot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}{3}{2}", DateTime.Now, message,
                        Environment.NewLine, ex));
            }
            catch
            {
            }
        }
    }
}
