using System;

namespace MeshFit.Core.Geometry
{
    /// <summary>B-spline basis function evaluation (Piegl &amp; Tiller, "The NURBS Book", A2.1 / A2.3).</summary>
    public static class BSplineBasis
    {
        /// <summary>Knot span index for parameter u (degree p, n+1 control points, knots of length n+p+2).</summary>
        public static int FindSpan(int n, int p, double u, double[] knots)
        {
            if (u >= knots[n + 1]) return n;
            if (u <= knots[p]) return p;
            int low = p, high = n + 1, mid = (low + high) / 2;
            while (u < knots[mid] || u >= knots[mid + 1])
            {
                if (u < knots[mid]) high = mid; else low = mid;
                mid = (low + high) / 2;
            }
            return mid;
        }

        /// <summary>Basis functions and derivatives up to order nd: ders[k, j] = k-th derivative of N(span-p+j).</summary>
        public static double[,] DersBasisFuns(int span, double u, int p, int nd, double[] U)
        {
            var ders = new double[nd + 1, p + 1];
            var ndu = new double[p + 1, p + 1];
            var left = new double[p + 1];
            var right = new double[p + 1];
            ndu[0, 0] = 1.0;
            for (int j = 1; j <= p; j++)
            {
                left[j] = u - U[span + 1 - j];
                right[j] = U[span + j] - u;
                double saved = 0.0;
                for (int r = 0; r < j; r++)
                {
                    ndu[j, r] = right[r + 1] + left[j - r];
                    double temp = ndu[r, j - 1] / ndu[j, r];
                    ndu[r, j] = saved + right[r + 1] * temp;
                    saved = left[j - r] * temp;
                }
                ndu[j, j] = saved;
            }
            for (int j = 0; j <= p; j++) ders[0, j] = ndu[j, p];
            var a = new double[2, p + 1];
            for (int r = 0; r <= p; r++)
            {
                int s1 = 0, s2 = 1;
                a[0, 0] = 1.0;
                for (int k = 1; k <= nd; k++)
                {
                    double d = 0.0;
                    int rk = r - k, pk = p - k;
                    if (r >= k)
                    {
                        a[s2, 0] = a[s1, 0] / ndu[pk + 1, rk];
                        d = a[s2, 0] * ndu[rk, pk];
                    }
                    int j1 = rk >= -1 ? 1 : -rk;
                    int j2 = (r - 1 <= pk) ? k - 1 : p - r;
                    for (int j = j1; j <= j2; j++)
                    {
                        a[s2, j] = (a[s1, j] - a[s1, j - 1]) / ndu[pk + 1, rk + j];
                        d += a[s2, j] * ndu[rk + j, pk];
                    }
                    if (r <= pk)
                    {
                        a[s2, k] = -a[s1, k - 1] / ndu[pk + 1, r];
                        d += a[s2, k] * ndu[r, pk];
                    }
                    ders[k, r] = d;
                    int tmp = s1; s1 = s2; s2 = tmp;
                }
            }
            int rr = p;
            for (int k = 1; k <= nd; k++)
            {
                for (int j = 0; j <= p; j++) ders[k, j] *= rr;
                rr *= p - k;
            }
            return ders;
        }

        /// <summary>Clamped uniform knot vector on [a, b] for n control points of degree p.</summary>
        public static double[] ClampedUniformKnots(int nCtrl, int p, double a, double b)
        {
            var k = new double[nCtrl + p + 1];
            int inner = nCtrl - p - 1;
            for (int i = 0; i <= p; i++) { k[i] = a; k[k.Length - 1 - i] = b; }
            for (int i = 1; i <= inner; i++) k[p + i] = a + (b - a) * i / (inner + 1);
            return k;
        }
    }

    /// <summary>Tensor-product (optionally rational) B-spline surface. Control net indexed [u, v].</summary>
    public sealed class NurbsSurface
    {
        public int DegreeU { get; }
        public int DegreeV { get; }
        public double[] KnotsU { get; }
        public double[] KnotsV { get; }
        public Vec3[,] ControlPoints { get; }
        /// <summary>Null for a non-rational (polynomial) surface.</summary>
        public double[,] Weights { get; }

