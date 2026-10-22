using System.Drawing;
using System.Drawing.Drawing2D;
using MeshFit.Core.Fitting;

namespace MeshFit.SolidWorks
{
    /// <summary>Small drawn icons describing each surface type (no image files needed).</summary>
    internal static class ShapeIcons
    {
        public static Bitmap Draw(FitRequest type)
        {
            var bmp = new Bitmap(24, 24);
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(Color.FromArgb(30, 70, 140), 1.4f))
            using (var fill = new SolidBrush(Color.FromArgb(150, 195, 240)))
            using (var dark = new SolidBrush(Color.FromArgb(95, 150, 215)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                switch (type)
                {
                    case FitRequest.Plane:
                    {
                        var p = new[] { new PointF(2, 16), new PointF(8, 6), new PointF(22, 6), new PointF(16, 16) };
                        g.FillPolygon(fill, p); g.DrawPolygon(pen, p);
                        break;
                    }
                    case FitRequest.Sphere:
                        using (var gb = new LinearGradientBrush(new Rectangle(3, 3, 18, 18), Color.FromArgb(220, 235, 255), Color.FromArgb(70, 130, 205), 45f))
                            g.FillEllipse(gb, 3, 3, 18, 18);
                        g.DrawEllipse(pen, 3, 3, 18, 18);
                        g.DrawArc(pen, 3, 9, 18, 6, 0, 180);
                        break;
                    case FitRequest.Cylinder:
                        g.FillRectangle(fill, 5, 5, 14, 14);
                        g.FillEllipse(dark, 5, 16, 14, 6);
                        g.FillEllipse(fill, 5, 2, 14, 6);
                        g.DrawEllipse(pen, 5, 2, 14, 6);
                        g.DrawLine(pen, 5, 5, 5, 19); g.DrawLine(pen, 19, 5, 19, 19);
                        g.DrawArc(pen, 5, 16, 14, 6, 0, 180);
                        break;
                    case FitRequest.Cone:
                    {
                        var p = new[] { new PointF(12, 2), new PointF(3, 19), new PointF(21, 19) };
                        g.FillPolygon(fill, p);
                        g.FillEllipse(dark, 3, 16, 18, 6);
                        g.DrawLine(pen, 12, 2, 3, 19); g.DrawLine(pen, 12, 2, 21, 19);
                        g.DrawArc(pen, 3, 16, 18, 6, 0, 180);
                        break;
                    }
                    case FitRequest.Torus:
                        g.FillEllipse(fill, 1, 6, 22, 13);
                        g.DrawEllipse(pen, 1, 6, 22, 13);
                        g.FillEllipse(Brushes.White, 8, 10, 8, 4);
                        g.DrawArc(pen, 7, 9, 10, 5, 180, 180);
                        g.DrawArc(pen, 8, 10, 8, 4, 0, 180);
                        break;
                    case FitRequest.Freeform:
                    {
                        var top = new[] { new PointF(2, 12), new PointF(7, 4), new PointF(12, 13), new PointF(17, 5), new PointF(22, 10) };
                        using (var path = new GraphicsPath())
                        {
                            path.AddCurve(top);
                            path.AddLine(22, 10, 22, 20);
                            path.AddLine(22, 20, 2, 20);
                            path.CloseFigure();
                            g.FillPath(fill, path);
                        }
                        g.DrawCurve(pen, top);
                        g.DrawCurve(pen, new[] { new PointF(2, 17), new PointF(8, 13), new PointF(13, 19), new PointF(22, 15) });
                        break;
                    }
                    default: // Auto
                        g.FillEllipse(fill, 2, 2, 20, 20);
                        g.DrawEllipse(pen, 2, 2, 20, 20);
                        using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                            g.DrawString("A", f, Brushes.Navy, 6, 4);
                        break;
                }
            }
            return bmp;
        }
    }
}
