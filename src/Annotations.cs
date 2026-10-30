using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cloudshot
{
    public enum ToolKind
    {
        None,
        Rect,
        Ellipse,
        Arrow,
        Pen,
        Text,
        Mosaic
    }

    /// <summary>
    /// 标注对象：坐标一律使用物理像素（与截图 1:1）。
    /// </summary>
    public abstract class Annotation
    {
        public Color Color = Color.FromRgb(0xF5, 0x2C, 0x2C);
        public double Thickness = 3;

        public abstract void Draw(DrawingContext dc, BitmapSource mosaic, Size area);

        protected static Pen MakePen(Color color, double thickness)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }
    }

    public sealed class RectAnnotation : Annotation
    {
        public Rect Bounds;

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            if (Bounds.Width < 1 && Bounds.Height < 1) return;
            dc.DrawRectangle(null, MakePen(Color, Thickness), Bounds);
        }
    }

    public sealed class EllipseAnnotation : Annotation
    {
        public Rect Bounds;

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            if (Bounds.Width < 1 && Bounds.Height < 1) return;
            dc.DrawEllipse(null, MakePen(Color, Thickness),
                new Point(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2),
                Bounds.Width / 2, Bounds.Height / 2);
        }
    }

    public sealed class ArrowAnnotation : Annotation
    {
        public Point From;
        public Point To;

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            double dx = To.X - From.X;
            double dy = To.Y - From.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 2) return;

            double headLength = Math.Max(12, Thickness * 4.2);
            double headWidth = Math.Max(9, Thickness * 3.2);
            double ux = dx / length;
            double uy = dy / length;

            var tip = To;
            var baseCenter = new Point(To.X - ux * headLength, To.Y - uy * headLength);
            var perpendicular = new Point(-uy, ux);

            var left = new Point(baseCenter.X + perpendicular.X * headWidth / 2, baseCenter.Y + perpendicular.Y * headWidth / 2);
            var right = new Point(baseCenter.X - perpendicular.X * headWidth / 2, baseCenter.Y - perpendicular.Y * headWidth / 2);

            var pen = MakePen(Color, Thickness);
            dc.DrawLine(pen, From, new Point(baseCenter.X, baseCenter.Y));

            var brush = new SolidColorBrush(Color);
            brush.Freeze();
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(left, true, true);
                ctx.LineTo(right, true, true);
            }
            head.Freeze();
            dc.DrawGeometry(brush, null, head);
        }
    }

    public sealed class PenAnnotation : Annotation
    {
        public readonly List<Point> Points = new List<Point>();

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            if (Points.Count == 0) return;
            if (Points.Count == 1)
            {
                dc.DrawEllipse(new SolidColorBrush(Color), null, Points[0], Thickness / 2, Thickness / 2);
                return;
            }
            dc.DrawGeometry(null, MakePen(Color, Thickness), StrokeGeometry.Build(Points, false));
        }
    }

    public sealed class TextAnnotation : Annotation
    {
        public Point Origin;
        public string Text = string.Empty;
        public double FontSize = 24;
        public string FontFamily = "Microsoft YaHei UI";

        public double Width { get; private set; }
        public double Height { get; private set; }

        public bool Contains(Point p)
        {
            const double pad = 4;
            return p.X >= Origin.X - pad && p.X <= Origin.X + Width + pad &&
                   p.Y >= Origin.Y - pad && p.Y <= Origin.Y + Height + pad;
        }

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            if (string.IsNullOrEmpty(Text)) return;
            var formatted = new FormattedText(
                Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily),
                FontSize,
                new SolidColorBrush(Color),
                1.0);
            Width = formatted.Width;
            Height = formatted.Height;
            dc.DrawText(formatted, Origin);
        }
    }

    public sealed class MosaicAnnotation : Annotation
    {
        public readonly List<Point> Points = new List<Point>();

        public override void Draw(DrawingContext dc, BitmapSource mosaic, Size area)
        {
            if (mosaic == null || Points.Count == 0) return;
            var clip = StrokeGeometry.Build(Points, true, Thickness);
            if (clip == null) return;
            dc.PushClip(clip);
            dc.DrawImage(mosaic, new Rect(0, 0, area.Width, area.Height));
            dc.Pop();
        }
    }

    internal static class StrokeGeometry
    {
        /// <summary>
        /// 由采样点生成描边几何；widened=true 时返回加宽后的轮廓（用于马赛克裁剪）。
        /// </summary>
        public static Geometry Build(IList<Point> points, bool widened, double width = 12)
        {
            if (points == null || points.Count == 0) return null;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], false, false);
                for (int i = 1; i < points.Count; i++)
                {
                    ctx.LineTo(points[i], true, true);
                }
            }
            geometry.Freeze();

            if (!widened) return geometry;

            var pen = new Pen(Brushes.Black, Math.Max(2, width))
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();

            var result = geometry.GetWidenedPathGeometry(pen);
            result.Freeze();
            return result;
        }
    }

    /// <summary>
    /// 标注绘制层：所有图形由同一份模型绘制，保证预览与导出像素一致。
    /// </summary>
    public sealed class AnnotationLayer : FrameworkElement
    {
        public readonly List<Annotation> Items = new List<Annotation>();
        public Annotation Preview;
        public BitmapSource Mosaic;
        public Size Area;

        public AnnotationLayer()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public void DrawAll(DrawingContext dc)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].Draw(dc, Mosaic, Area);
            }
            if (Preview != null) Preview.Draw(dc, Mosaic, Area);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            DrawAll(dc);
        }

        public void Refresh()
        {
            InvalidateVisual();
        }

        /// <summary>命中已有的文字标注（从上往下找，后添加的优先）。</summary>
        public TextAnnotation HitTestText(Point p)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var text = Items[i] as TextAnnotation;
                if (text != null && text.Contains(p)) return text;
            }
            return null;
        }
    }
}
