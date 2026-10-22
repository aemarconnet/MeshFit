using System;

namespace MeshFit.Core.Geometry
{
    /// <summary>Small dense linear algebra helpers (no external dependencies).</summary>
    public static class LinearAlgebra
    {
        /// <summary>
        /// Eigen-decomposition of a symmetric 3x3 matrix by cyclic Jacobi rotations.
        /// Eigenvalues are returned in ascending order; eigenvector i is column i of <paramref name="vectors"/>.
        /// </summary>
        public static void SymmetricEigen3(double[,] a, out double[] values, out Vec3[] vectors)
        {
            var m = (double[,])a.Clone();
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(m[0, 1]) + Math.Abs(m[0, 2]) + Math.Abs(m[1, 2]);
                if (off < 1e-300) break;
                for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (theta == 0) t = 1;
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 3; k++)
                    {
                        double mkp = m[k, p], mkq = m[k, q];
                        m[k, p] = c * mkp - s * mkq;
                        m[k, q] = s * mkp + c * mkq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double mpk = m[p, k], mqk = m[q, k];
                        m[p, k] = c * mpk - s * mqk;
                        m[q, k] = s * mpk + c * mqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
            }
            int[] idx = { 0, 1, 2 };
            Array.Sort(idx, (i, j) => m[i, i].CompareTo(m[j, j]));
            values = new double[3];
            vectors = new Vec3[3];
            for (int i = 0; i < 3; i++)
            {
                int k = idx[i];
                values[i] = m[k, k];
                vectors[i] = new Vec3(v[0, k], v[1, k], v[2, k]).Normalized();
            }
        }

        /// <summary>Eigen-decomposition of a symmetric n×n matrix (cyclic Jacobi). Ascending eigenvalues; vectors[i] is eigenvector i.</summary>
        public static void SymmetricEigen(double[,] a, out double[] values, out double[][] vectors)
        {
            int n = a.GetLength(0);
            var m = (double[,])a.Clone();
            var v = new double[n, n];
            for (int i = 0; i < n; i++) v[i, i] = 1;
            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = 0;
                for (int p = 0; p < n; p++) for (int q = p + 1; q < n; q++) off += Math.Abs(m[p, q]);
                if (off < 1e-300) break;
                for (int p = 0; p < n - 1; p++)
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(m[p, q]) < 1e-300) continue;
                    double theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
                    double t = theta == 0 ? 1 : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < n; k++) { double x = m[k, p], y = m[k, q]; m[k, p] = c * x - s * y; m[k, q] = s * x + c * y; }
                    for (int k = 0; k < n; k++) { double x = m[p, k], y = m[q, k]; m[p, k] = c * x - s * y; m[q, k] = s * x + c * y; }
                    for (int k = 0; k < n; k++) { double x = v[k, p], y = v[k, q]; v[k, p] = c * x - s * y; v[k, q] = s * x + c * y; }
                }
            }
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (i, j) => m[i, i].CompareTo(m[j, j]));
            values = new double[n];
            vectors = new double[n][];
            for (int i = 0; i < n; i++)
            {
                values[i] = m[idx[i], idx[i]];
                vectors[i] = new double[n];
                for (int k = 0; k < n; k++) vectors[i][k] = v[k, idx[i]];
            }
        }

        /// <summary>
        /// In-place Cholesky factorisation of a symmetric positive definite matrix (lower triangle used).
        /// Returns false if the matrix is not positive definite.
        /// </summary>
        public static bool Cholesky(double[,] a, int n)
        {
            for (int j = 0; j < n; j++)
            {
                double d = a[j, j];
                for (int k = 0; k < j; k++) d -= a[j, k] * a[j, k];
                if (d <= 0 || double.IsNaN(d)) return false;
                d = Math.Sqrt(d);
                a[j, j] = d;
                for (int i = j + 1; i < n; i++)
                {
                    double s = a[i, j];
                    for (int k = 0; k < j; k++) s -= a[i, k] * a[j, k];
                    a[i, j] = s / d;
                }
            }
            return true;
        }

        /// <summary>Solves L Lᵀ x = b using a factor produced by <see cref="Cholesky"/>. b is overwritten with x.</summary>
        public static void CholeskySolve(double[,] l, int n, double[] b)
        {
            for (int i = 0; i < n; i++)
            {
                double s = b[i];
                for (int k = 0; k < i; k++) s -= l[i, k] * b[k];
                b[i] = s / l[i, i];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = b[i];
                for (int k = i + 1; k < n; k++) s -= l[k, i] * b[k];
                b[i] = s / l[i, i];
            }
        }

        /// <summary>
        /// Solves the symmetric system A x = b (A is n×n, symmetric). Falls back to adding a
        /// small diagonal ridge if A is singular. Returns null if it cannot be solved.
        /// </summary>
        public static double[] SolveSymmetric(double[,] a, double[] b)
        {
            int n = b.Length;
            double maxDiag = 0;
            for (int i = 0; i < n; i++) maxDiag = Math.Max(maxDiag, Math.Abs(a[i, i]));
            if (maxDiag == 0) maxDiag = 1;
            double ridge = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                var f = (double[,])a.Clone();
                if (ridge > 0) for (int i = 0; i < n; i++) f[i, i] += ridge;
                if (Cholesky(f, n))
                {
                    var x = (double[])b.Clone();
                    CholeskySolve(f, n, x);
                    return x;
                }
                ridge = ridge == 0 ? maxDiag * 1e-12 : ridge * 100;
            }
            return null;
        }

        /// <summary>Linear least squares min |A x - b| via normal equations (fine for few unknowns).</summary>
        public static double[] LeastSquares(double[][] rows, double[] rhs, int cols)
        {
            var ata = new double[cols, cols];
            var atb = new double[cols];
            for (int r = 0; r < rows.Length; r++)
            {
                var row = rows[r];
                for (int i = 0; i < cols; i++)
                {
                    atb[i] += row[i] * rhs[r];
                    for (int j = 0; j <= i; j++) ata[i, j] += row[i] * row[j];
                }
            }
            for (int i = 0; i < cols; i++) for (int j = 0; j < i; j++) ata[j, i] = ata[i, j];
            return SolveSymmetric(ata, atb);
        }
    }
}
