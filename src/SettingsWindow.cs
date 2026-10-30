using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cloudshot
{
    internal sealed class SettingsWindow : Window
    {
        private readonly TextBlock _hotkeyText;
        private readonly Border _hotkeyBox;
        private readonly TextBlock _status;
        private bool _recording;

        public SettingsWindow()
        {
            Title = "Cloudshot 设置";
            Width = 430;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF7, 0xF9));
            FontFamily = new FontFamily("Microsoft YaHei UI");
            ShowInTaskbar = true;
            Topmost = true;

            var root = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };

            root.Children.Add(new TextBlock
            {
                Text = "Cloudshot 轻截图",
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1D, 0x21))
            });
            root.Children.Add(new TextBlock
            {
                Text = "常驻托盘，按快捷键即可在任意窗口上框选截图，确认后直接进剪贴板。",
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 18),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
            });

            root.Children.Add(SectionLabel("截图快捷键"));

            _hotkeyText = new TextBlock
            {
                Text = Settings.HotkeyText,
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _hotkeyBox = new Border
            {
                Height = 42,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE2)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = _hotkeyText
            };
            _hotkeyBox.MouseLeftButtonDown += delegate { BeginRecording(); };
            root.Children.Add(_hotkeyBox);

            root.Children.Add(new TextBlock
            {
                Text = "点击上面的方框，然后按下新的组合键（需含 Ctrl / Alt / Shift / Win，或使用 F1–F12）。Esc 取消。",
                FontSize = 11.5,
                Margin = new Thickness(0, 6, 0, 16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9C))
            });

            root.Children.Add(SectionLabel("翻译服务"));

            var providerBox = new ComboBox
            {
                Height = 28,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            };
            providerBox.Items.Add("有道翻译（推荐，需要 App ID + 密钥）");
            providerBox.Items.Add("MyMemory（免 Key，质量一般）");
            providerBox.SelectedIndex = Settings.TranslateProvider == "mymemory" ? 1 : 0;
            root.Children.Add(providerBox);

            var appKeyBox = new TextBox
            {
                Height = 28,
                FontSize = 13,
                Text = Settings.YoudaoAppKey,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "有道智云 → 应用管理 → 我的应用 → 应用ID"
            };
            root.Children.Add(MakeField("App ID", appKeyBox));

            var secretBox = new PasswordBox
            {
                Height = 28,
                FontSize = 13,
                Password = Settings.YoudaoSecret,
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "有道智云 → 我的应用 → 应用密钥（保存时会用 Windows DPAPI 加密）"
            };
            root.Children.Add(MakeField("应用密钥", secretBox));

            var translateStatus = new TextBlock
            {
                Text = string.IsNullOrEmpty(Settings.YoudaoAppKey) ? "尚未配置有道凭据，将使用 MyMemory 免 Key 通道。" : "已配置有道凭据。",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9C))
            };
            root.Children.Add(translateStatus);

            var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 18) };
            var testButton = new Button
            {
                Content = "测试翻译",
                Width = 96,
                Height = 30
            };
            testButton.Click += delegate
            {
                Settings.TranslateProvider = providerBox.SelectedIndex == 1 ? "mymemory" : "youdao";
                Settings.YoudaoAppKey = appKeyBox.Text.Trim();
                Settings.YoudaoSecret = secretBox.Password.Trim();
                Settings.Save();
                translateStatus.Text = "正在测试…";
                var key = Settings.YoudaoAppKey;
                var secret = Settings.YoudaoSecret;
                var provider = Settings.TranslateProvider;

                System.Threading.Tasks.Task.Run(delegate
                {
                    var result = Translator.Translate("Hello, this is a connection test.", false);
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        translateStatus.Text = result.Ok
                            ? "连接正常（" + result.Provider + "，" + result.ElapsedMs + " ms）：" + result.Text
                            : "连接失败：" + result.Error;
                    }));
                });
            };
            testRow.Children.Add(testButton);
            root.Children.Add(testRow);

            root.Children.Add(SectionLabel("启动"));

            var autoStart = new CheckBox
            {
                Content = "开机自动启动",
                IsChecked = Settings.IsAutoStartEnabled(),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 16)
            };
            Action refreshAutoStartLabel = delegate
            {
                autoStart.Content = "开机自动启动（当前：" + AutoStart.Mechanism + "）";
            };
            refreshAutoStartLabel();

            autoStart.Checked += delegate
            {
                if (!Settings.SetAutoStart(true))
                {
                    autoStart.IsChecked = false;
                    refreshAutoStartLabel();
                    var answer = MessageBox.Show(
                        "开机自启设置失败：" + Environment.NewLine + Environment.NewLine +
                        Settings.LastAutoStartError + Environment.NewLine + Environment.NewLine +
                        "是否打开「启动」文件夹，手动放一个 Cloudshot.exe 的快捷方式？",
                        "Cloudshot", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer == MessageBoxResult.Yes) AutoStart.OpenStartupFolder();
                    return;
                }
                refreshAutoStartLabel();
            };
            autoStart.Unchecked += delegate
            {
                Settings.SetAutoStart(false);
                refreshAutoStartLabel();
            };
            root.Children.Add(autoStart);

            _status = new TextBlock
            {
                Text = TrayController.Instance.HotkeyAvailable
                    ? "热键已生效，随时可以截图。"
                    : "当前快捷键注册失败，请换一个组合键。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 0, 0, 16)
            };
            root.Children.Add(_status);

            var close = new Button
            {
                Content = "关闭",
                Width = 84,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = true
            };
            close.Click += delegate { Close(); };
            root.Children.Add(close);

            Content = root;

            PreviewKeyDown += OnPreviewKeyDown;
            Closed += delegate
            {
                Settings.TranslateProvider = providerBox.SelectedIndex == 1 ? "mymemory" : "youdao";
                Settings.YoudaoAppKey = appKeyBox.Text.Trim();
                Settings.YoudaoSecret = secretBox.Password.Trim();
                Settings.Save();
            };
        }

        private static StackPanel MakeField(string label, FrameworkElement input)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                Margin = new Thickness(0, 0, 0, 3)
            });
            panel.Children.Add(input);
            return panel;
        }

        private static TextBlock SectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x51, 0x5C)),
                Margin = new Thickness(0, 0, 0, 6)
            };
        }

        private void BeginRecording()
        {
            _recording = true;
            _hotkeyText.Text = "请按下快捷键…";
            _hotkeyText.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            _hotkeyBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
            Keyboard.Focus(this);
            Focus();
        }

        private void EndRecording(string message)
        {
            _recording = false;
            _hotkeyText.Text = Settings.HotkeyText;
            _hotkeyText.Foreground = Brushes.Black;
            _hotkeyBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE2));
            if (message != null) _status.Text = message;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_recording) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;

            if (key == Key.Escape)
            {
                EndRecording(null);
                return;
            }

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            var modifiers = Keyboard.Modifiers;
            uint mods = 0;
            if ((modifiers & ModifierKeys.Control) != 0) mods |= NativeMethods.MOD_CONTROL;
            if ((modifiers & ModifierKeys.Alt) != 0) mods |= NativeMethods.MOD_ALT;
            if ((modifiers & ModifierKeys.Shift) != 0) mods |= NativeMethods.MOD_SHIFT;
            if ((modifiers & ModifierKeys.Windows) != 0) mods |= NativeMethods.MOD_WIN;

            bool functionKey = key >= Key.F1 && key <= Key.F24;
            if (mods == 0 && !functionKey)
            {
                _status.Text = "至少需要一个修饰键（Ctrl / Alt / Shift / Win）。";
                _hotkeyText.Text = "再按一次…";
                return;
            }

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0)
            {
                return;
            }

            uint oldMods = Settings.HotkeyModifiers;
            uint oldKey = Settings.HotkeyKey;
            Settings.HotkeyModifiers = mods;
            Settings.HotkeyKey = vk;
            Settings.Save();

            if (!TrayController.Instance.RegisterHotkey())
            {
                Settings.HotkeyModifiers = oldMods;
                Settings.HotkeyKey = oldKey;
                Settings.Save();
                TrayController.Instance.RegisterHotkey();
                EndRecording("该组合键已被占用，已恢复原来的快捷键。");
                return;
            }

            EndRecording("已保存，新快捷键立刻生效。");
        }
    }
}