        public int CountU => ControlPoints.GetLength(0);
        public int CountV => ControlPoints.GetLength(1);
        public bool IsRational => Weights != null;
        public double UMin => KnotsU[DegreeU];
        public double UMax => KnotsU[KnotsU.Length - 1 - DegreeU];
        public double VMin => KnotsV[DegreeV];
        public double VMax => KnotsV[KnotsV.Length - 1 - DegreeV];

        public NurbsSurface(int degU, int degV, double[] knotsU, double[] knotsV, Vec3[,] ctrl, double[,] weights = null)
        {
            if (knotsU.Length != ctrl.GetLength(0) + degU + 1) throw new ArgumentException("U knot count mismatch");
            if (knotsV.Length != ctrl.GetLength(1) + degV + 1) throw new ArgumentException("V knot count mismatch");
            DegreeU = degU; DegreeV = degV; KnotsU = knotsU; KnotsV = knotsV; ControlPoints = ctrl; Weights = weights;
        }

        public Vec3 Evaluate(double u, double v)
        {
            Evaluate(u, v, out Vec3 s, out _, out _);
            return s;
        }

        /// <summary>Point and first partial derivatives.</summary>
        public void Evaluate(double u, double v, out Vec3 s, out Vec3 su, out Vec3 sv)
        {
            u = Math.Max(UMin, Math.Min(UMax, u));
            v = Math.Max(VMin, Math.Min(VMax, v));
            int nu = CountU - 1, nv = CountV - 1;
            int spanU = BSplineBasis.FindSpan(nu, DegreeU, u, KnotsU);
            int spanV = BSplineBasis.FindSpan(nv, DegreeV, v, KnotsV);
            var bu = BSplineBasis.DersBasisFuns(spanU, u, DegreeU, 1, KnotsU);
            var bv = BSplineBasis.DersBasisFuns(spanV, v, DegreeV, 1, KnotsV);
            Vec3 a = Vec3.Zero, au = Vec3.Zero, av = Vec3.Zero;
            double w = 0, wu = 0, wv = 0;
            for (int i = 0; i <= DegreeU; i++)
            {
                int iu = spanU - DegreeU + i;
                for (int j = 0; j <= DegreeV; j++)
                {
                    int jv = spanV - DegreeV + j;
                    double wt = Weights == null ? 1.0 : Weights[iu, jv];
                    Vec3 p = ControlPoints[iu, jv] * wt;
                    double n00 = bu[0, i] * bv[0, j], n10 = bu[1, i] * bv[0, j], n01 = bu[0, i] * bv[1, j];
                    a += p * n00; au += p * n10; av += p * n01;
                    w += wt * n00; wu += wt * n10; wv += wt * n01;
                }
            }
            s = a / w;
            su = (au - s * wu) / w;
            sv = (av - s * wv) / w;
        }

        /// <summary>Closest point by Newton iteration starting from (u0, v0).</summary>
        public Vec3 Project(Vec3 p, ref double u, ref double v, int iterations = 12)
        {
            Vec3 s = Vec3.Zero;
            for (int it = 0; it < iterations; it++)
            {
                Evaluate(u, v, out s, out Vec3 su, out Vec3 sv);
                Vec3 r = s - p;
                // Gauss-Newton step on |S(u,v) - p|^2.
                double a11 = su.Dot(su), a12 = su.Dot(sv), a22 = sv.Dot(sv);
                double b1 = -r.Dot(su), b2 = -r.Dot(sv);
                double det = a11 * a22 - a12 * a12;
                if (Math.Abs(det) < 1e-30) break;
                double du = (b1 * a22 - b2 * a12) / det, dv = (a11 * b2 - a12 * b1) / det;
                double nu = Math.Max(UMin, Math.Min(UMax, u + du));
                double nv = Math.Max(VMin, Math.Min(VMax, v + dv));
                double step = Math.Abs(nu - u) / (UMax - UMin) + Math.Abs(nv - v) / (VMax - VMin);
                u = nu; v = nv;
                if (step < 1e-10) break;
            }
            return Evaluate(u, v);
        }
    }
}
