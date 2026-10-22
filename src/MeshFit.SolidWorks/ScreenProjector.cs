using System;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Converts view pixel coordinates (as reported by mouse events) into a pick line in model space,
    /// using IModelView.Transform (model → pixels, origin top-left, y down) and IModelView.Orientation3
    /// (whose +Z points toward the viewer) to decide which hit is in front. Orthographic views only.
    /// </summary>
    internal sealed class ScreenProjector
    {
        private readonly double[,] _a = new double[3, 3]; // pixel = A * p + b
        private readonly double[] _b = new double[3];
        public Vec3 TowardViewer { get; private set; }

        public ScreenProjector(IModelView view, IMathUtility math)
        {
            var xf = view.Transform;
            var origin = Apply(math, xf, Vec3.Zero);
            for (int k = 0; k < 3; k++) _b[k] = origin[k];
            var axes = new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ };
            for (int c = 0; c < 3; c++)
            {
                var p = Apply(math, xf, axes[c]);
                for (int r = 0; r < 3; r++) _a[r, c] = p[r] - _b[r];
            }
            // Model direction mapped to the view's +Z by the orientation (rotation only).
            var orient = view.Orientation3;
            double vx = ApplyVector(math, orient, Vec3.UnitX).Z;
            double vy = ApplyVector(math, orient, Vec3.UnitY).Z;
            double vz = ApplyVector(math, orient, Vec3.UnitZ).Z;
            TowardViewer = new Vec3(vx, vy, vz).Normalized();
        }

        private static Vec3 Apply(IMathUtility math, MathTransform xf, Vec3 p)
        {
            var mp = (MathPoint)math.CreatePoint(p.ToArray());
            var r = (double[])((MathPoint)mp.MultiplyTransform(xf)).ArrayData;
            return new Vec3(r[0], r[1], r[2]);
        }

        private static Vec3 ApplyVector(IMathUtility math, MathTransform xf, Vec3 v)
        {
            var mv = (MathVector)math.CreateVector(v.ToArray());
            var r = (double[])((MathVector)mv.MultiplyTransform(xf)).ArrayData;
            return new Vec3(r[0], r[1], r[2]);
        }

        /// <summary>Model-space line through the pixel: point on the line and unit direction toward the viewer.</summary>
        public void PickLine(int px, int py, out Vec3 point, out Vec3 dir)
        {
            var r0 = new Vec3(_a[0, 0], _a[0, 1], _a[0, 2]);
            var r1 = new Vec3(_a[1, 0], _a[1, 1], _a[1, 2]);
            double y0 = px - _b[0], y1 = py - _b[1];
            // Minimum-norm solution of [r0; r1] p = y.
            double g00 = r0.Dot(r0), g01 = r0.Dot(r1), g11 = r1.Dot(r1);
            double det = g00 * g11 - g01 * g01;
            double l0 = (y0 * g11 - y1 * g01) / det, l1 = (g00 * y1 - g01 * y0) / det;
            point = r0 * l0 + r1 * l1;
            dir = r0.Cross(r1).Normalized();
            if (dir.Dot(TowardViewer) < 0) dir = -dir;
        }

        /// <summary>Model length of one pixel (for screen-constant sizes).</summary>
        public double PixelSize
        {
            get
            {
                var r0 = new Vec3(_a[0, 0], _a[0, 1], _a[0, 2]);
                return 1.0 / Math.Max(r0.Length, 1e-300);
            }
        }
    }
}
