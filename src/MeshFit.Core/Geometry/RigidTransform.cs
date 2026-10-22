using System;
using System.Globalization;

namespace MeshFit.Core.Geometry
{
    /// <summary>Rotation (3×3, row-major) followed by translation: p' = R p + T.</summary>
    public sealed class RigidTransform
    {
        public readonly double[,] R;
        public readonly Vec3 T;

        public RigidTransform(double[,] r, Vec3 t) { R = r; T = t; }

        public static RigidTransform Identity => new RigidTransform(new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } }, Vec3.Zero);
        public static RigidTransform Translation(Vec3 t) => new RigidTransform(Identity.R, t);

        /// <summary>Rotation by <paramref name="angle"/> (radians) about the axis through <paramref name="point"/>.</summary>
        public static RigidTransform Rotation(Vec3 axis, double angle, Vec3 point = default)
        {
            var a = axis.Normalized();
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
            var r = new double[,]
            {
                { t * a.X * a.X + c, t * a.X * a.Y - s * a.Z, t * a.X * a.Z + s * a.Y },
                { t * a.X * a.Y + s * a.Z, t * a.Y * a.Y + c, t * a.Y * a.Z - s * a.X },
                { t * a.X * a.Z - s * a.Y, t * a.Y * a.Z + s * a.X, t * a.Z * a.Z + c }
            };
            var rot = new RigidTransform(r, Vec3.Zero);
            return new RigidTransform(r, point - rot.ApplyVector(point));
        }

        /// <summary>Smallest rotation taking direction a onto direction b.</summary>
        public static RigidTransform RotationBetween(Vec3 a, Vec3 b)
        {
            a = a.Normalized(); b = b.Normalized();
            var axis = a.Cross(b);
            double sin = axis.Length, cos = a.Dot(b);
            if (sin < 1e-12) return cos > 0 ? Identity : Rotation(a.AnyPerpendicular(), Math.PI);
            return Rotation(axis, Math.Atan2(sin, cos));
        }

        public Vec3 Apply(Vec3 p) => ApplyVector(p) + T;

        public Vec3 ApplyVector(Vec3 v) => new Vec3(
            R[0, 0] * v.X + R[0, 1] * v.Y + R[0, 2] * v.Z,
            R[1, 0] * v.X + R[1, 1] * v.Y + R[1, 2] * v.Z,
            R[2, 0] * v.X + R[2, 1] * v.Y + R[2, 2] * v.Z);

        /// <summary>this ∘ first: apply <paramref name="first"/>, then this.</summary>
        public RigidTransform After(RigidTransform first)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++) r[i, j] += R[i, k] * first.R[k, j];
            return new RigidTransform(r, Apply(first.T));
        }

        public RigidTransform Inverse()
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) r[i, j] = R[j, i];
            var inv = new RigidTransform(r, Vec3.Zero);
            return new RigidTransform(r, -inv.ApplyVector(T));
        }

        public Frame Apply(Frame f) => new Frame(Apply(f.Origin), ApplyVector(f.ZAxis), ApplyVector(f.XAxis));

        public bool IsIdentity(double eps = 1e-12)
        {
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (Math.Abs(R[i, j] - (i == j ? 1 : 0)) > eps) return false;
            return T.Length <= eps;
        }

        /// <summary>12 numbers: rotation rows then translation.</summary>
        public override string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            var parts = new string[12];
            for (int i = 0; i < 9; i++) parts[i] = R[i / 3, i % 3].ToString("R", inv);
            parts[9] = T.X.ToString("R", inv); parts[10] = T.Y.ToString("R", inv); parts[11] = T.Z.ToString("R", inv);
            return string.Join(" ", parts);
        }

        public static RigidTransform Parse(string s)
        {
            var p = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 12) throw new FormatException("Transform needs 12 numbers.");
            var v = new double[12];
            for (int i = 0; i < 12; i++) v[i] = double.Parse(p[i], CultureInfo.InvariantCulture);
            var r = new double[3, 3];
            for (int i = 0; i < 9; i++) r[i / 3, i % 3] = v[i];
            return new RigidTransform(r, new Vec3(v[9], v[10], v[11]));
        }

        /// <summary>Euler angles (degrees, rotations about fixed X then Y then Z) for display.</summary>
        public void ToEulerXYZ(out double rx, out double ry, out double rz)
        {
            ry = Math.Asin(Math.Max(-1, Math.Min(1, -R[2, 0])));
            rx = Math.Atan2(R[2, 1], R[2, 2]);
            rz = Math.Atan2(R[1, 0], R[0, 0]);
            rx *= 180 / Math.PI; ry *= 180 / Math.PI; rz *= 180 / Math.PI;
        }
    }
}
