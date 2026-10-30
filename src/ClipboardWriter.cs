using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cloudshot
{
    /// <summary>
    /// 剪贴板写入 + 专用后台 STA 线程。
    ///
    /// 背景（2026-10 排查「点击完成后卡一下」）：
    ///   1. .NET 的 <c>Clipboard.SetDataObject</c> 遇到 CLIPBRD_E_CANT_OPEN 会在内部反复重试，
    ///      单次调用在剪贴板被占用时实测要 ~970ms（原始 OpenClipboard 则是 0ms 立即失败）。
    ///      旧实现在 UI 线程上循环 6 次，最坏卡满 6 秒 —— 与日志里 6273ms 的尖峰完全吻合。
    ///   2. 抢占剪贴板的多为输入法（本机是微信输入法 wetype_server），它会在剪贴板内容变化后
    ///      立即 OpenClipboard 读取并长时间持有，所以表现为「有时候卡一下」。
    ///
    /// 对策：
    ///   1. 用 <c>OpenClipboard</c> 做真实可写性探测（锁定时 0ms 失败），避开 SetDataObject 的 1 秒重试；
    ///   2. 合成与写入都放到后台 STA 线程，UI 线程不再等待；
    ///   3. 该后台线程必须跑 Dispatcher 消息泵 —— 实测裸 STA 线程会让 OLE 剪贴板失败并泄漏锁；
    ///   4. 统一用 copy:true（OleFlushClipboard），数据固化进系统剪贴板，不依赖本进程存活。
    /// </summary>
    internal static class ClipboardWriter
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        /// <summary>图片写入的总预算：够一次正常写入 + 一次重试，但远小于旧实现的 6 秒。</summary>
        public const int ImageBudgetMs = 1200;

        /// <summary>文本写入预算：取色/复制译文要即时反馈，宁可失败也不拖住界面。</summary>
        public const int TextBudgetMs = 150;

        private const int ProbeIntervalMs = 20;

        /// <summary>
        /// 一次正常写入的耗时上限（实测 130-370ms）。
        /// 剩余预算低于这个值就没必要再试，避免「试到一半超支近 1 秒」。
        /// </summary>
        private const int NormalWriteMs = 450;

        private static readonly object Gate = new object();
        private static Dispatcher _dispatcher;
        private static Thread _worker;
        private static int _pending;

        /// <summary>最近一次写入失败的原因，用于日志诊断。</summary>
        public static string LastError = string.Empty;

        /// <summary>当前排队中的任务数（诊断用）。</summary>
        public static int PendingCount
        {
            get { return Volatile.Read(ref _pending); }
        }

        /// <summary>
        /// 真正试一下能不能拿到剪贴板锁（拿到立刻释放）。
        /// OpenClipboard 在锁被占时是 0ms 立即失败，不像 SetDataObject 会内部重试约 1 秒，
        /// 所以可以放心用它做探测。
        /// </summary>
        public static bool IsClipboardAvailable()
        {
            var handle = OpenClipboard(IntPtr.Zero);
            if (handle == IntPtr.Zero) return false;
            CloseClipboard();
            return true;
        }

        #region 后台线程

        /// <summary>
        /// 把一个任务排到后台 STA 线程执行（合成图片、写剪贴板都用它）。
        /// 立即返回，不阻塞调用方。
        /// </summary>
        public static void RunOnWorker(Action job)
        {
            if (job == null) return;
            if (!EnsureWorker()) return;

            Interlocked.Increment(ref _pending);
            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                {
                    try
                    {
                        job();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("后台剪贴板任务失败", ex);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }));
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _pending);
                Log.Error("排入剪贴板任务失败", ex);
            }
        }

        private static bool EnsureWorker()
        {
            if (_dispatcher != null) return true;

            lock (Gate)
            {
                if (_dispatcher != null) return true;

                var ready = new ManualResetEventSlim(false);

                // 关键：这个线程必须跑 Dispatcher 消息泵。
                // 实测（2026-10）——裸 STA 线程没有消息泵时，OLE 剪贴板操作会失败，
                // 而且失败后会「泄漏」剪贴板锁（GetOpenClipboardWindow 持续返回非零），
                // 之后所有写入全部卡死。加 Dispatcher.Run() 后完全正常。
                var thread = new Thread(delegate()
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;

                    // 预热：本线程第一次碰 OLE 剪贴板要付出一次性初始化开销
                    // （实测未预热时首次 SetDataObject 要 5 秒+，预热后 200-350ms）。
                    // 放在线程启动时做，避免第一次真正截图时踩到。
                    WarmUp();

                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "Cloudshot.Clipboard"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _worker = thread;

                ready.Wait(3000);
                return _dispatcher != null;
            }
        }

        /// <summary>
        /// 在当前 STA 线程上预热 OLE 剪贴板。
        /// 只做一次「打开-关闭」，不写任何数据，不会影响用户剪贴板内容。
        /// </summary>
        private static void WarmUp()
        {
            try
            {
                // 只碰 OpenClipboard 不够：真正贵的是 SetDataObject 首次调用时
                // OLE 在后台线程上的初始化（实测 5 秒+）。这里完整跑一次，
                // 把这份一次性开销挪到程序启动、用户还没截图的时候。
                // 用空文本，不会破坏用户剪贴板里的内容（随后就被覆盖也无所谓，
                // 因为预热发生在启动时，用户还没开始用）。
                var handle = OpenClipboard(IntPtr.Zero);
                if (handle != IntPtr.Zero) CloseClipboard();

                var data = new DataObject();
                data.SetData(DataFormats.UnicodeText, string.Empty);
                Clipboard.SetDataObject(data, false);
            }
            catch
            {
            }
        }

        /// <summary>等待排队的写入完成（退出程序前调用，避免刚截完就退出导致图片丢失）。</summary>
        public static bool WaitForIdle(int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (Volatile.Read(ref _pending) > 0)
            {
                if (watch.ElapsedMilliseconds >= timeoutMs) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        #endregion

        #region 写入

        /// <summary>
        /// 把图片写入剪贴板。必须在带消息泵的 STA 线程调用（后台 worker 就是）。
        ///
        /// 预算控制：SetDataObject 撞上占用时单次约 970ms 才抛异常，
        /// 所以不能只看「总耗时是否超预算」——那样最后一次尝试总能超支近 1 秒。
        /// 这里用 NormalWriteMs 兜底：剩余预算不够一次正常写入就不再试，
        /// 最坏被限制在约 1 次超时（~970ms），而不是旧实现的 6 次（~6 秒）。
        /// </summary>
        public static bool WriteImage(BitmapSource image, int budgetMs)
        {
            if (image == null) return false;

            var watch = Stopwatch.StartNew();

            while (true)
            {
                if (IsClipboardAvailable())
                {
                    try
                    {
                        var data = new DataObject();
                        data.SetImage(image);
                        Clipboard.SetDataObject(data, true);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                long used = watch.ElapsedMilliseconds;
                if (used >= budgetMs) return false;
                if (budgetMs - used < NormalWriteMs) return false;

                Thread.Sleep(ProbeIntervalMs);
            }
        }

        /// <summary>
        /// 同步写文本（取色值、复制译文）。
        /// 预算很短（150ms）：剪贴板被占用时宁可快速失败，让调用方用 toast 提示，
        /// 也不把 UI 线程拖住。
        /// </summary>
        public static bool TryWriteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            var watch = Stopwatch.StartNew();

            while (true)
            {
                if (IsClipboardAvailable())
                {
                    try
                    {
                        var data = new DataObject();
                        data.SetData(DataFormats.UnicodeText, text);
                        data.SetData(DataFormats.Text, text);
                        Clipboard.SetDataObject(data, true);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                if (watch.ElapsedMilliseconds >= TextBudgetMs) return false;
                Thread.Sleep(10);
            }
        }

        #endregion
    }
}
