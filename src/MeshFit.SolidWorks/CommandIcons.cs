using System.Drawing;
using System.Drawing.Drawing2D;

namespace MeshFit.SolidWorks
{
    /// <summary>Command icons drawn in code (20-unit design grid, rendered at any size).</summary>
    internal static class CommandIcons
    {
        public static Bitmap Render(Cmd cmd, int size, bool transparent = false)
        {
            var bmp = new Bitmap(size, size);
            var background = transparent ? Color.Transparent : Color.White;
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(background);
                    g.ScaleTransform(size / 20f, size / 20f);
                    using (var pen = new Pen(Color.FromArgb(40, 90, 160), 2f))
                    using (var fill = new SolidBrush(Color.FromArgb(70, 150, 230)))
                    using (var red = new Pen(Color.FromArgb(200, 40, 40), 2.5f))
                    {
                        switch (cmd)
                        {
                            case Cmd.ToolSmartFill: // paint bucket
                                g.FillPolygon(fill, new[] { new PointF(4, 9), new PointF(10, 3), new PointF(16, 9), new PointF(10, 15) });
                                g.DrawPolygon(pen, new[] { new PointF(4, 9), new PointF(10, 3), new PointF(16, 9), new PointF(10, 15) });
                                g.FillEllipse(fill, 14, 13, 4, 5);
                                break;
                            case Cmd.ToolBrush:
                                g.DrawLine(pen, 15, 3, 8, 11);
                                g.FillEllipse(fill, 3, 10, 7, 7);
                                break;
                            case Cmd.ToolOff:
                                g.DrawEllipse(pen, 3, 3, 14, 14);
                                g.DrawLine(red, 6, 6, 14, 14);
                                break;
                            case Cmd.ModeRemove:
                                g.DrawEllipse(pen, 2, 2, 16, 16);
                                g.DrawLine(red, 6, 10, 14, 10);
                                break;
                            case Cmd.Undo:
                                g.DrawArc(pen, 4, 5, 12, 10, 180, 270);
                                g.FillPolygon(fill, new[] { new PointF(1, 8), new PointF(7, 8), new PointF(4, 13) });
                                break;
                            case Cmd.NewRegion:
                                g.DrawRectangle(pen, 3, 3, 14, 14);
                                g.DrawLine(pen, 10, 6, 10, 14); g.DrawLine(pen, 6, 10, 14, 10);
                                break;
                            case Cmd.FillIslands:
                                g.FillRectangle(fill, 2, 2, 16, 16);
                                g.FillEllipse(Brushes.White, 7, 7, 6, 6);
                                g.DrawEllipse(red, 7, 7, 6, 6);
                                break;
                            case Cmd.CreateExtrude: // profile + arrow
                                g.DrawArc(pen, 2, 8, 10, 10, 180, 90);
                                g.DrawLine(pen, 7, 8, 16, 8); g.DrawLine(pen, 10, 14, 18, 4); g.DrawLine(pen, 18, 4, 14, 5); g.DrawLine(pen, 18, 4, 17, 8);
                                break;
                            case Cmd.CreateRevolve: // circular arrow around an axis
                                g.DrawLine(red, 10, 1, 10, 19);
                                g.DrawEllipse(pen, 3, 6, 14, 8);
                                g.FillPolygon(fill, new[] { new PointF(15, 12), new PointF(19, 13), new PointF(16, 16) });
                                break;
                            case Cmd.NextFeature: // play / next
                                g.FillPolygon(fill, new[] { new PointF(5, 4), new PointF(15, 10), new PointF(5, 16) });
                                g.DrawLine(pen, 17, 4, 17, 16);
                                break;
                            case Cmd.CleanUp: // broom
                                g.DrawLine(pen, 15, 2, 9, 11);
                                g.FillPolygon(fill, new[] { new PointF(6, 10), new PointF(12, 13), new PointF(9, 19), new PointF(2, 16) });
                                break;
                            case Cmd.Optimize: // target
                                g.DrawEllipse(pen, 2, 2, 16, 16);
                                g.DrawEllipse(pen, 6, 6, 8, 8);
                                g.FillEllipse(fill, 8, 8, 4, 4);
                                break;
                            case Cmd.GrowToFillets: // arrows out to a curved edge
                                g.DrawArc(pen, 12, 2, 14, 16, 100, 160);
                                g.DrawLine(pen, 2, 10, 11, 10); g.DrawLine(pen, 8, 7, 11, 10); g.DrawLine(pen, 8, 13, 11, 10);
                                break;
                            case Cmd.TrimFillets: // scissors-like cut at a curved edge
                                g.DrawArc(pen, 12, 2, 14, 16, 100, 160);
                                g.DrawLine(red, 3, 5, 10, 15); g.DrawLine(red, 10, 5, 3, 15);
                                break;
                            case Cmd.FitAndGrow:
                                g.DrawArc(pen, 2, 6, 16, 16, 200, 140);
                                g.DrawLine(pen, 10, 2, 10, 9); g.DrawLine(pen, 7, 5, 10, 2); g.DrawLine(pen, 13, 5, 10, 2);
                                break;
                            case Cmd.ReverseEngineer: // mesh triangle turning into a smooth surface
                                g.DrawPolygon(pen, new[] { new PointF(2, 17), new PointF(7, 6), new PointF(11, 17) });
                                g.DrawLine(pen, 4.5f, 11.5f, 9, 11.5f);
                                g.DrawBezier(red, 10, 15, 13, 3, 16, 3, 19, 10);
                                break;
                            case Cmd.LoadMesh: // folder with a triangle
                                g.FillRectangle(fill, 2, 6, 16, 11);
                                g.DrawRectangle(pen, 2, 6, 16, 11);
                                g.DrawLine(pen, 2, 6, 7, 3); g.DrawLine(pen, 7, 3, 10, 6);
                                g.DrawPolygon(Pens.White, new[] { new PointF(6, 15), new PointF(10, 9), new PointF(14, 15) });
                                break;
                            case Cmd.UseBodies: // triangulated body
                                g.DrawPolygon(pen, new[] { new PointF(3, 16), new PointF(10, 3), new PointF(17, 16) });
                                g.DrawLine(pen, 6.5f, 9.5f, 13.5f, 9.5f); g.DrawLine(pen, 6.5f, 9.5f, 10, 16); g.DrawLine(pen, 13.5f, 9.5f, 10, 16);
                                break;
                            case Cmd.PositionMesh: // axes triad
                                g.DrawLine(red, 5, 15, 17, 15); g.DrawLine(pen, 5, 15, 5, 3);
                                g.DrawLine(new Pen(Color.Green, 2), 5, 15, 13, 9);
                                break;
                            case Cmd.AnalyzeCad: // colour scale
                                g.FillRectangle(Brushes.RoyalBlue, 2, 4, 5, 12); g.FillRectangle(Brushes.LimeGreen, 7, 4, 6, 12); g.FillRectangle(Brushes.OrangeRed, 13, 4, 5, 12);
                                break;
                            case Cmd.ToggleZebra:
                                for (int i = 0; i < 4; i++) g.FillRectangle(Brushes.Black, 2, 2 + i * 4.5f, 16, 2.2f);
                                break;
                            case Cmd.HideSurfaces: // closed eye
                                g.DrawArc(pen, 2, 6, 16, 10, 0, 180); g.DrawLine(red, 3, 3, 17, 17);
                                break;
                            case Cmd.ShowSurfaces: // eye
                                g.DrawEllipse(pen, 2, 5, 16, 10); g.FillEllipse(fill, 7, 7, 6, 6);
                                break;
                            case Cmd.AnalyzeFillet: // R dimension on an arc
                                g.DrawArc(pen, 2, 2, 28, 28, 180, 90);
                                g.DrawLine(red, 16, 16, 6, 6);
                                break;
                            case Cmd.BoundaryToSketch: // spline curve
                                g.DrawBezier(pen, 2, 15, 6, 2, 12, 18, 18, 4);
                                g.FillEllipse(fill, 1, 13, 4, 4); g.FillEllipse(fill, 16, 2, 4, 4);
                                break;
                            case Cmd.ExportIges: // arrow out of a box
                                g.DrawRectangle(pen, 2, 8, 11, 10);
                                g.DrawLine(red, 8, 12, 17, 3); g.DrawLine(red, 17, 3, 12, 3); g.DrawLine(red, 17, 3, 17, 8);
                                break;
                            case Cmd.SaveProject: // floppy
                                g.FillRectangle(fill, 3, 3, 14, 14); g.FillRectangle(Brushes.White, 6, 3, 8, 5); g.FillRectangle(Brushes.White, 5, 11, 10, 6);
                                break;
                            case Cmd.OpenProject: // open folder
                                g.FillPolygon(fill, new[] { new PointF(2, 16), new PointF(5, 8), new PointF(18, 8), new PointF(15, 16) });
                                g.DrawLine(pen, 2, 16, 2, 5); g.DrawLine(pen, 2, 5, 8, 5);
                                break;
                            case Cmd.Settings: // gear
                                g.DrawEllipse(pen, 5, 5, 10, 10);
                                for (int i = 0; i < 8; i++)
                                {
                                    double a = i * System.Math.PI / 4;
                                    g.DrawLine(pen, 10 + 5 * (float)System.Math.Cos(a), 10 + 5 * (float)System.Math.Sin(a), 10 + 8 * (float)System.Math.Cos(a), 10 + 8 * (float)System.Math.Sin(a));
                                }
                                break;
                            case Cmd.UnloadMesh: // triangle with an x
                                g.DrawPolygon(pen, new[] { new PointF(2, 16), new PointF(8, 4), new PointF(14, 16) });
                                g.DrawLine(red, 12, 3, 18, 9); g.DrawLine(red, 18, 3, 12, 9);
                                break;
                            default:
                                g.FillEllipse(fill, 4, 4, 12, 12);
                                break;
                        }
                    }
                }
            return bmp;
        }
    }
}
