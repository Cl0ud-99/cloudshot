using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cloudshot
{
    internal static class ScreenCapture
    {
        /// <summary>
        /// 抓取整个虚拟桌面（覆盖所有显示器），返回 Bgra32 的冻结位图。
        /// </summary>
        public static BitmapSource CaptureVirtualScreen(out int left, out int top, out int width, out int height)
        {
            left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN));
            height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

            using (var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
                }

                var hBitmap = bmp.GetHbitmap();
                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                    converted.Freeze();
                    return converted;
                }
                finally
                {
                    NativeMethods.DeleteObject(hBitmap);
                }
            }
        }

        /// <summary>
        /// 把位图转成像素化（马赛克）版本，block 为方块边长（物理像素）。
        /// </summary>
        public static BitmapSource Pixelate(BitmapSource source, int block)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            int stride = w * 4;
            var input = new byte[stride * h];
            source.CopyPixels(input, stride, 0);

            var output = new byte[input.Length];
            for (int by = 0; by < h; by += block)
            {
                int bh = Math.Min(block, h - by);
                for (int bx = 0; bx < w; bx += block)
                {
                    int bw = Math.Min(block, w - bx);
                    long a = 0, r = 0, g = 0, b = 0;
                    int count = 0;
                    for (int y = by; y < by + bh; y++)
                    {
                        int rowOffset = y * stride;
                        for (int x = bx; x < bx + bw; x++)
                        {
                            int i = rowOffset + x * 4;
                            b += input[i];
                            g += input[i + 1];
                            r += input[i + 2];
                            a += input[i + 3];
                            count++;
                        }
                    }

                    byte ab = (byte)(a / count);
                    byte rb = (byte)(r / count);
                    byte gb = (byte)(g / count);
                    byte bb = (byte)(b / count);

                    for (int y = by; y < by + bh; y++)
                    {
                        int rowOffset = y * stride;
                        for (int x = bx; x < bx + bw; x++)
                        {
                            int i = rowOffset + x * 4;
                            output[i] = bb;
                            output[i + 1] = gb;
                            output[i + 2] = rb;
                            output[i + 3] = ab;
                        }
                    }
                }
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, output, stride);
            result.Freeze();
            return result;
        }
    }
}
