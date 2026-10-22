using System;

namespace MeshFit.Core.Geometry
{
    /// <summary>Symmetric positive definite banded matrix with in-place Cholesky solver.</summary>
    public sealed class BandedSymmetricMatrix
    {
        public int Size { get; }
        public int Bandwidth { get; }
        // _b[i, d] = A[i, i - d], d = 0..Bandwidth
        private readonly double[,] _b;

        public BandedSymmetricMatrix(int n, int bandwidth)
        {
            Size = n; Bandwidth = Math.Min(bandwidth, n - 1);
            _b = new double[n, Bandwidth + 1];
        }

        public void Add(int i, int j, double v)
        {
            if (i < j) { int t = i; i = j; j = t; }
            int d = i - j;
            if (d > Bandwidth) throw new ArgumentOutOfRangeException(nameof(j), "Entry outside band");
            _b[i, d] += v;
        }

        public double Get(int i, int j)
        {
            if (i < j) { int t = i; i = j; j = t; }
            int d = i - j;
            return d > Bandwidth ? 0 : _b[i, d];
        }

        public double Trace()
        {
            double s = 0;
            for (int i = 0; i < Size; i++) s += _b[i, 0];
            return s;
        }

        public void AddDiagonal(double v) { for (int i = 0; i < Size; i++) _b[i, 0] += v; }

        /// <summary>Factorises in place (A = L Lᵀ). Returns false if not positive definite.</summary>
        public bool Factor()
        {
            int n = Size, bw = Bandwidth;
            for (int j = 0; j < n; j++)
            {
                double s = _b[j, 0];
                for (int k = Math.Max(0, j - bw); k < j; k++) { double l = _b[j, j - k]; s -= l * l; }
                if (s <= 0 || double.IsNaN(s)) return false;
                double ljj = Math.Sqrt(s);
                _b[j, 0] = ljj;
                int iMax = Math.Min(n - 1, j + bw);
                for (int i = j + 1; i <= iMax; i++)
                {
                    double t = _b[i, i - j];
                    int k0 = Math.Max(0, i - bw);
                    for (int k = k0; k < j; k++) t -= _b[i, i - k] * _b[j, j - k];
                    _b[i, i - j] = t / ljj;
                }
            }
            return true;
        }

        /// <summary>Solves using the factor; x is overwritten with the solution.</summary>
        public void Solve(double[] x)
        {
            int n = Size, bw = Bandwidth;
            for (int i = 0; i < n; i++)
            {
                double s = x[i];
                for (int k = Math.Max(0, i - bw); k < i; k++) s -= _b[i, i - k] * x[k];
                x[i] = s / _b[i, 0];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = x[i];
                int kMax = Math.Min(n - 1, i + bw);
                for (int k = i + 1; k <= kMax; k++) s -= _b[k, k - i] * x[k];
                x[i] = s / _b[i, 0];
            }
        }
    }
}
