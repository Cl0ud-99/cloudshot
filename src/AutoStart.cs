using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Cloudshot
{
    /// <summary>
    /// 开机自启：优先写 HKCU 的 Run 键；被组策略 / 安全软件拒绝时，退回「启动」文件夹快捷方式。
    /// 两种机制都会被 IsEnabled 识别，关闭时一并清理。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Cloudshot";

        public static string LastError { get; private set; } = string.Empty;

        public static string StartupShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Cloudshot.lnk");
            }
        }

        public static bool IsEnabled
        {
            get { return HasRunValue() || File.Exists(StartupShortcutPath); }
        }

        public static string Mechanism
        {
            get
            {
                if (HasRunValue()) return "注册表启动项";
                if (File.Exists(StartupShortcutPath)) return "启动文件夹快捷方式";
                return "未启用";
            }
        }

        public static bool Enable()
        {
            LastError = string.Empty;

            string registryError;
            if (TryWriteRunKey(out registryError)) return true;

            string shortcutError;
            if (TryCreateShortcut(out shortcutError)) return true;

            LastError = "注册表写入被拒绝（" + Shorten(registryError) + "）；" +
                        "启动文件夹写入被拒绝（" + Shorten(shortcutError) + "）。" +
                        Environment.NewLine + Environment.NewLine +
                        "如果 Cloudshot.exe 目前放在受保护/沙箱目录（例如某些开发工作区），" +
                        "请先把它复制到普通文件夹（桌面、C:\\Tools 等）再试。";
            return false;
        }

        public static bool Disable()
        {
            LastError = string.Empty;
            bool ok = true;

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                ok = false;
                LastError = ex.Message;
            }

            try
            {
                if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
            }
            catch (Exception ex)
            {
                ok = false;
                LastError = ex.Message;
            }

            return ok;
        }

        public static void OpenStartupFolder()
        {
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "shell:startup");
            }
            catch
            {
            }
        }

        private static bool HasRunValue()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    var value = key.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(value);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryWriteRunKey(out string error)
        {
            error = string.Empty;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        error = "无法打开 Run 键";
                        return false;
                    }
                    var exe = Assembly.GetExecutingAssembly().Location;
                    key.SetValue(RunValueName, "\"" + exe + "\"", RegistryValueKind.String);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 在「启动」文件夹里创建 .lnk（通过 WScript.Shell，无需额外引用）。
        /// </summary>
        private static bool TryCreateShortcut(out string error)
        {
            error = string.Empty;
            object shell = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    error = "系统未注册 WScript.Shell";
                    return false;
                }

                shell = Activator.CreateInstance(shellType);
                var linkPath = StartupShortcutPath;
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                    null, shell, new object[] { linkPath });
                if (shortcut == null)
                {
                    error = "CreateShortcut 返回空";
                    return false;
                }

                var exe = Assembly.GetExecutingAssembly().Location;
                var type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut,
                    new object[] { Path.GetDirectoryName(exe) ?? string.Empty });
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                    new object[] { exe + ",0" });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut,
                    new object[] { "Cloudshot 轻截图" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

                return File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                return false;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
        }

        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message)) return "未知原因";
            var index = message.LastIndexOf(": ", StringComparison.Ordinal);
            var text = index >= 0 ? message.Substring(index + 2) : message;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length > 120 ? text.Substring(0, 120) + "…" : text;
        }
    }
}
