using System;
using System.IO;
using System.Windows;

namespace Cloudshot
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool openSettings = Array.Exists(args, delegate(string a)
            {
                return string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase);
            });
            bool captureNow = Array.Exists(args, delegate(string a)
            {
                return string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase);
            });

            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "Cloudshot.SingleInstance.Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行：让它打开设置或直接截图
                    var name = openSettings ? "Cloudshot.ShowSettings" : "Cloudshot.CaptureNow";
                    var message = NativeMethods.RegisterWindowMessage(name);
                    NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, message, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                var app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };

                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Error("未处理的界面异常", e.Exception);
                    e.Handled = true;
                };

                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    Log.Error("未处理的异常", e.ExceptionObject as Exception);
                };

                bool diag = Array.Exists(args, delegate(string a)
                {
                    return string.Equals(a, "--diag", StringComparison.OrdinalIgnoreCase);
                });

                string autoStartArg = Array.Find(args, delegate(string a)
                {
                    return a.StartsWith("--autostart-", StringComparison.OrdinalIgnoreCase);
                });

                if (diag) WriteDiagnostics();

                if (args.Length >= 2 && string.Equals(args[0], "--translate-test", StringComparison.OrdinalIgnoreCase))
                {
                    RunTranslateTest(args[1]);
                    return;
                }

                if (args.Length >= 3 && string.Equals(args[0], "--set-youdao", StringComparison.OrdinalIgnoreCase))
                {
                    Settings.Load();
                    Settings.YoudaoAppKey = args[1].Trim();
                    Settings.YoudaoSecret = args[2].Trim();
                    Settings.TranslateProvider = "youdao";
                    Settings.Save();
                    WriteResult("youdao credentials saved");
                    return;
                }

                if (autoStartArg != null)
                {
                    bool enable = autoStartArg.EndsWith("-on", StringComparison.OrdinalIgnoreCase);
                    bool ok = Settings.SetAutoStart(enable);
                    WriteResult(string.Format("autostart {0}: ok={1} mechanism={2} error={3}",
                        enable ? "on" : "off", ok, AutoStart.Mechanism,
                        AutoStart.LastError.Replace("\r", " ").Replace("\n", " ")));
                    return;
                }

                Settings.Load();
                TrayController.Instance.Initialize();

                if (openSettings)
                {
                    app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(delegate { TrayController.Instance.ShowSettings(); }));
                }
                else if (captureNow)
                {
                    app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(delegate { TrayController.Instance.Capture(); }));
                }

                app.Run();
            }
        }

        private static void WriteResult(string text)
        {
            try
            {
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                File.AppendAllText(Path.Combine(dir, "autostart-result.txt"),
                    DateTime.Now.ToString("HH:mm:ss") + " " + text + Environment.NewLine);
            }
            catch
            {
            }
        }

        private static void RunTranslateTest(string imagePath)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                Settings.Load();
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(imagePath);
                image.EndInit();
                image.Freeze();

                var ocr = Ocr.Recognize(image);
                sb.AppendLine("OCR [" + ocr.LanguageTag + "] " + ocr.ElapsedMs + " ms  pixels=" + image.PixelWidth + "x" + image.PixelHeight + " fmt=" + image.Format);
                sb.AppendLine("diag: " + Ocr.Diagnostics);
                sb.AppendLine(ocr.Text);
                sb.AppendLine();

                var tr = Translator.Translate(ocr.Text, ocr.IsChinese);
                sb.AppendLine("Translate via " + tr.Provider + " in " + tr.ElapsedMs + " ms, ok=" + tr.Ok);
                sb.AppendLine(tr.Ok ? tr.Text : tr.Error);
            }
            catch (Exception ex)
            {
                sb.AppendLine("FAILED: " + ex);
            }

            var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            File.WriteAllText(Path.Combine(dir, "translate-test.txt"), sb.ToString(), new System.Text.UTF8Encoding(false));
        }

        private static void WriteDiagnostics()        {
            var lines = new System.Text.StringBuilder();
            try
            {
                lines.AppendLine("user=" + System.Security.Principal.WindowsIdentity.GetCurrent().Name);
                lines.AppendLine("hku=" + Microsoft.Win32.Registry.CurrentUser.Name);
                lines.AppendLine("appdata=" + Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                lines.AppendLine("exe=" + System.Reflection.Assembly.GetExecutingAssembly().Location);
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run"))
                    {
                        key.SetValue("CloudshotDiag", "1");
                    }
                    lines.AppendLine("reg Run write: ok");
                }
                catch (Exception ex)
                {
                    lines.AppendLine("reg Run write failed: " + ex.GetType().Name + " " + ex.Message);
                }
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\CloudshotDiagTest"))
                    {
                        key.SetValue("Probe", "1");
                    }
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\CloudshotDiagTest", false);
                    lines.AppendLine("reg own-key write: ok");
                }
                catch (Exception ex)
                {
                    lines.AppendLine("reg own-key write failed: " + ex.GetType().Name + " " + ex.Message);
                }
                try
                {
                    var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run", false);
                    lines.AppendLine("Run key exists: " + (runKey != null));
                    if (runKey != null) runKey.Close();
                }
                catch (Exception ex)
                {
                    lines.AppendLine("Run key probe failed: " + ex.Message);
                }
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    var probe = Path.Combine(appData, "cloudshot-probe.txt");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    lines.AppendLine("appdata write: ok");
                }
                catch (Exception ex)
                {
                    lines.AppendLine("appdata write failed: " + ex.GetType().Name + " " + ex.Message);
                }
                try
                {
                    var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                    lines.AppendLine("startup folder: " + startup);
                    var probe = Path.Combine(startup, "cloudshot-probe.txt");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    lines.AppendLine("startup write: ok");
                }
                catch (Exception ex)
                {
                    lines.AppendLine("startup write failed: " + ex.GetType().Name + " " + ex.Message);
                }
                try
                {
                    var probe = Path.Combine(Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "settings-probe.txt");
                    File.WriteAllText(probe, "probe");
                    File.Delete(probe);
                    lines.AppendLine("exe-dir write: ok");
                }
                catch (Exception ex)
                {
                    lines.AppendLine("exe-dir write failed: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                lines.AppendLine("diag failed: " + ex.Message);
            }

            try
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".", "diag.txt"),
                    lines.ToString());
            }
            catch
            {
            }
        }
    }
}
