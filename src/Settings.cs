using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Cloudshot
{
    /// <summary>
    /// 极简配置：%APPDATA%\Cloudshot\settings.ini
    /// </summary>
    internal static class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "Cloudshot";

        public static uint HotkeyModifiers = NativeMethods.MOD_ALT;
        public static uint HotkeyKey = 0x41; // A

        /// <summary>翻译服务商：youdao / mymemory</summary>
        public static string TranslateProvider = "youdao";
        public static string YoudaoAppKey = string.Empty;
        public static string YoudaoSecret = string.Empty;

        private static string Dir
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cloudshot");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(Dir, "settings.ini"); }
        }

        public static bool Exists
        {
            get { return File.Exists(FilePath); }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var value = line.Substring(idx + 1).Trim();
                    if (key.Equals("hotkey_modifiers", StringComparison.OrdinalIgnoreCase))
                    {
                        uint mods;
                        if (uint.TryParse(value, out mods) && mods > 0) HotkeyModifiers = mods;
                    }
                    else if (key.Equals("hotkey_key", StringComparison.OrdinalIgnoreCase))
                    {
                        uint vk;
                        if (uint.TryParse(value, out vk) && vk > 0) HotkeyKey = vk;
                    }
                    else if (key.Equals("translate_provider", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(value)) TranslateProvider = value;
                    }
                    else if (key.Equals("youdao_appkey_enc", StringComparison.OrdinalIgnoreCase))
                    {
                        YoudaoAppKey = Unprotect(value);
                    }
                    else if (key.Equals("youdao_secret_enc", StringComparison.OrdinalIgnoreCase))
                    {
                        YoudaoSecret = Unprotect(value);
                    }
                    else if (key.Equals("auto_text_mode", StringComparison.OrdinalIgnoreCase))
                    {
                        // 旧配置项，已不再使用（取字改为手动触发）
                    }
                }
            }
            catch
            {
                // 配置损坏时回退默认值，不影响启动
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Cloudshot settings");
                sb.AppendLine("hotkey_modifiers=" + HotkeyModifiers);
                sb.AppendLine("hotkey_key=" + HotkeyKey);
                sb.AppendLine("translate_provider=" + TranslateProvider);
                sb.AppendLine("youdao_appkey_enc=" + Protect(YoudaoAppKey));
                sb.AppendLine("youdao_secret_enc=" + Protect(YoudaoSecret));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public static string HotkeyText
        {
            get
            {
                var sb = new StringBuilder();
                if ((HotkeyModifiers & NativeMethods.MOD_CONTROL) != 0) sb.Append("Ctrl + ");
                if ((HotkeyModifiers & NativeMethods.MOD_ALT) != 0) sb.Append("Alt + ");
                if ((HotkeyModifiers & NativeMethods.MOD_SHIFT) != 0) sb.Append("Shift + ");
                if ((HotkeyModifiers & NativeMethods.MOD_WIN) != 0) sb.Append("Win + ");
                sb.Append(KeyName(HotkeyKey));
                return sb.ToString();
            }
        }

        public static string KeyName(uint vk)
        {
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk);
            switch (key)
            {
                case System.Windows.Input.Key.Return: return "Enter";
                case System.Windows.Input.Key.Escape: return "Esc";
                case System.Windows.Input.Key.Back: return "Backspace";
                case System.Windows.Input.Key.Space: return "Space";
                case System.Windows.Input.Key.PrintScreen: return "PrintScreen";
                case System.Windows.Input.Key.OemPlus: return "=";
                case System.Windows.Input.Key.OemMinus: return "-";
                case System.Windows.Input.Key.OemComma: return ",";
                case System.Windows.Input.Key.OemPeriod: return ".";
                case System.Windows.Input.Key.OemQuestion: return "/";
                case System.Windows.Input.Key.OemSemicolon: return ";";
                case System.Windows.Input.Key.OemQuotes: return "'";
                case System.Windows.Input.Key.OemOpenBrackets: return "[";
                case System.Windows.Input.Key.OemCloseBrackets: return "]";
                case System.Windows.Input.Key.OemPipe: return "\\";
                case System.Windows.Input.Key.OemTilde: return "`";
            }
            return key.ToString();
        }

        public static bool IsAutoStartEnabled()
        {
            return AutoStart.IsEnabled;
        }

        public static bool SetAutoStart(bool enabled)
        {
            if (enabled)
            {
                bool ok = AutoStart.Enable();
                LastAutoStartError = AutoStart.LastError;
                if (!ok) Log.Error("设置开机自启失败", new InvalidOperationException(AutoStart.LastError));
                return ok;
            }

            bool removed = AutoStart.Disable();
            LastAutoStartError = AutoStart.LastError;
            return removed;
        }

        public static string LastAutoStartError = string.Empty;

        /// <summary>用当前用户的 DPAPI 加密，避免密钥以明文躺在配置文件里。</summary>
        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            try
            {
                var bytes = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Unprotect(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return string.Empty;
            try
            {
                var bytes = Convert.FromBase64String(encoded);
                var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
