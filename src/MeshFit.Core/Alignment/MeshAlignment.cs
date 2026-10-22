using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Alignment
{
    public enum AlignKind { Plane, Axis, Point }

    /// <summary>A plane (point + normal), an axis (point + direction) or a point.</summary>
    public sealed class AlignFeature
    {
        public AlignKind Kind { get; set; }
        public Vec3 Point { get; set; }
        public Vec3 Direction { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;

        public static AlignFeature Plane(string name, Vec3 p, Vec3 n) => new AlignFeature { Kind = AlignKind.Plane, Name = name, Point = p, Direction = n.Normalized() };
        public static AlignFeature Axis(string name, Vec3 p, Vec3 d) => new AlignFeature { Kind = AlignKind.Axis, Name = name, Point = p, Direction = d.Normalized() };
        public static AlignFeature At(string name, Vec3 p) => new AlignFeature { Kind = AlignKind.Point, Name = name, Point = p };

        /// <summary>Alignment features of a fitted surface: plane, axis (cylinder/cone/torus), centre/apex points.</summary>
        public static IEnumerable<AlignFeature> FromSurface(string region, FittedSurface s)
        {
            switch (s)
            {
                case PlaneSurface p: yield return Plane($"{region} (plane)", p.Origin, p.Normal); break;
                case CylinderSurface c: yield return Axis($"{region} (cylinder axis)", c.AxisPoint, c.Axis); break;
                case ConeSurface k:
                    yield return Axis($"{region} (cone axis)", k.Center, k.Axis);
                    if (k.HalfAngle > 1e-3) yield return At($"{region} (cone apex)", k.Apex);
                    break;
                case SphereSurface sp: yield return At($"{region} (sphere centre)", sp.Center); break;
                case TorusSurface t:
                    yield return Axis($"{region} (torus axis)", t.Center, t.Axis);
                    yield return At($"{region} (torus centre)", t.Center);
                    break;
            }
        }

        /// <summary>The part's default references.</summary>
        public static List<AlignFeature> PartReferences() => new List<AlignFeature>
        {
            Plane("Front plane (XY)", Vec3.Zero, Vec3.UnitZ),
            Plane("Top plane (XZ)", Vec3.Zero, Vec3.UnitY),
            Plane("Right plane (YZ)", Vec3.Zero, Vec3.UnitX),
            Axis("X axis", Vec3.Zero, Vec3.UnitX),
            Axis("Y axis", Vec3.Zero, Vec3.UnitY),
            Axis("Z axis", Vec3.Zero, Vec3.UnitZ),
            At("Origin", Vec3.Zero),
        };
    }

    /// <summary>One "mate": move the mesh feature onto the part reference.</summary>
    public sealed class AlignMate
    {
        public AlignFeature Source { get; set; }
        public AlignFeature Target { get; set; }
        /// <summary>Reverse the direction pairing (anti-aligned instead of the nearest orientation).</summary>
        public bool Flip { get; set; }
    }

    public static class MeshAlignment
    {
        /// <summary>Whether a mesh feature kind can be mated to a target kind.</summary>
        public static bool Compatible(AlignKind source, AlignKind target) =>
            source == target || source == AlignKind.Point;

        /// <summary>
        /// Solves up to three mates in priority order (like 3-2-1 alignment): earlier mates dominate,
        /// later ones fix the remaining freedom. Unconstrained freedoms stay as they are.
        /// </summary>
        public static RigidTransform Solve(IList<AlignMate> mates)
        {
            if (mates.Count == 0) return RigidTransform.Identity;
            foreach (var m in mates)
                if (!Compatible(m.Source.Kind, m.Target.Kind))
                    throw new ArgumentException($"Cannot mate {m.Source.Kind.ToString().ToLowerInvariant()} '{m.Source.Name}' to {m.Target.Kind.ToString().ToLowerInvariant()} '{m.Target.Name}'.");

            // 1. Rotation from the direction pairs (planes and axes), weighted by priority.
            var pairs = new List<(Vec3 s, Vec3 t, double w)>();
            for (int i = 0; i < mates.Count; i++)
            {
                var m = mates[i];
                if (m.Source.Kind == AlignKind.Point) continue;
                var s = m.Source.Direction; var t = m.Target.Direction;
                bool anti = s.Dot(t) < 0;          // nearest orientation by default
                if (anti ^ m.Flip) t = -t;
                pairs.Add((s, t, Math.Pow(100, mates.Count - i)));
            }
            RigidTransform rot = RigidTransform.Identity;
            if (pairs.Count == 1 || (pairs.Count > 1 && pairs.All(p => Math.Abs(p.s.Dot(pairs[0].s)) > 0.9999)))
                rot = RigidTransform.RotationBetween(pairs[0].s, pairs[0].t);
            else if (pairs.Count > 1)
                rot = Horn(pairs);

            // 2. Translation: weighted least squares on the positional conditions (minimum norm).
            var ata = new double[3, 3];
            var atb = new double[3];
            void Row(Vec3 a, double b, double w)
            {
                for (int i = 0; i < 3; i++) { atb[i] += w * a[i] * b; for (int j = 0; j < 3; j++) ata[i, j] += w * a[i] * a[j]; }
            }
            for (int i = 0; i < mates.Count; i++)
            {
                var m = mates[i];
                double w = Math.Pow(100, mates.Count - i);
                var p = rot.Apply(m.Source.Point);           // source point after rotation
                var gap = m.Target.Point - p;                // translation must close this gap
                switch (m.Target.Kind)
                {
                    case AlignKind.Plane:
                        Row(m.Target.Direction, m.Target.Direction.Dot(gap), w);
                        break;
                    case AlignKind.Axis:
                    {
                        var d = m.Target.Direction;
                        var e1 = d.AnyPerpendicular(); var e2 = d.Cross(e1);
                        Row(e1, e1.Dot(gap), w); Row(e2, e2.Dot(gap), w);
                        break;
                    }
                    case AlignKind.Point:
                        Row(Vec3.UnitX, gap.X, w); Row(Vec3.UnitY, gap.Y, w); Row(Vec3.UnitZ, gap.Z, w);
                        break;
                }
            }
            double tr = ata[0, 0] + ata[1, 1] + ata[2, 2];
            for (int i = 0; i < 3; i++) ata[i, i] += 1e-12 * Math.Max(tr, 1);
            var t3 = LinearAlgebra.SolveSymmetric(ata, atb) ?? new double[3];
            return RigidTransform.Translation(new Vec3(t3[0], t3[1], t3[2])).After(rot);
        }

        /// <summary>Best rotation mapping the weighted direction pairs (Horn's quaternion method).</summary>
        private static RigidTransform Horn(List<(Vec3 s, Vec3 t, double w)> pairs)
        {
            var M = new double[3, 3];
            foreach (var (s, t, w) in pairs)
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) M[i, j] += w * s[i] * t[j];
            double sxx = M[0, 0], sxy = M[0, 1], sxz = M[0, 2], syx = M[1, 0], syy = M[1, 1], syz = M[1, 2], szx = M[2, 0], szy = M[2, 1], szz = M[2, 2];
            var N = new double[,]
            {
                { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
                { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
                { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
                { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz }
            };
            LinearAlgebra.SymmetricEigen(N, out _, out var vecs);
            var q = vecs[3];
            double q0 = q[0], qx = q[1], qy = q[2], qz = q[3];
            var r = new double[,]
            {
                { q0*q0 + qx*qx - qy*qy - qz*qz, 2*(qx*qy - q0*qz), 2*(qx*qz + q0*qy) },
                { 2*(qy*qx + q0*qz), q0*q0 - qx*qx + qy*qy - qz*qz, 2*(qy*qz - q0*qx) },
                { 2*(qz*qx - q0*qy), 2*(qz*qy + q0*qx), q0*q0 - qx*qx - qy*qy + qz*qz }
            };
            return new RigidTransform(r, Vec3.Zero);
        }

        // ------------------------------------------------------------------ automatic placements

        public static Vec3 AreaCentroid(TriMesh m)
        {
            Vec3 c = Vec3.Zero; double a = 0;
            for (int t = 0; t < m.TriangleCount; t++) { c += m.Centroids[t] * m.FaceAreas[t]; a += m.FaceAreas[t]; }
            return a > 0 ? c / a : m.Bounds.Center;
        }

        /// <summary>Exact area second moment of the surface about <paramref name="c"/>.</summary>
        public static double[,] SecondMoment(TriMesh m, Vec3 c)
        {
            var cov = new double[3, 3];
            for (int t = 0; t < m.TriangleCount; t++)
            {
                Vec3 a = m.P(t, 0) - c, b = m.P(t, 1) - c, d = m.P(t, 2) - c, g = m.Centroids[t] - c;
                double w = m.FaceAreas[t] / 12;
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        cov[i, j] += w * (a[i] * a[j] + b[i] * b[j] + d[i] * d[j] + 9 * g[i] * g[j]);
            }
            return cov;
        }

        public static RigidTransform CentroidToOrigin(TriMesh m) => RigidTransform.Translation(-AreaCentroid(m));
        public static RigidTransform BoxCenterToOrigin(TriMesh m) => RigidTransform.Translation(-m.Bounds.Center);
        public static RigidTransform BoxMinToOrigin(TriMesh m) => RigidTransform.Translation(-m.Bounds.Min);

        /// <summary>
        /// Centroid to the origin and principal axes to X (longest), Y, Z (thinnest) — the usual vehicle
        /// convention for length / width / height. Axis signs are chosen nearest to the current orientation.
        /// </summary>
        public static RigidTransform PrincipalAxes(TriMesh m)
        {
            var c = AreaCentroid(m);
            var cov = SecondMoment(m, c);
            LinearAlgebra.SymmetricEigen3(cov, out _, out var v);
            var x = v[2]; var y = v[1];
            if (x.X < 0) x = -x;
            if (y.Y < 0) y = -y;
            var z = x.Cross(y);
            // Rows of R are the new axes expressed in the old frame.
            var r = new double[,] { { x.X, x.Y, x.Z }, { y.X, y.Y, y.Z }, { z.X, z.Y, z.Z } };
            var rot = new RigidTransform(r, Vec3.Zero);
            return new RigidTransform(r, -rot.ApplyVector(c));
        }

        private sealed class MirrorState : ILsqState
        {
            public Vec3 N; public double D; // plane n·x = D
            public Bvh Bvh; public double Clip;
            public int ParameterCount => 3;
            public ILsqState Step(double[] d)
            {
                var e1 = N.AnyPerpendicular(); var e2 = N.Cross(e1);
                return new MirrorState { N = (N + e1 * d[0] + e2 * d[1]).Normalized(), D = D + d[2], Bvh = Bvh, Clip = Clip };
            }
            public double Residual(Vec3 p)
            {
                var q = p - N * (2 * (p.Dot(N) - D));
                Bvh.Nearest(q, out var c);
                return Math.Min(c.DistanceTo(q), Clip);
            }
        }

        /// <summary>
        /// Finds the mirror-symmetry plane of the mesh (e.g. the vehicle centre plane): candidates through
        /// the centroid normal to each principal axis are refined by minimising the distance between the
        /// mirrored scan and the scan. Returns the plane and its RMS mirror error.
        /// </summary>
        public static AlignFeature SymmetryPlane(TriMesh m, out double rms, int samples = 1500)
        {
            var c = AreaCentroid(m);
            var cov = SecondMoment(m, c);
            LinearAlgebra.SymmetricEigen3(cov, out _, out var axes);
            var bvh = m.Bvh;
            var rnd = new Random(3);
            var pts = Enumerable.Range(0, Math.Min(samples, m.VertexCount)).Select(_ => m.Vertices[rnd.Next(m.VertexCount)]).ToArray();
            double clip = 0.05 * m.Bounds.Diagonal;
            MirrorState best = null; double bestCost = double.MaxValue;
            var r = new double[pts.Length];
            foreach (var n in axes)
            {
                var st = new MirrorState { N = n, D = n.Dot(c), Bvh = bvh, Clip = clip };
                st = (MirrorState)LevenbergMarquardt.Minimize(st, pts, null, m.Bounds.Diagonal, 30);
                double cost = LevenbergMarquardt.Evaluate(st, pts, null, r);
                if (cost < bestCost) { bestCost = cost; best = st; }
            }
            rms = Math.Sqrt(bestCost / pts.Length);
            var point = c - best.N * (c.Dot(best.N) - best.D);   // centroid projected onto the plane
            var normal = best.N.X < 0 ? -best.N : best.N;           // stable orientation
            return AlignFeature.Plane("Symmetry plane", point, normal);
        }
    }
}
