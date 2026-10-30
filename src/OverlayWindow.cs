using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Cloudshot
{
    /// <summary>
    /// 全屏截图覆盖层：先抓图、再显示，因此永远不会把自己拍进去。
    /// 所有绘制坐标都使用物理像素（通过 1/scale 的 LayoutTransform 实现）。
    /// </summary>
    internal sealed class OverlayWindow : Window
    {
        private const double HandleSize = 8;

        private readonly BitmapSource _shot;
        private readonly Int32Rect _screen;
        private double _scale = 1.0;

        private Canvas _pixelRoot;
        private Canvas _uiRoot;
        private Image _bgImage;
        private Rectangle _dimFull;
        private Rectangle _dimTop;
        private Rectangle _dimBottom;
        private Rectangle _dimLeft;
        private Rectangle _dimRight;
        private Canvas _selCanvas;
        private AnnotationLayer _layer;
        private Rectangle _border;
        private Canvas _handleLayer;
        private readonly List<Rectangle> _handles = new List<Rectangle>(8);
        private Border _toolbar;
        private Border _sizeBadge;
        private TextBlock _sizeText;
        private TextBlock _hint;
        private TextBox _textEditor;

        private bool _hasSelection;
        private Rect _sel;
        private Point _anchor;
        private bool _selecting;
        private bool _moving;
        private bool _resizing;
        private int _resizeHandle = -1;
        private Rect _selAtDragStart;
        private Point _dragOrigin;

        private ToolKind _tool = ToolKind.None;
        private Annotation _preview;
        private bool _annotating;
        private Point _annotStart;
        private readonly Stack<Annotation> _redo = new Stack<Annotation>();
        private readonly Dictionary<ToolKind, Border> _toolButtons = new Dictionary<ToolKind, Border>();
        private readonly List<Border> _swatches = new List<Border>();
        private Color _color = Color.FromRgb(0xF5, 0x2C, 0x2C);
        private bool _closing;
        private Size _toolbarSize;
        private Size _sizeBadgeSize;

        private Point _pendingPoint;
        private bool _movePending;

        private const double LoupeSize = 128;
        private const int LoupeSourcePixels = 14;

        private Border _loupe;
        private Canvas _loupeCanvas;
        private ImageBrush _loupeBrush;
        private Rectangle _loupeCrossV;
        private Rectangle _loupeCrossH;
        private TextBlock _loupeText;
        private Brush _loupeCrossBrush;
        private readonly byte[] _pixel = new byte[4];

        private readonly List<WindowHit> _windows;
        private Rectangle _hoverBorder;
        private bool _hoverValid;
        private Rect _hoverRect;
        private bool _pendingClick;
        private bool _pressedWindowValid;
        private Rect _pressedWindow;
        private Point _pressPoint;
        private int _openedTick;
        private const int OpenGuardMs = 120;

        private Border _toast;
        private TextBlock _toastText;
        private System.Windows.Threading.DispatcherTimer _toastTimer;

        private Border _translatePanel;
        private TextBlock _tpStatus;
        private TextBlock _tpOriginal;
        private TextBlock _tpTranslated;
        private Border _tpCopyButton;
        private Border _tpCloseButton;
        private string _tpTranslatedText = string.Empty;
        private bool _translating;

        private Canvas _textLayer;
        private List<Ocr.WordBox> _textWords;
        private bool _textMode;
        private bool _textBusy;
        private bool _textDragging;
        private int _textSelStart = -1;
        private int _textSelEnd = -1;
        private double _ocrScale = 2.0;

        private double _textFontSize = 24;
        private TextBlock _fontSizeText;
        private TextAnnotation _dragText;
        private Point _dragTextStart;
        private Point _dragTextOrigin;

        public OverlayWindow(BitmapSource shot, Int32Rect screen)
        {
            _shot = shot;
            _screen = screen;

            // 画面已经冻结，窗口位置不会变，枚举一次即可
            _windows = WindowEnumerator.Collect();

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            Background = Brushes.Black;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;
            UseLayoutRounding = false;
            SnapsToDevicePixels = false;
            Left = 0;
            Top = 0;
            Width = 200;
            Height = 200;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            int dpi = NativeMethods.GetDpiForWindow(hwnd);
            _scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _openedTick = Environment.TickCount;

            // 不出现在 Alt+Tab 列表里
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                new IntPtr(exStyle.ToInt64() | NativeMethods.WS_EX_TOOLWINDOW));

            BuildUi();
            LayoutWindowToScreen(hwnd);

            // 布局完成后强制拿到前台与键盘焦点，保证 Esc / Enter 立刻可用
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(delegate
            {
                ForceForeground(hwnd);
                _pixelRoot.Focus();
                Keyboard.Focus(_pixelRoot);

                // 进入截图时先识别光标下已有的窗口，用户不动鼠标直接点也能截
                NativeMethods.POINT cursor;
                if (NativeMethods.GetCursorPos(out cursor))
                {
                    UpdateHover(new Point(cursor.X - _screen.X, cursor.Y - _screen.Y));
                }
            }));
        }

        private static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == hwnd) return;

                uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, IntPtr.Zero);
                uint currentThread = NativeMethods.GetCurrentThreadId();
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
                    NativeMethods.SetForegroundWindow(hwnd);
                    NativeMethods.SetFocus(hwnd);
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                }
                else
                {
                    NativeMethods.SetForegroundWindow(hwnd);
                    NativeMethods.SetFocus(hwnd);
                }
            }
            catch
            {
            }
        }

        private void LayoutWindowToScreen(IntPtr hwnd)
        {
            Left = _screen.X / _scale;
            Top = _screen.Y / _scale;
            Width = _screen.Width / _scale;
            Height = _screen.Height / _scale;

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                _screen.X, _screen.Y, _screen.Width, _screen.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            LayoutHint();
        }

        #region UI 构建

        private void BuildUi()
        {
            var grid = new Grid { Background = Brushes.Black };

            _pixelRoot = new Canvas
            {
                Width = _screen.Width,
                Height = _screen.Height,
                Background = Brushes.Black,
                Focusable = true,
                LayoutTransform = new ScaleTransform(1.0 / _scale, 1.0 / _scale)
            };
            _pixelRoot.MouseLeftButtonDown += OnPixelMouseDown;
            _pixelRoot.MouseMove += OnPixelMouseMove;
            _pixelRoot.MouseLeftButtonUp += OnPixelMouseUp;
            _pixelRoot.MouseRightButtonDown += OnRightButtonDown;
            _pixelRoot.MouseRightButtonUp += OnRightButtonUp;
            _pixelRoot.MouseLeave += OnPixelMouseLeave;

            _bgImage = new Image
            {
                Source = _shot,
                Width = _screen.Width,
                Height = _screen.Height,
                Stretch = Stretch.Fill
            };
            RenderOptions.SetBitmapScalingMode(_bgImage, BitmapScalingMode.NearestNeighbor);
            _pixelRoot.Children.Add(_bgImage);

            // 压暗层：全屏一块 + 选区四周四块（用矩形拼出镂空，比每帧重建 CombinedGeometry 便宜得多）
            var dimBrush = new SolidColorBrush(Color.FromArgb(0x96, 0x00, 0x00, 0x00));
            dimBrush.Freeze();

            _dimFull = new Rectangle
            {
                Fill = dimBrush,
                Width = _screen.Width,
                Height = _screen.Height,
                IsHitTestVisible = false
            };
            _pixelRoot.Children.Add(_dimFull);

            _dimTop = CreateDimRect(dimBrush);
            _dimBottom = CreateDimRect(dimBrush);
            _dimLeft = CreateDimRect(dimBrush);
            _dimRight = CreateDimRect(dimBrush);
            _pixelRoot.Children.Add(_dimTop);
            _pixelRoot.Children.Add(_dimBottom);
            _pixelRoot.Children.Add(_dimLeft);
            _pixelRoot.Children.Add(_dimRight);

            _selCanvas = new Canvas
            {
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                ClipToBounds = true
            };

            _layer = new AnnotationLayer();
            _selCanvas.Children.Add(_layer);

            _textLayer = new Canvas { IsHitTestVisible = false };
            _selCanvas.Children.Add(_textLayer);
            _pixelRoot.Children.Add(_selCanvas);

            _hoverBorder = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5)),
                StrokeThickness = 2,
                Fill = null,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _pixelRoot.Children.Add(_hoverBorder);

            _border = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5)),
                StrokeThickness = 1.5,
                Fill = null,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _pixelRoot.Children.Add(_border);

            _handleLayer = new Canvas { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            for (int i = 0; i < 8; i++)
            {
                var handle = new Rectangle
                {
                    Width = 9,
                    Height = 9,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5)),
                    StrokeThickness = 1.2,
                    RadiusX = 1.5,
                    RadiusY = 1.5
                };
                _handles.Add(handle);
                _handleLayer.Children.Add(handle);
            }
            _pixelRoot.Children.Add(_handleLayer);

            _uiRoot = new Canvas { Background = null };

            // 工具栏/提示条上也支持右键取消（这些元素在 _pixelRoot 之上，事件不会冒泡到它）
            _uiRoot.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            _uiRoot.MouseRightButtonUp += OnRightButtonUp;

            _sizeBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x16, 0x16)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _sizeText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            _sizeBadge.Child = _sizeText;
            _uiRoot.Children.Add(_sizeBadge);

            _hint = new TextBlock
            {
                Text = "单击窗口截图  ·  拖动框选  ·  1–6 标注  ·  C 取色  ·  O 取字  ·  T 翻译  ·  Enter 确认  ·  Esc 取消",
                Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                FontSize = 13,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            _uiRoot.Children.Add(_hint);

            _toastText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            _toast = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Child = _toastText,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _uiRoot.Children.Add(_toast);

            _toolbar = BuildToolbar();
            _toolbar.Visibility = Visibility.Collapsed;
            _toolbar.MouseEnter += delegate { HideLoupe(); };
            _uiRoot.Children.Add(_toolbar);

            BuildLoupe();
            BuildTranslatePanel();

            grid.Children.Add(_pixelRoot);
            grid.Children.Add(_uiRoot);
            Content = grid;

            UpdateDim();
            LayoutHint();
        }

        /// <summary>
        /// 取色放大镜：用 ImageBrush 的 Viewbox 直接放大整屏截图的局部，
        /// 不产生任何新位图，拖动时只是改一个矩形区域，开销可以忽略。
        /// </summary>
        private void BuildLoupe()
        {
            _loupeBrush = new ImageBrush(_shot)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, LoupeSourcePixels, LoupeSourcePixels)
            };

            _loupeCrossBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0x3B, 0x30));
            _loupeCrossBrush.Freeze();

            var zoom = new Rectangle
            {
                Width = LoupeSize,
                Height = LoupeSize,
                Fill = _loupeBrush
            };
            RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);

            _loupeCanvas = new Canvas { Width = LoupeSize, Height = LoupeSize };
            _loupeCanvas.Children.Add(zoom);

            _loupeCrossV = new Rectangle
            {
                Width = 1,
                Height = LoupeSize,
                Fill = _loupeCrossBrush
            };
            _loupeCrossH = new Rectangle
            {
                Width = LoupeSize,
                Height = 1,
                Fill = _loupeCrossBrush
            };
            _loupeCanvas.Children.Add(_loupeCrossV);
            _loupeCanvas.Children.Add(_loupeCrossH);

            _loupeText = new TextBlock
            {
                Text = "0000, 0000   #FFFFFF",
                Foreground = Brushes.White,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };

            var panel = new StackPanel();
            panel.Children.Add(_loupeCanvas);
            panel.Children.Add(_loupeText);

            _loupe = new Border
            {
                Width = LoupeSize + 8,
                Height = LoupeSize + 28,
                Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0E, 0x0E, 0x10)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3),
                Child = panel,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(_loupe, BitmapScalingMode.NearestNeighbor);
            _uiRoot.Children.Add(_loupe);
        }

        private void HideLoupe()
        {
            if (_loupe != null && _loupe.Visibility != Visibility.Collapsed)
            {
                _loupe.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateLoupe(Point p)
        {
            if (_loupe == null || _closing) return;
            if (_textMode || _annotating || _textEditor != null || _dragText != null)
            {
                HideLoupe();
                return;
            }

            double px = Math.Floor(p.X);
            double py = Math.Floor(p.Y);
            if (px < 0 || py < 0 || px >= _screen.Width || py >= _screen.Height)
            {
                HideLoupe();
                return;
            }

            const double src = LoupeSourcePixels;
            double half = src / 2.0;
            double vx = Math.Max(0, Math.Min(_screen.Width - src, px + 0.5 - half));
            double vy = Math.Max(0, Math.Min(_screen.Height - src, py + 0.5 - half));
            _loupeBrush.Viewbox = new Rect(vx, vy, src, src);

            Canvas.SetLeft(_loupeCrossV, Math.Round((px + 0.5 - vx) / src * LoupeSize));
            Canvas.SetTop(_loupeCrossH, Math.Round((py + 0.5 - vy) / src * LoupeSize));

            int ix = (int)px;
            int iy = (int)py;
            try
            {
                _shot.CopyPixels(new Int32Rect(ix, iy, 1, 1), _pixel, 4, 0);
            }
            catch
            {
            }
            byte b = _pixel[0], g = _pixel[1], r = _pixel[2];
            _loupeText.Text = string.Format("{0}, {1}   #{2:X2}{3:X2}{4:X2}", ix, iy, r, g, b);

            double screenW = _screen.Width / _scale;
            double screenH = _screen.Height / _scale;
            double w = _loupe.Width;
            double h = _loupe.Height;

            double left = px / _scale + 20;
            double top = py / _scale + 20;
            if (left + w > screenW - 2) left = px / _scale - w - 20;
            if (top + h > screenH - 2) top = py / _scale - h - 20;

            left = Math.Max(2, Math.Min(screenW - w - 2, left));
            top = Math.Max(2, Math.Min(screenH - h - 2, top));

            Canvas.SetLeft(_loupe, left);
            Canvas.SetTop(_loupe, top);
            _loupe.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 把光标所在像素的颜色以 #RRGGBB 复制到剪贴板，并给一个短暂提示；遮罩不关闭，可以连续取色。
        /// </summary>
        private void CopyColorUnderCursor()
        {
            NativeMethods.POINT point;
            if (!NativeMethods.GetCursorPos(out point)) return;

            int x = point.X - _screen.X;
            int y = point.Y - _screen.Y;
            if (x < 0 || y < 0 || x >= _screen.Width || y >= _screen.Height) return;

            var pixel = new byte[4];
            try
            {
                _shot.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            }
            catch
            {
                return;
            }

            string hex = string.Format("#{0:X2}{1:X2}{2:X2}", pixel[2], pixel[1], pixel[0]);
            UpdateLoupe(new Point(x, y));

            // 复制失败要说一声：旧实现静默 return，用户会以为键没生效
            ShowToast(CopyTextToClipboard(hex) ? "已复制 " + hex : "剪贴板被占用，取色未复制");
        }

        private static string LastClipboardError = string.Empty;

        /// <summary>
        /// 复制纯文本（取色值、复制译文）。
        ///
        /// 改动说明：旧实现用 copy:false + 静默吞掉 Flush 失败，数据靠本进程持有，
        /// 既不可靠（flush 失败时其他程序可能粘贴不到），又会在被占用时反复重试。
        /// 现在统一走 ClipboardWriter：先用 GetOpenClipboardWindow 廉价探测，
        /// 空闲才写入，且用 copy:true 把数据固化进系统剪贴板；总预算 150ms，
        /// 抢不到就立刻返回 false，让调用方给出提示而不是拖住界面。
        /// </summary>
        private static bool CopyTextToClipboard(string text)
        {
            if (ClipboardWriter.TryWriteText(text)) return true;
            LastClipboardError = ClipboardWriter.LastError;
            return false;
        }
        private void ShowToast(string text)
        {
            if (_toast == null) return;

            _toastText.Text = text;
            _toast.Visibility = Visibility.Visible;
            _toast.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double toastW = _toast.DesiredSize.Width;
            double toastH = _toast.DesiredSize.Height;

            double screenW = _screen.Width / _scale;
            double screenH = _screen.Height / _scale;

            NativeMethods.POINT point;
            NativeMethods.GetCursorPos(out point);
            double cursorLeft = (point.X - _screen.X) / _scale;
            double cursorTop = (point.Y - _screen.Y) / _scale;

            // 默认贴在放大镜下方；放大镜没显示时贴光标右下方
            double left = cursorLeft + 20;
            double top = cursorTop + 20;

            if (_loupe != null && _loupe.Visibility == Visibility.Visible)
            {
                double loupeLeft = Canvas.GetLeft(_loupe);
                double loupeTop = Canvas.GetTop(_loupe);
                if (!double.IsNaN(loupeLeft) && !double.IsNaN(loupeTop))
                {
                    left = loupeLeft;
                    top = loupeTop + _loupe.Height + 6;
                }
            }

            if (double.IsNaN(left) || double.IsNaN(top))
            {
                left = cursorLeft + 20;
                top = cursorTop + 20;
            }

            left = Math.Max(2, Math.Min(screenW - toastW - 2, left));
            top = Math.Max(2, Math.Min(screenH - toastH - 2, top));

            Canvas.SetLeft(_toast, left);
            Canvas.SetTop(_toast, top);
            _toast.Visibility = Visibility.Visible;

            if (_toastTimer == null)
            {
                _toastTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1300)
                };
                _toastTimer.Tick += delegate
                {
                    _toastTimer.Stop();
                    if (_toast != null) _toast.Visibility = Visibility.Collapsed;
                };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        #region 翻译面板

        private void BuildTranslatePanel()
        {
            _tpStatus = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x91, 0x9C)),
                FontSize = 11,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var title = new TextBlock
            {
                Text = "翻译",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(title);
            titleRow.Children.Add(_tpStatus);

            _tpOriginal = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xAE, 0xB8)),
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 64,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 8, 0, 6)
            };

            _tpTranslated = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220
            };

            var divider = new Rectangle
            {
                Height = 1,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 2, 0, 8)
            };

            _tpCopyButton = MakePanelButton("复制译文", delegate { CopyTranslated(); });
            _tpCloseButton = MakePanelButton("关闭", delegate { CloseTranslatePanel(); });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttons.Children.Add(_tpCopyButton);
            buttons.Children.Add(_tpCloseButton);

            var stack = new StackPanel();
            stack.Children.Add(titleRow);
            stack.Children.Add(_tpOriginal);
            stack.Children.Add(divider);
            stack.Children.Add(_tpTranslated);
            stack.Children.Add(buttons);

            _translatePanel = new Border
            {
                Width = 430,
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1A, 0x1A, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(14, 12, 14, 12),
                Child = stack,
                Visibility = Visibility.Collapsed
            };
            _uiRoot.Children.Add(_translatePanel);
        }

        private Border MakePanelButton(string text, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            var button = new Border
            {
                Child = label,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand
            };
            button.MouseEnter += delegate { button.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)); };
            button.MouseLeave += delegate { button.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); };
            button.MouseLeftButtonUp += delegate { if (onClick != null) onClick(); };
            return button;
        }

        /// <summary>
        /// 进入「框选文字」模式：OCR 识别选区，把每个词的坐标铺在原图上，
        /// 用户可以像选文本一样拖动选择任意片段，Ctrl+C 复制。
        /// </summary>
        private void EnterTextMode(bool silent = false)
        {
            if (_closing || _textBusy) return;

            if (!_hasSelection)
            {
                ShowToast("先框选要识别文字的区域");
                return;
            }

            if (_textMode)
            {
                ExitTextMode();
                ShowToast("已退出取字模式");
                return;
            }

            int x = (int)Math.Round(_sel.X);
            int y = (int)Math.Round(_sel.Y);
            int w = Math.Max(1, (int)Math.Round(_sel.Width));
            int h = Math.Max(1, (int)Math.Round(_sel.Height));

            BitmapSource crop;
            try
            {
                var bitmap = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
                bitmap.Freeze();
                crop = bitmap;
            }
            catch
            {
                ShowToast("截取选区失败");
                return;
            }

            _textBusy = true;
            if (!silent) ShowToast("正在识别文字（" + _ocrScale + "×）…");
            HideLoupe();

            var scale = _ocrScale;
            System.Threading.Tasks.Task.Run(delegate
            {
                Ocr.Result ocr = null;
                try
                {
                    ocr = Ocr.Recognize(crop, scale);
                }
                catch
                {
                }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _textBusy = false;
                    if (_closing) return;

                    if (ocr == null || ocr.Words.Count == 0)
                    {
                        if (!silent) ShowToast("未识别到文字");
                        return;
                    }

                    _textMode = true;
                    _textWords = ocr.Words;
                    _textSelStart = -1;
                    _textSelEnd = -1;
                    RenderTextHighlights();
                    Cursor = Cursors.IBeam;
                    if (!silent) ShowToast("拖动选字 · Ctrl+C 复制 · 当前 " + _ocrScale + "×（+/- 调整）");
                }));
            });
        }

        private void ExitTextMode()
        {
            _textMode = false;
            _textWords = null;
            _textDragging = false;
            _textSelStart = -1;
            _textSelEnd = -1;
            if (_textLayer != null) _textLayer.Children.Clear();
            if (!_closing) Cursor = Cursors.Cross;
        }

        private void RenderTextHighlights()
        {
            if (_textLayer == null) return;
            _textLayer.Children.Clear();
            if (_textWords == null || _textSelStart < 0) return;

            int from = Math.Min(_textSelStart, _textSelEnd);
            int to = Math.Max(_textSelStart, _textSelEnd);
            for (int i = from; i <= to && i < _textWords.Count; i++)
            {
                var word = _textWords[i];
                var rect = new Rectangle
                {
                    Width = Math.Max(1, word.W),
                    Height = Math.Max(1, word.H),
                    Fill = new SolidColorBrush(Color.FromArgb(0xA0, 0x3F, 0xA9, 0xF5)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, word.X);
                Canvas.SetTop(rect, word.Y);
                _textLayer.Children.Add(rect);
            }
        }

        /// <summary>
        /// 找到光标命中的词；没命中就取最近的词（拖出选区也能继续选）。
        /// </summary>
        private int NearestWordIndex(Point p)
        {
            if (_textWords == null || _textWords.Count == 0) return -1;

            for (int i = 0; i < _textWords.Count; i++)
            {
                var w = _textWords[i];
                if (p.X >= w.X - 2 && p.X <= w.X + w.W + 2 && p.Y >= w.Y - 2 && p.Y <= w.Y + w.H + 2)
                {
                    return i;
                }
            }

            int best = -1;
            double bestDistance = double.MaxValue;
            for (int i = 0; i < _textWords.Count; i++)
            {
                var w = _textWords[i];
                double cx = w.X + w.W / 2;
                double cy = w.Y + w.H / 2;
                double distance = (cx - p.X) * (cx - p.X) + (cy - p.Y) * (cy - p.Y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return bestDistance > 160000 ? -1 : best; // 超过 400px 视为没选中
        }

        private void CopySelectedWords()
        {
            if (!_textMode || _textWords == null || _textSelStart < 0) return;

            int from = Math.Min(_textSelStart, _textSelEnd);
            int to = Math.Max(_textSelStart, _textSelEnd);
            var sb = new StringBuilder();

            for (int i = from; i <= to && i < _textWords.Count; i++)
            {
                var word = _textWords[i];
                if (sb.Length > 0)
                {
                    if (word.NewLine)
                    {
                        sb.Append('\n');
                    }
                    else if (NeedsSpace(sb[sb.Length - 1], word.Text[0]))
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(word.Text);
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0) return;

            if (CopyTextToClipboard(text))
            {
                ShowToast("已复制 " + text.Length + " 字");
            }
            else
            {
                ShowToast("复制失败，剪贴板被占用");
            }
        }

        private static bool IsCjkChar(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) ||
                   (c >= 0x3040 && c <= 0x30FF);
        }

        private static bool IsCjkPunctuation(char c)
        {
            return c == '\u3002' || c == '\uFF0C' || c == '\uFF01' || c == '\uFF1F' ||
                   c == '\uFF1B' || c == '\uFF1A' || c == '\u201C' || c == '\u201D' ||
                   c == '\u3001' || c == '\uFF08' || c == '\uFF09' || c == '\u2026';
        }

        /// <summary>中文之间、以及中文标点两侧都不需要空格。</summary>
        private static bool NeedsSpace(char prev, char next)
        {
            if (IsCjkPunctuation(prev) || IsCjkPunctuation(next)) return false;
            if (IsCjkChar(prev) && IsCjkChar(next)) return false;
            return true;
        }

        private static string LanguageName(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "未知语言";
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "中文";
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "English";
            if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "日本語";
            if (tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "한국어";
            if (tag.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "Français";
            if (tag.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return "Deutsch";
            if (tag.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "Español";
            if (tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "Русский";
            return tag;
        }

        private void TranslateSelection()
        {
            if (_closing || _translating) return;
            if (!_hasSelection)
            {
                ShowToast("先框选要翻译的文字");
                return;
            }

            int x = (int)Math.Round(_sel.X);
            int y = (int)Math.Round(_sel.Y);
            int w = Math.Max(1, (int)Math.Round(_sel.Width));
            int h = Math.Max(1, (int)Math.Round(_sel.Height));

            BitmapSource crop;
            try
            {
                var bitmap = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
                bitmap.Freeze();
                crop = bitmap;
            }
            catch
            {
                ShowToast("截取选区失败");
                return;
            }

            _translating = true;
            _tpTranslatedText = string.Empty;
            _tpStatus.Text = "正在识别…";
            _tpOriginal.Text = string.Empty;
            _tpTranslated.Text = "…";
            _tpCopyButton.Opacity = 0.35;
            _translatePanel.Visibility = Visibility.Visible;
            LayoutTranslatePanel();

            System.Threading.Tasks.Task.Run(delegate
            {
                Ocr.Result ocr = null;
                Translator.Result translation = null;
                try
                {
                    ocr = Ocr.Recognize(crop, _ocrScale);
                    if (!string.IsNullOrWhiteSpace(ocr.Text))
                    {
                        translation = Translator.Translate(ocr.Text, ocr.IsChinese);
                    }
                }
                catch (Exception ex)
                {
                    translation = new Translator.Result { Ok = false, Error = ex.Message };
                }

                Dispatcher.BeginInvoke(new Action(delegate { ApplyTranslation(ocr, translation); }));
            });
        }

        private void ApplyTranslation(Ocr.Result ocr, Translator.Result translation)
        {
            _translating = false;
            if (_closing) return;

            if (ocr == null || string.IsNullOrWhiteSpace(ocr.Text))
            {
                _tpStatus.Text = "未识别到文字";
                _tpOriginal.Text = string.Empty;
                _tpTranslated.Text = "可以把选区框大一点，或避开纯图形区域再试。";
                _tpCopyButton.Opacity = 0.35;
                LayoutTranslatePanel();
                return;
            }

            _tpOriginal.Text = ocr.Text;
            _tpStatus.Text = ocr.IsChinese ? "中 → 英" : "英 → 中";

            if (translation == null || !translation.Ok)
            {
                _tpTranslated.Text = translation != null ? translation.Error : "翻译失败";
                _tpStatus.Text += " · 失败";
                _tpCopyButton.Opacity = 0.35;
            }
            else
            {
                _tpTranslatedText = translation.Text;
                _tpTranslated.Text = translation.Text;
                _tpStatus.Text += " · " + translation.Provider + " · OCR " + ocr.ElapsedMs + " ms / 翻译 " + translation.ElapsedMs + " ms";
                _tpCopyButton.Opacity = 1.0;
            }
            LayoutTranslatePanel();
        }

        private void CopyTranslated()
        {
            if (string.IsNullOrEmpty(_tpTranslatedText)) return;
            ShowToast(CopyTextToClipboard(_tpTranslatedText) ? "已复制译文" : "剪贴板被占用，译文未复制");
        }

        private void CloseTranslatePanel()
        {
            if (_translatePanel != null) _translatePanel.Visibility = Visibility.Collapsed;
            _tpTranslatedText = string.Empty;
        }

        private void LayoutTranslatePanel()
        {
            if (_translatePanel == null || _translatePanel.Visibility != Visibility.Visible) return;

            _translatePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = _translatePanel.Width;
            double h = _translatePanel.DesiredSize.Height;
            double selLeft = _sel.X / _scale;
            double selTop = _sel.Y / _scale;
            double selW = _sel.Width / _scale;
            double selH = _sel.Height / _scale;
            double screenW = _screen.Width / _scale;
            double screenH = _screen.Height / _scale;

            double left = Math.Max(8, Math.Min(screenW - w - 8, selLeft + selW - w));
            double top = selTop + selH + 12;
            if (top + h > screenH - 8) top = selTop - h - 12;
            if (top < 8) top = Math.Max(8, screenH - h - 8);

            Canvas.SetLeft(_translatePanel, left);
            Canvas.SetTop(_translatePanel, top);
        }

        #endregion

        private Border BuildToolbar()
        {            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            AddTool(panel, ToolKind.Rect, "矩形", "拖拽绘制矩形框（快捷键 1）");
            AddTool(panel, ToolKind.Ellipse, "椭圆", "拖拽绘制椭圆（快捷键 2）");
            AddTool(panel, ToolKind.Arrow, "箭头", "从起点拖到终点（快捷键 3）");
            AddTool(panel, ToolKind.Pen, "画笔", "按住左键自由绘制（快捷键 4）");
            AddTool(panel, ToolKind.Text, "文字", "点击后在图上打字（快捷键 5）");
            AddTool(panel, ToolKind.Mosaic, "马赛克", "涂抹遮挡敏感信息（快捷键 6）");

            panel.Children.Add(Separator());

            AddCommand(panel, "取字", "识别选区文字，拖动选择后复制（快捷键 O）", delegate { EnterTextMode(); });
            AddCommand(panel, "翻译", "识别选区文字并翻译（快捷键 T）", delegate { TranslateSelection(); });

            // 撤销 / 重做只保留快捷键（Ctrl+Z / Ctrl+Y），不占工具栏
            panel.Children.Add(Separator());

            foreach (var color in new[]
            {
                Color.FromRgb(0xF5, 0x2C, 0x2C),
                Color.FromRgb(0xFF, 0x95, 0x00),
                Color.FromRgb(0xFF, 0xD6, 0x0A),
                Color.FromRgb(0x34, 0xC7, 0x59),
                Color.FromRgb(0x0A, 0x84, 0xFF),
                Color.FromRgb(0xFF, 0xFF, 0xFF)
            })
            {
                AddSwatch(panel, color);
            }

            panel.Children.Add(Separator());

            AddCommand(panel, "A−", "缩小文字字号", delegate { ChangeFontSize(-4); });
            _fontSizeText = new TextBlock
            {
                Text = ((int)_textFontSize).ToString(),
                Foreground = Brushes.White,
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "当前文字字号（px）"
            };
            panel.Children.Add(_fontSizeText);
            AddCommand(panel, "A+", "放大文字字号", delegate { ChangeFontSize(4); });

            panel.Children.Add(Separator());

            AddCommand(panel, "取消", "Esc", delegate { Cancel(); });
            AddCommand(panel, "完成", "Enter", delegate { Confirm(); });

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x1E)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 4, 5, 4),
                Child = panel,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.55,
                    Direction = 270
                }
            };
            return border;
        }

        private static Rectangle Separator()
        {
            return new Rectangle
            {
                Width = 1,
                Height = 18,
                Margin = new Thickness(5, 0, 5, 0),
                Fill = new SolidColorBrush(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private Border CreateButton(string text, string tooltip, Action onClick)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 12.5,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var button = new Border
            {
                Child = label,
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(1, 0, 1, 0),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = tooltip
            };
            button.MouseEnter += delegate
            {
                if (!(button.Tag is bool) || !(bool)button.Tag)
                {
                    button.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                }
            };
            button.MouseLeave += delegate
            {
                if (!(button.Tag is bool) || !(bool)button.Tag)
                {
                    button.Background = Brushes.Transparent;
                }
            };
            button.MouseLeftButtonUp += delegate
            {
                if (onClick != null) onClick();
            };
            return button;
        }

        private void AddTool(StackPanel panel, ToolKind kind, string text, string tooltip)
        {
            var button = CreateButton(text, tooltip, delegate { SelectTool(kind); });
            _toolButtons[kind] = button;
            panel.Children.Add(button);
        }

        private void AddCommand(StackPanel panel, string text, string tooltip, Action onClick)
        {
            panel.Children.Add(CreateButton(text, tooltip, onClick));
        }

        private void AddSwatch(StackPanel panel, Color color)
        {
            var dot = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = "标注颜色"
            };
            dot.MouseLeftButtonUp += delegate
            {
                _color = color;
                UpdateSwatches();
            };
            _swatches.Add(dot);
            panel.Children.Add(dot);
        }

        #endregion

        #region 选区

        private void OnPixelMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_closing) return;
            var p = e.GetPosition(_pixelRoot);
            _pixelRoot.CaptureMouse();

            if (_textEditor != null) CommitTextEditor();

            if (!_hasSelection)
            {
                // 先记下按下点：不移动就是“单击窗口截图”，移动了才转成拖拽框选
                _pressPoint = p;
                _pendingClick = true;
                _pressedWindowValid = _hoverValid;
                _pressedWindow = _hoverRect;
                return;
            }

            // 控制点最优先：不管开着什么工具、取字模式，拖白点都能改选区
            int handle = HitHandle(p);
            if (handle >= 0)
            {
                BeginResize(p, handle);
                return;
            }

            if (_textMode && _sel.Contains(p))
            {
                var local = new Point(p.X - _sel.X, p.Y - _sel.Y);
                int index = NearestWordIndex(local);
                if (index >= 0)
                {
                    _textDragging = true;
                    _textSelStart = index;
                    _textSelEnd = index;
                    RenderTextHighlights();
                    return;
                }
            }

            if (!_sel.Contains(p))
            {
                BeginSelect(p);
                return;
            }

            // 开着工具时，Ctrl + 拖动 = 整体移动选区
            if (_tool != ToolKind.None && Keyboard.Modifiers == ModifierKeys.Control)
            {
                BeginMove(p);
                return;
            }

            if (_tool == ToolKind.Text)
            {
                var local = new Point(p.X - _sel.X, p.Y - _sel.Y);
                var existing = _layer.HitTestText(local);
                if (existing != null)
                {
                    // 点中已有文字：拖动移动它
                    _dragText = existing;
                    _dragTextStart = local;
                    _dragTextOrigin = existing.Origin;
                    Cursor = Cursors.SizeAll;
                    return;
                }
                StartTextEditor(p, e);
                return;
            }

            if (_tool != ToolKind.None)
            {
                BeginAnnotate(p);
                return;
            }

            BeginMove(p);
        }

        private void OnPixelMouseMove(object sender, MouseEventArgs e)
        {
            // 鼠标事件可能远高于屏幕刷新率（1000Hz 鼠标），合并到每个渲染帧只处理一次
            _pendingPoint = e.GetPosition(_pixelRoot);
            if (_movePending) return;
            _movePending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                new Action(ApplyPendingMove));
        }

        private void OnPixelMouseLeave(object sender, MouseEventArgs e)
        {
            HideLoupe();
        }

        private void ApplyPendingMove()
        {
            _movePending = false;
            if (_closing) return;

            var p = _pendingPoint;
            UpdateLoupe(p);

            if (_textDragging)
            {
                var local = new Point(p.X - _sel.X, p.Y - _sel.Y);
                int index = NearestWordIndex(local);
                if (index >= 0 && index != _textSelEnd)
                {
                    _textSelEnd = index;
                    RenderTextHighlights();
                }
                return;
            }

            if (_dragText != null)
            {
                var local = new Point(p.X - _sel.X, p.Y - _sel.Y);
                _dragText.Origin = new Point(
                    _dragTextOrigin.X + (local.X - _dragTextStart.X),
                    _dragTextOrigin.Y + (local.Y - _dragTextStart.Y));
                _layer.Refresh();
                return;
            }

            if (_pendingClick)
            {
                if (Distance(p, _pressPoint) <= 4) return;

                _pendingClick = false;
                BeginSelect(_pressPoint);
                _sel = RectFromPoints(_anchor, p);
                UpdateSelectionVisuals();
                return;
            }

            if (_selecting)
            {
                _sel = RectFromPoints(_anchor, p);
                UpdateSelectionVisuals();
                return;
            }
            if (_moving)
            {
                double dx = p.X - _dragOrigin.X;
                double dy = p.Y - _dragOrigin.Y;
                _sel = new Rect(
                    Math.Max(0, Math.Min(_screen.Width - _selAtDragStart.Width, _selAtDragStart.X + dx)),
                    Math.Max(0, Math.Min(_screen.Height - _selAtDragStart.Height, _selAtDragStart.Y + dy)),
                    _selAtDragStart.Width,
                    _selAtDragStart.Height);
                UpdateSelectionVisuals();
                return;
            }
            if (_resizing)
            {
                _sel = RectFromResize(_selAtDragStart, _resizeHandle, p);
                UpdateSelectionVisuals();
                return;
            }
            if (_annotating)
            {
                UpdateAnnotation(p);
                return;
            }

            if (!_hasSelection) UpdateHover(p);
            UpdateCursor(p);
        }

        /// <summary>
        /// 命中测试当前光标下的顶层窗口（列表按 Z 序，取第一个命中的）。
        /// </summary>
        private void UpdateHover(Point p)
        {
            bool found = false;
            var rect = new Rect();

            if (_windows != null)
            {
                for (int i = 0; i < _windows.Count; i++)
                {
                    var w = _windows[i];
                    var r = new Rect(w.X, w.Y, w.Width, w.Height);
                    if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom)
                    {
                        rect = r;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                if (_hoverValid)
                {
                    _hoverValid = false;
                    UpdateSelectionVisuals();
                }
                return;
            }

            if (_hoverValid && Math.Abs(_hoverRect.X - rect.X) < 0.5 && Math.Abs(_hoverRect.Y - rect.Y) < 0.5 &&
                Math.Abs(_hoverRect.Width - rect.Width) < 0.5 && Math.Abs(_hoverRect.Height - rect.Height) < 0.5)
            {
                return;
            }

            _hoverRect = rect;
            _hoverValid = true;
            UpdateSelectionVisuals();
        }

        private void CaptureWindow(Rect rect)
        {
            Log.Info("close reason: window click");
            _hoverValid = false;
            _hoverBorder.Visibility = Visibility.Collapsed;
            _hasSelection = true;
            _sel = NormalizeSelection(rect);
            UpdateSelectionVisuals();
            Confirm();
        }

        private void OnPixelMouseUp(object sender, MouseButtonEventArgs e)
        {
            // 先把这一帧还没应用的移动补上，避免松手时选区差最后一帧
            if (_movePending) ApplyPendingMove();
            if (_pixelRoot.IsMouseCaptured) _pixelRoot.ReleaseMouseCapture();

            if (_textDragging)
            {
                _textDragging = false;
                return;
            }

            if (_dragText != null)
            {
                _dragText = null;
                UpdateCursor(e.GetPosition(_pixelRoot));
                return;
            }

            if (_pendingClick)
            {
                _pendingClick = false;
                if (_pressedWindowValid) CaptureWindow(_pressedWindow);
                return;
            }

            if (_selecting)
            {
                _selecting = false;
                if (_sel.Width < 4 || _sel.Height < 4)
                {
                    ClearSelection();
                }
                else
                {
                    _hasSelection = true;
                    UpdateToolbarState();
                    UpdateSelectionVisuals();
                }
                return;
            }
            if (_moving || _resizing)
            {
                _moving = false;
                _resizing = false;
                _resizeHandle = -1;
                UpdateCursor(e.GetPosition(_pixelRoot));
                return;
            }
            if (_annotating)
            {
                FinishAnnotation();
            }
        }

        private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 右键按下只吞掉事件，真正取消放到“松开”时做：
            // 否则窗口在按下瞬间就关了，紧随其后的右键松开会被背景窗口收到，弹出它的右键菜单
            e.Handled = true;
        }

        private void OnRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (Environment.TickCount - _openedTick < OpenGuardMs) return;
            if (_closing) return;
            Log.Info("close reason: right click");

            // 立即关闭（跟以前一样，不改透明度，避免黑屏闪烁）；
            // 之后 250ms 内的右键事件由低级鼠标钩子吞掉，不会漏到背景窗口
            RightClickBuffer.Start(250);
            Cancel();
        }

        private void BeginSelect(Point p)
        {
            _hoverValid = false;
            CloseTranslatePanel();
            ExitTextMode();
            ClearSelection();
            _selecting = true;
            _anchor = p;
            _sel = new Rect(p, new Size(0, 0));
            _hint.Visibility = Visibility.Collapsed;
            UpdateSelectionVisuals();
        }

        private void BeginMove(Point p)
        {
            ExitTextMode();
            _moving = true;
            _dragOrigin = p;
            _selAtDragStart = _sel;
            Cursor = Cursors.SizeAll;
        }

        private void BeginResize(Point p, int handle)
        {
            ExitTextMode();
            _resizing = true;
            _resizeHandle = handle;
            _selAtDragStart = _sel;
            _dragOrigin = p;
        }

        private Rect RectFromPoints(Point a, Point b)
        {
            double x1 = Math.Max(0, Math.Min(a.X, b.X));
            double y1 = Math.Max(0, Math.Min(a.Y, b.Y));
            double x2 = Math.Max(0, Math.Min(_screen.Width, Math.Max(a.X, b.X)));
            double y2 = Math.Max(0, Math.Min(_screen.Height, Math.Max(a.Y, b.Y)));
            return new Rect(x1, y1, Math.Max(1, x2 - x1), Math.Max(1, y2 - y1));
        }

        private Rect RectFromResize(Rect start, int handle, Point p)
        {
            double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
            double x = Math.Max(0, Math.Min(_screen.Width, p.X));
            double y = Math.Max(0, Math.Min(_screen.Height, p.Y));

            if (handle == 0 || handle == 3 || handle == 5) left = x;
            if (handle == 2 || handle == 4 || handle == 7) right = x;
            if (handle == 0 || handle == 1 || handle == 2) top = y;
            if (handle == 5 || handle == 6 || handle == 7) bottom = y;

            if (right < left) { double t = left; left = right; right = t; }
            if (bottom < top) { double t = top; top = bottom; bottom = t; }

            return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        private int HitHandle(Point p)
        {
            if (!_hasSelection) return -1;
            for (int i = 0; i < _handles.Count; i++)
            {
                double cx = Canvas.GetLeft(_handles[i]) + HandleSize / 2.0 + 0.5;
                double cy = Canvas.GetTop(_handles[i]) + HandleSize / 2.0 + 0.5;
                if (Math.Abs(p.X - cx) <= 8 && Math.Abs(p.Y - cy) <= 8) return i;
            }
            return -1;
        }

        private void ClearSelection()
        {
            _hasSelection = false;
            _selecting = false;
            _moving = false;
            _resizing = false;
            _preview = null;
            _annotating = false;
            _layer.Items.Clear();
            _redo.Clear();
            _layer.Mosaic = null;
            _layer.Refresh();
            CommitTextEditor();
            UpdateSelectionVisuals();
        }

        private void UpdateSelectionVisuals()
        {
            if (!_hasSelection && (!_selecting || _sel.Width < 1 || _sel.Height < 1))
            {
                _selCanvas.Visibility = Visibility.Collapsed;
                _border.Visibility = Visibility.Collapsed;
                _handleLayer.Visibility = Visibility.Collapsed;
                _toolbar.Visibility = Visibility.Collapsed;
                UpdateHoverVisuals();
                UpdateDim();
                return;
            }

            var sel = NormalizeSelection(_sel);
            _sel = sel;

            _hoverBorder.Visibility = Visibility.Collapsed;

            _selCanvas.Visibility = Visibility.Visible;
            Canvas.SetLeft(_selCanvas, sel.X);
            Canvas.SetTop(_selCanvas, sel.Y);
            _selCanvas.Width = sel.Width;
            _selCanvas.Height = sel.Height;

            int w = Math.Max(1, (int)Math.Round(sel.Width));
            int h = Math.Max(1, (int)Math.Round(sel.Height));

            if (Math.Abs(_layer.Area.Width - sel.Width) > 0.5 || Math.Abs(_layer.Area.Height - sel.Height) > 0.5)
            {
                _layer.Mosaic = null;
            }
            _layer.Area = new Size(sel.Width, sel.Height);
            _layer.Width = sel.Width;
            _layer.Height = sel.Height;
            if (_layer.Items.Count > 0) _layer.Refresh();

            _border.Visibility = Visibility.Visible;
            Canvas.SetLeft(_border, sel.X);
            Canvas.SetTop(_border, sel.Y);
            _border.Width = sel.Width;
            _border.Height = sel.Height;

            PositionHandles(sel);
            _handleLayer.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;

            string sizeText = string.Format("{0} × {1}", w, h);
            if (_sizeText.Text != sizeText) _sizeText.Text = sizeText;
            _sizeBadge.Visibility = Visibility.Visible;
            LayoutSizeBadge(sel);

            _hint.Visibility = Visibility.Collapsed;
            UpdateDim();

            if (_hasSelection)
            {
                _toolbar.Visibility = Visibility.Visible;
                LayoutToolbar(sel);
            }
        }

        private Rect NormalizeSelection(Rect r)
        {
            double x = Math.Max(0, Math.Min(_screen.Width, r.X));
            double y = Math.Max(0, Math.Min(_screen.Height, r.Y));
            double w = Math.Max(1, Math.Min(_screen.Width - x, r.Width));
            double h = Math.Max(1, Math.Min(_screen.Height - y, r.Height));
            return new Rect(x, y, w, h);
        }

        private void PositionHandles(Rect sel)
        {
            var xs = new[] { sel.Left, sel.Left + sel.Width / 2, sel.Right };
            var ys = new[] { sel.Top, sel.Top + sel.Height / 2, sel.Bottom };
            int index = 0;
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    if (row == 1 && col == 1) continue;
                    Canvas.SetLeft(_handles[index], xs[col] - HandleSize / 2);
                    Canvas.SetTop(_handles[index], ys[row] - HandleSize / 2);
                    index++;
                }
            }
        }

        private static Rectangle CreateDimRect(Brush brush)
        {
            return new Rectangle
            {
                Fill = brush,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
        }

        /// <summary>
        /// 用“整屏板 + 四块边”拼出选区镂空，拖动时只改 4 个矩形的位置尺寸，不产生任何几何对象。
        /// 没有选区时，镂空跟随当前悬停的窗口。
        /// </summary>
        private void UpdateDim()
        {
            Rect hole;
            bool hasHole = false;

            if (_hasSelection || (_selecting && _sel.Width >= 1 && _sel.Height >= 1))
            {
                hole = NormalizeSelection(_sel);
                hasHole = true;
            }
            else if (_hoverValid)
            {
                hole = _hoverRect;
                hasHole = true;
            }
            else
            {
                hole = new Rect(0, 0, 0, 0);
            }

            if (!hasHole)
            {
                _dimFull.Visibility = Visibility.Visible;
                _dimTop.Visibility = Visibility.Collapsed;
                _dimBottom.Visibility = Visibility.Collapsed;
                _dimLeft.Visibility = Visibility.Collapsed;
                _dimRight.Visibility = Visibility.Collapsed;
                return;
            }

            double x0 = hole.Left;
            double y0 = hole.Top;
            double x1 = hole.Right;
            double y1 = hole.Bottom;
            double w = _screen.Width;
            double h = _screen.Height;

            _dimFull.Visibility = Visibility.Collapsed;
            _dimTop.Visibility = Visibility.Visible;
            _dimBottom.Visibility = Visibility.Visible;
            _dimLeft.Visibility = Visibility.Visible;
            _dimRight.Visibility = Visibility.Visible;

            Place(_dimTop, 0, 0, w, y0);
            Place(_dimBottom, 0, y1, w, h - y1);
            Place(_dimLeft, 0, y0, x0, y1 - y0);
            Place(_dimRight, x1, y0, w - x1, y1 - y0);
        }

        private static void Place(Rectangle rect, double x, double y, double width, double height)
        {
            if (width < 0) width = 0;
            if (height < 0) height = 0;
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = width;
            rect.Height = height;
        }

        private void UpdateHoverVisuals()
        {
            if (_hoverValid && _hoverBorder != null)
            {
                _hoverBorder.Visibility = Visibility.Visible;
                Canvas.SetLeft(_hoverBorder, _hoverRect.X);
                Canvas.SetTop(_hoverBorder, _hoverRect.Y);
                _hoverBorder.Width = _hoverRect.Width;
                _hoverBorder.Height = _hoverRect.Height;

                int w = Math.Max(1, (int)Math.Round(_hoverRect.Width));
                int h = Math.Max(1, (int)Math.Round(_hoverRect.Height));
                string text = string.Format("{0} × {1}", w, h);
                if (_sizeText.Text != text) _sizeText.Text = text;
                _sizeBadge.Visibility = Visibility.Visible;
                LayoutSizeBadge(_hoverRect);
            }
            else
            {
                if (_hoverBorder != null) _hoverBorder.Visibility = Visibility.Collapsed;
                _sizeBadge.Visibility = Visibility.Collapsed;
            }

            _hint.Visibility = Visibility.Visible;
        }

        private void LayoutSizeBadge(Rect sel)
        {
            if (_sizeBadgeSize.Width <= 0)
            {
                _sizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _sizeBadgeSize = _sizeBadge.DesiredSize;
            }
            double w = _sizeBadgeSize.Width;
            double h = _sizeBadgeSize.Height;
            double left = sel.X / _scale;
            double top = sel.Y / _scale - h - 6;
            if (top < 2) top = sel.Y / _scale + 6;
            left = Math.Max(2, Math.Min(_screen.Width / _scale - w - 2, left));
            Canvas.SetLeft(_sizeBadge, left);
            Canvas.SetTop(_sizeBadge, top);
        }

        private void LayoutToolbar(Rect sel)
        {
            if (_toolbarSize.Width <= 0)
            {
                _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _toolbarSize = _toolbar.DesiredSize;
            }
            double w = _toolbarSize.Width;
            double h = _toolbarSize.Height;

            double selLeft = sel.X / _scale;
            double selTop = sel.Y / _scale;
            double selW = sel.Width / _scale;
            double selH = sel.Height / _scale;
            double screenW = _screen.Width / _scale;
            double screenH = _screen.Height / _scale;

            double left = selLeft + selW - w;
            double top = selTop + selH + 10;
            if (top + h > screenH - 4) top = selTop - h - 10;
            if (top < 4) top = selTop + selH - h - 10;

            left = Math.Max(4, Math.Min(screenW - w - 4, left));

            Canvas.SetLeft(_toolbar, left);
            Canvas.SetTop(_toolbar, top);
        }

        private void LayoutHint()
        {
            if (_hint == null) return;
            _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = _hint.DesiredSize.Width;
            double h = _hint.DesiredSize.Height;
            Canvas.SetLeft(_hint, Math.Max(0, (_screen.Width / _scale - w) / 2));
            Canvas.SetTop(_hint, _screen.Height / _scale - h - 42);
            _hint.Visibility = _hasSelection ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateCursor(Point p)
        {
            if (_textMode)
            {
                Cursor = Cursors.IBeam;
                return;
            }

            // 八个控制点始终可拖（哪怕正开着标注工具）
            if (_hasSelection)
            {
                switch (HitHandle(p))
                {
                    case 0:
                    case 7:
                        Cursor = Cursors.SizeNWSE;
                        return;
                    case 2:
                    case 5:
                        Cursor = Cursors.SizeNESW;
                        return;
                    case 1:
                    case 6:
                        Cursor = Cursors.SizeNS;
                        return;
                    case 3:
                    case 4:
                        Cursor = Cursors.SizeWE;
                        return;
                }
            }

            bool ctrlDown = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (_hasSelection && _tool == ToolKind.Text && !ctrlDown)
            {
                var local = new Point(p.X - _sel.X, p.Y - _sel.Y);
                Cursor = _layer.HitTestText(local) != null ? Cursors.SizeAll : Cursors.IBeam;
                return;
            }
            if (!_hasSelection)
            {
                Cursor = Cursors.Cross;
                return;
            }
            if (_tool != ToolKind.None)
            {
                // 按住 Ctrl：光标立刻变成“移动”，提示当前是整体拖选区
                Cursor = ctrlDown && _sel.Contains(p) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            Cursor = _sel.Contains(p) ? Cursors.SizeAll : Cursors.Cross;
        }

        #endregion

        #region 标注

        private void SelectTool(ToolKind kind)
        {
            if (_closing) return;
            if (kind != ToolKind.None) ExitTextMode();
            _tool = _tool == kind ? ToolKind.None : kind;
            UpdateToolbarState();
            UpdateSelectionVisuals();
        }

        private void UpdateToolbarState()
        {
            foreach (var pair in _toolButtons)
            {
                bool active = pair.Key == _tool;
                pair.Value.Background = active
                    ? new SolidColorBrush(Color.FromArgb(0x66, 0x3F, 0xA9, 0xF5))
                    : Brushes.Transparent;
                pair.Value.Tag = active;
            }
            UpdateSwatches();
        }

        private void UpdateSwatches()
        {
            for (int i = 0; i < _swatches.Count; i++)
            {
                var brush = _swatches[i].Background as SolidColorBrush;
                bool active = brush != null && brush.Color == _color;
                _swatches[i].BorderBrush = active
                    ? Brushes.White
                    : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                _swatches[i].BorderThickness = new Thickness(active ? 2 : 1);
            }
        }

        private void BeginAnnotate(Point p)
        {
            _annotating = true;
            _annotStart = p;

            // 注意：所有标注模型都用「选区内的相对坐标」，这里必须先换算，
            // 否则首点会是屏幕绝对坐标，箭头/画笔/马赛克会从屏幕右下角拉出一条多余路径。
            var start = ClampLocal(p);

            switch (_tool)
            {
                case ToolKind.Rect:
                    _preview = new RectAnnotation { Color = _color, Thickness = 3, Bounds = new Rect(start, new Size(0, 0)) };
                    break;
                case ToolKind.Ellipse:
                    _preview = new EllipseAnnotation { Color = _color, Thickness = 3, Bounds = new Rect(start, new Size(0, 0)) };
                    break;
                case ToolKind.Arrow:
                    _preview = new ArrowAnnotation { Color = _color, Thickness = 3, From = start, To = start };
                    break;
                case ToolKind.Pen:
                    var pen = new PenAnnotation { Color = _color, Thickness = 3 };
                    pen.Points.Add(start);
                    _preview = pen;
                    break;
                case ToolKind.Mosaic:
                    EnsureMosaic();
                    var mosaic = new MosaicAnnotation { Color = _color, Thickness = 16 };
                    mosaic.Points.Add(start);
                    _preview = mosaic;
                    break;
                default:
                    _annotating = false;
                    return;
            }

            _layer.Preview = _preview;
            _layer.Refresh();
        }

        private void UpdateAnnotation(Point p)
        {
            if (_preview == null) return;

            var rect = _preview as RectAnnotation;
            if (rect != null)
            {
                rect.Bounds = LocalRect(_annotStart, p);
                _layer.Refresh();
                return;
            }
            var ellipse = _preview as EllipseAnnotation;
            if (ellipse != null)
            {
                ellipse.Bounds = LocalRect(_annotStart, p);
                _layer.Refresh();
                return;
            }
            var arrow = _preview as ArrowAnnotation;
            if (arrow != null)
            {
                arrow.To = ClampLocal(p);
                _layer.Refresh();
                return;
            }
            var pen = _preview as PenAnnotation;
            if (pen != null)
            {
                pen.Points.Add(ClampLocal(p));
                _layer.Refresh();
                return;
            }
            var mosaic = _preview as MosaicAnnotation;
            if (mosaic != null)
            {
                var local = ClampLocal(p);
                if (mosaic.Points.Count == 0 || Distance(mosaic.Points[mosaic.Points.Count - 1], local) > 3)
                {
                    mosaic.Points.Add(local);
                }
                _layer.Refresh();
            }
        }

        private void FinishAnnotation()
        {
            _annotating = false;
            if (_preview == null) return;

            bool keep = true;
            var rect = _preview as RectAnnotation;
            if (rect != null && rect.Bounds.Width < 4 && rect.Bounds.Height < 4) keep = false;
            var ellipse = _preview as EllipseAnnotation;
            if (ellipse != null && ellipse.Bounds.Width < 4 && ellipse.Bounds.Height < 4) keep = false;
            var arrow = _preview as ArrowAnnotation;
            if (arrow != null && Distance(arrow.From, arrow.To) < 6) keep = false;
            var pen = _preview as PenAnnotation;
            if (pen != null && pen.Points.Count < 2) keep = false;
            var mosaic = _preview as MosaicAnnotation;
            if (mosaic != null && mosaic.Points.Count < 2) keep = false;

            if (keep)
            {
                _layer.Items.Add(_preview);
                _redo.Clear();
            }
            _preview = null;
            _layer.Preview = null;
            _layer.Refresh();
        }

        private Rect LocalRect(Point a, Point b)
        {
            var pa = ClampLocal(a);
            var pb = ClampLocal(b);
            double x = Math.Min(pa.X, pb.X);
            double y = Math.Min(pa.Y, pb.Y);
            double w = Math.Max(1, Math.Abs(pa.X - pb.X));
            double h = Math.Max(1, Math.Abs(pa.Y - pb.Y));
            return new Rect(x, y, w, h);
        }

        private Point ClampLocal(Point p)
        {
            return new Point(
                Math.Max(0, Math.Min(_layer.Area.Width, p.X - _sel.X)),
                Math.Max(0, Math.Min(_layer.Area.Height, p.Y - _sel.Y)));
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void EnsureMosaic()
        {
            if (_layer.Mosaic != null) return;
            int w = (int)Math.Round(_layer.Area.Width);
            int h = (int)Math.Round(_layer.Area.Height);
            if (w < 2 || h < 2) return;
            try
            {
                var cropped = new CroppedBitmap(_shot,
                    new Int32Rect((int)Math.Round(_sel.X), (int)Math.Round(_sel.Y), w, h));
                _layer.Mosaic = ScreenCapture.Pixelate(cropped, 12);
            }
            catch
            {
            }
        }

        private void Undo()
        {
            if (_layer.Items.Count == 0) return;
            var last = _layer.Items[_layer.Items.Count - 1];
            _layer.Items.RemoveAt(_layer.Items.Count - 1);
            _redo.Push(last);
            _layer.Refresh();
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;
            _layer.Items.Add(_redo.Pop());
            _layer.Refresh();
        }

        #endregion

        #region 文字

        private void ChangeFontSize(double delta)
        {
            _textFontSize = Math.Max(10, Math.Min(120, _textFontSize + delta));
            if (_fontSizeText != null) _fontSizeText.Text = ((int)_textFontSize).ToString();
            if (_textEditor != null) _textEditor.FontSize = _textFontSize;
        }

        private void StartTextEditor(Point p, MouseButtonEventArgs e)
        {
            CommitTextEditor();
            HideLoupe();
            _textEditor = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(_color),
                CaretBrush = new SolidColorBrush(_color),
                SelectionBrush = new SolidColorBrush(Color.FromArgb(0x66, _color.R, _color.G, _color.B)),
                FontSize = _textFontSize,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                MinWidth = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
            };
            Canvas.SetLeft(_textEditor, Math.Max(0, p.X - _sel.X));
            Canvas.SetTop(_textEditor, Math.Max(0, p.Y - _sel.Y));
            _selCanvas.Children.Add(_textEditor);
            _textEditor.PreviewKeyDown += TextEditorKeyDown;
            _textEditor.Focus();
            e.Handled = true;
        }

        private void TextEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTextEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTextEditor();
                e.Handled = true;
            }
        }

        private void CommitTextEditor()
        {
            if (_textEditor == null) return;
            var box = _textEditor;
            _textEditor = null;
            box.PreviewKeyDown -= TextEditorKeyDown;
            _selCanvas.Children.Remove(box);

            var text = box.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            var annotation = new TextAnnotation
            {
                Color = _color,
                Text = text,
                FontSize = _textFontSize,
                Origin = new Point(Canvas.GetLeft(box), Canvas.GetTop(box))
            };
            _layer.Items.Add(annotation);
            _redo.Clear();
            _layer.Refresh();
        }

        private void CancelTextEditor()
        {
            if (_textEditor == null) return;
            var box = _textEditor;
            _textEditor = null;
            box.PreviewKeyDown -= TextEditorKeyDown;
            _selCanvas.Children.Remove(box);
        }

        #endregion

        #region 键盘

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                RefreshCursor();
            }
        }

        private void RefreshCursor()
        {
            if (_closing || _pixelRoot == null) return;
            try
            {
                UpdateCursor(Mouse.GetPosition(_pixelRoot));
            }
            catch
            {
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // 按下/松开 Ctrl 立刻切换光标，让人知道“现在拖是移动选区”
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                RefreshCursor();
                return;
            }

            if (e.Key == Key.Escape)
            {
                // 刚打开时忽略 Esc：可能上一个遮罩的 Esc 事件刚好落在新窗口上
                if (Environment.TickCount - _openedTick < OpenGuardMs)
                {
                    e.Handled = true;
                    return;
                }
                if (_textEditor != null) { CancelTextEditor(); e.Handled = true; return; }
                if (_translatePanel != null && _translatePanel.Visibility == Visibility.Visible)
                {
                    CloseTranslatePanel();
                    e.Handled = true;
                    return;
                }
                Log.Info("close reason: esc");
                Cancel();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (_textEditor != null) { CommitTextEditor(); e.Handled = true; return; }
                Log.Info("close reason: enter");
                Confirm();
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                if (_textMode && _textSelStart >= 0)
                {
                    CopySelectedWords();
                    e.Handled = true;
                    return;
                }
                if (_translatePanel != null && _translatePanel.Visibility == Visibility.Visible)
                {
                    CopyTranslated();
                    e.Handled = true;
                    return;
                }
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; return; }

            if (_textEditor != null) return;

            // C：复制光标处的颜色值（#RRGGBB），遮罩保持打开，可以连续取色
            if (Keyboard.Modifiers == ModifierKeys.None &&
                (e.Key == Key.C || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.C)))
            {
                CopyColorUnderCursor();
                e.Handled = true;
                return;
            }

            // O：进入/退出框选取字模式
            if (Keyboard.Modifiers == ModifierKeys.None &&
                (e.Key == Key.O || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.O)))
            {
                EnterTextMode();
                e.Handled = true;
                return;
            }

            // +/-：调整 OCR 放大倍率并重新识别（应对无法缩放源窗口的场景）
            var keyForScale = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            if ((Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None &&
                (keyForScale == Key.OemPlus || keyForScale == Key.Add ||
                 keyForScale == Key.OemMinus || keyForScale == Key.Subtract))
            {
                bool up = keyForScale == Key.OemPlus || keyForScale == Key.Add;

                // 文字工具激活时，+/- 调的是字号；否则调 OCR 放大倍率
                if (_tool == ToolKind.Text)
                {
                    ChangeFontSize(up ? 4 : -4);
                    ShowToast("字号 " + (int)_textFontSize);
                    e.Handled = true;
                    return;
                }

                double next = Math.Max(1.0, Math.Min(4.0, _ocrScale + (up ? 1.0 : -1.0)));
                if (Math.Abs(next - _ocrScale) > 0.01)
                {
                    _ocrScale = next;
                    if (_hasSelection)
                    {
                        ExitTextMode();
                        EnterTextMode();
                    }
                    else
                    {
                        ShowToast("识别倍率 " + _ocrScale + "×");
                    }
                }
                e.Handled = true;
                return;
            }

            // T：翻译当前选区
            if (Keyboard.Modifiers == ModifierKeys.None &&
                (e.Key == Key.T || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.T)))
            {
                TranslateSelection();
                e.Handled = true;
                return;
            }

            ToolKind tool = ToolKind.None;
            switch (e.Key)
            {
                case Key.D1: case Key.NumPad1: tool = ToolKind.Rect; break;
                case Key.D2: case Key.NumPad2: tool = ToolKind.Ellipse; break;
                case Key.D3: case Key.NumPad3: tool = ToolKind.Arrow; break;
                case Key.D4: case Key.NumPad4: tool = ToolKind.Pen; break;
                case Key.D5: case Key.NumPad5: tool = ToolKind.Text; break;
                case Key.D6: case Key.NumPad6: tool = ToolKind.Mosaic; break;
            }
            if (tool != ToolKind.None && _hasSelection)
            {
                _tool = _tool == tool ? ToolKind.None : tool;
                UpdateToolbarState();
                UpdateSelectionVisuals();
                e.Handled = true;
            }
        }

        #endregion

        #region 收尾

        private void Confirm()
        {
            if (_closing) return;
            if (!_hasSelection || _layer.Area.Width < 1 || _layer.Area.Height < 1) return;

            CommitTextEditor();

            BitmapSource image;
            try
            {
                image = Compose();
            }
            catch (Exception ex)
            {
                Log.Error("合成截图失败", ex);
                return;
            }

            // 先关遮罩、再写剪贴板：合成（实测 60-80ms）完成后窗口立即消失，
            // 写入交给后台 STA 线程。剪贴板被输入法占用时最多再等 ClipboardWriter.ImageBudgetMs，
            // 这段时间用户已经在做别的事了，不再表现为「点完卡一下」。
            CloseOverlay();
            ClipboardWriter.RunOnWorker(delegate { WriteImageInBackground(image); });
        }

        /// <summary>
        /// 后台线程写入图片。失败时给一次可见提示 —— 旧实现是静默失败，
        /// 用户只会觉得「点了完成没反应」。
        /// </summary>
        private static void WriteImageInBackground(BitmapSource image)
        {
            if (ClipboardWriter.WriteImage(image, ClipboardWriter.ImageBudgetMs)) return;

            Log.Info("clipboard image write failed: " + ClipboardWriter.LastError);

            // 气泡必须回 UI 线程弹（NotifyIcon 的窗口属于 UI 线程）
            var app = System.Windows.Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(delegate
                {
                    TrayController.Instance.ShowBalloon("截图未复制",
                        "剪贴板正被其他程序占用（常见于输入法），请稍后再试一次。",
                        System.Windows.Forms.ToolTipIcon.Warning);
                }));
        }

        private BitmapSource Compose()
        {
            int w = Math.Max(1, (int)Math.Round(_layer.Area.Width));
            int h = Math.Max(1, (int)Math.Round(_layer.Area.Height));

            if (_layer.Mosaic == null && HasMosaic()) EnsureMosaic();

            var cropped = new CroppedBitmap(_shot,
                new Int32Rect((int)Math.Round(_sel.X), (int)Math.Round(_sel.Y), w, h));

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(cropped, new Rect(0, 0, w, h));
                _layer.DrawAll(dc);
            }

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        private bool HasMosaic()
        {
            for (int i = 0; i < _layer.Items.Count; i++)
            {
                if (_layer.Items[i] is MosaicAnnotation) return true;
            }
            return false;
        }

        // 旧的 CopyToClipboard（6 次 × .NET 内部约 1 秒重试）已移除：
        // 最坏会卡满 6 秒，是「点击完成后卡一下」的直接原因。
        // 现在统一走 ClipboardWriter（后台 STA 线程 + 廉价占用探测 + 总预算上限）。

        private void Cancel()
        {
            CloseOverlay();
        }

        private void CloseOverlay()
        {
            if (_closing) return;
            _closing = true;
            if (_pixelRoot != null && _pixelRoot.IsMouseCaptured) _pixelRoot.ReleaseMouseCapture();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _closing = true;

            // 主动断开对大位图的引用，让整屏截图（4K 下约 20MB）能尽快被回收
            try
            {
                if (_bgImage != null) _bgImage.Source = null;
                if (_layer != null)
                {
                    _layer.Mosaic = null;
                    _layer.Items.Clear();
                }
                Content = null;
            }
            catch
            {
            }
        }

        #endregion
    }
}
