using System;
using System.Collections.Generic;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Selection
{
    /// <summary>
    /// Selection operations on a plain triangle mask (bool per triangle). They don't touch a
    /// <see cref="RegionSet"/>, so they can run on snapshots in background threads.
    /// </summary>
    public static class MaskOps
    {
        /// <summary>
        /// Adds every triangle connected to the mask whose vertices lie within <paramref name="tolerance"/>
        /// of the surface and whose normal agrees with it. <paramref name="available"/> says which
        /// triangles may be added (e.g. unpainted ones). Returns the number added.
        /// </summary>
        public static int GrowToSurface(TriMesh m, bool[] mask, Func<int, bool> available, FittedSurface surface,
                                        double tolerance, double maxNormalDeviationDeg = 20)
        {
            double cosN = Math.Cos(maxNormalDeviationDeg * Math.PI / 180);
            double h = Math.Max(m.AverageEdgeLength * 0.05, 1e-9);
            var queue = new Queue<int>();
            var visited = new bool[m.TriangleCount];
            for (int t = 0; t < mask.Length; t++) if (mask[t]) { queue.Enqueue(t); visited[t] = true; }
            int added = 0;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb < 0 || visited[nb]) continue;
                    visited[nb] = true;
                    if (!available(nb)) continue;
                    bool ok = true;
                    for (int c = 0; c < 3 && ok; c++) ok = Math.Abs(surface.Distance(m.P(nb, c))) <= tolerance;
                    if (!ok) continue;
                    // Surface normal by finite difference of the distance field at the centroid.
                    var q = m.Centroids[nb];
                    var g = new Vec3(
                        surface.Distance(q + new Vec3(h, 0, 0)) - surface.Distance(q - new Vec3(h, 0, 0)),
                        surface.Distance(q + new Vec3(0, h, 0)) - surface.Distance(q - new Vec3(0, h, 0)),
                        surface.Distance(q + new Vec3(0, 0, h)) - surface.Distance(q - new Vec3(0, 0, h)));
                    if (g.Length > 0 && Math.Abs(g.Normalized().Dot(m.FaceNormals[nb])) < cosN) continue;
                    mask[nb] = true;
                    added++;
                    queue.Enqueue(nb);
                }
            }
            return added;
        }

        /// <summary>Removes the outer ring (triangles with a neighbour outside the mask; open mesh edges don't count).</summary>
        public static int Shrink(TriMesh m, bool[] mask)
        {
            var remove = new List<int>();
            for (int t = 0; t < mask.Length; t++)
            {
                if (!mask[t]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb >= 0 && !mask[nb]) { remove.Add(t); break; }
                }
            }
            foreach (int t in remove) mask[t] = false;
            return remove.Count;
        }

        /// <summary>Connected components of the mask (each a list of triangles), largest first.</summary>
        public static List<List<int>> Components(TriMesh m, bool[] mask)
        {
            var seen = new bool[mask.Length];
            var comps = new List<List<int>>();
            for (int s = 0; s < mask.Length; s++)
            {
                if (!mask[s] || seen[s]) continue;
                var c = new List<int> { s };
                seen[s] = true;
                for (int i = 0; i < c.Count; i++)
                {
                    int t = c[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int nb = m.Neighbors[t * 3 + k];
                        if (nb >= 0 && mask[nb] && !seen[nb]) { seen[nb] = true; c.Add(nb); }
                    }
                }
                comps.Add(c);
            }
            comps.Sort((a, b) => b.Count.CompareTo(a.Count));
            return comps;
        }

        public static double Area(TriMesh m, bool[] mask)
        {
            double a = 0;
            for (int t = 0; t < mask.Length; t++) if (mask[t]) a += m.FaceAreas[t];
            return a;
        }

        public static List<int> ToList(bool[] mask)
        {
            var l = new List<int>();
            for (int t = 0; t < mask.Length; t++) if (mask[t]) l.Add(t);
            return l;
        }
    }
}
