using System;
using System.Globalization;

namespace MeshFit.Core.Geometry
{
    /// <summary>Immutable 3D vector / point in double precision.</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);
        public double DistanceTo(Vec3 b) => (this - b).Length;
        public double DistanceSquaredTo(Vec3 b) => (this - b).LengthSquared;

        public Vec3 Normalized()
        {
            double l = Length;
            return l > 1e-300 ? this / l : UnitZ;
        }

        public bool IsFinite => !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z) ||
                                  double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));

        /// <summary>Returns some unit vector perpendicular to this one.</summary>
        public Vec3 AnyPerpendicular()
        {
            Vec3 n = Normalized();
            Vec3 t = Math.Abs(n.X) < 0.6 ? UnitX : Math.Abs(n.Y) < 0.6 ? UnitY : UnitZ;
            return (t - n * n.Dot(t)).Normalized();
        }

        public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;

        public double[] ToArray() => new[] { X, Y, Z };

        public bool Equals(Vec3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        // + 0.0 folds -0.0 into +0.0 so that equal vectors hash equally.
        public override int GetHashCode() => (X + 0.0).GetHashCode() * 397 ^ (Y + 0.0).GetHashCode() * 17 ^ (Z + 0.0).GetHashCode();

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:G6}, {1:G6}, {2:G6})", X, Y, Z);
    }

    /// <summary>Right-handed orthonormal frame (origin + three axes).</summary>
    public readonly struct Frame
    {
        public readonly Vec3 Origin, XAxis, YAxis, ZAxis;

        public Frame(Vec3 origin, Vec3 zAxis, Vec3 xHint)
        {
            Origin = origin;
            ZAxis = zAxis.Normalized();
            Vec3 x = xHint - ZAxis * ZAxis.Dot(xHint);
            if (x.Length < 1e-9) x = ZAxis.AnyPerpendicular();
            XAxis = x.Normalized();
            YAxis = ZAxis.Cross(XAxis);
        }

        public static Frame FromAxis(Vec3 origin, Vec3 zAxis) => new Frame(origin, zAxis, zAxis.AnyPerpendicular());

        public Vec3 ToLocal(Vec3 p)
        {
            Vec3 d = p - Origin;
            return new Vec3(d.Dot(XAxis), d.Dot(YAxis), d.Dot(ZAxis));
        }

        public Vec3 ToWorld(Vec3 l) => Origin + XAxis * l.X + YAxis * l.Y + ZAxis * l.Z;
        public Vec3 ToWorld(double x, double y, double z) => Origin + XAxis * x + YAxis * y + ZAxis * z;
    }

    public struct BoundingBox
    {
        public Vec3 Min, Max;
        public static BoundingBox Empty => new BoundingBox
        {
            Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue),
            Max = new Vec3(double.MinValue, double.MinValue, double.MinValue)
        };
        public void Add(Vec3 p) { Min = Vec3.Min(Min, p); Max = Vec3.Max(Max, p); }
        public Vec3 Center => (Min + Max) * 0.5;
        public double Diagonal => Max.X < Min.X ? 0 : (Max - Min).Length;
    }
}
