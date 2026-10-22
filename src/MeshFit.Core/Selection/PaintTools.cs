using System;
using System.Collections.Generic;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Selection
{
    /// <summary>Painting and region growing operations on a <see cref="RegionSet"/>.</summary>
    public static class PaintTools
    {
        /// <summary>
        /// Paints (or erases with <paramref name="regionId"/> = <see cref="RegionSet.None"/>) all triangles
        /// connected to <paramref name="seedTri"/> whose centroid lies within <paramref name="radius"/> of
        /// <paramref name="center"/>. Connectivity prevents painting through to the back side of a panel.
        /// </summary>
        /// <param name="onlyUnassigned">Do not steal triangles already owned by other regions.</param>
        public static int Brush(RegionSet set, int seedTri, Vec3 center, double radius, int regionId, bool onlyUnassigned = false) =>
            Brush(set, seedTri, center, radius, regionId, t => CanWrite(set, t, regionId, onlyUnassigned));

        /// <summary>Brush with an explicit filter deciding which triangles may change.</summary>
        public static int Brush(RegionSet set, int seedTri, Vec3 center, double radius, int regionId, Func<int, bool> canChange)
        {
            var m = set.Mesh;
            double r2 = radius * radius;
            int changed = 0;
            Flood(m, seedTri, t =>
            {
                if (m.Centroids[t].DistanceSquaredTo(center) > r2 && t != seedTri) return false;
                if (canChange(t) && set.Assign(t, regionId)) changed++;
                return true;
            });
            return changed;
        }

        /// <summary>
        /// Unselects the smooth patch under the seed: triangles connected across edges smoother than
        /// <paramref name="maxEdgeAngleDeg"/> that pass <paramref name="canChange"/> (e.g. "owned by the active region").
        /// </summary>
        public static int RemoveSmooth(RegionSet set, int seedTri, double maxEdgeAngleDeg, Func<int, bool> canChange)
        {
            var m = set.Mesh;
            if (set.OwnerOf(seedTri) == RegionSet.None || !canChange(seedTri)) return 0;
            double cosEdge = Math.Cos(maxEdgeAngleDeg * Math.PI / 180);
            var seen = new HashSet<int> { seedTri };
            var q = new Queue<int>();
            q.Enqueue(seedTri);
            int changed = 0;
            while (q.Count > 0)
            {
                int t = q.Dequeue();
                if (set.Assign(t, RegionSet.None)) changed++;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb < 0 || !seen.Add(nb)) continue;
                    if (set.OwnerOf(nb) == RegionSet.None || !canChange(nb)) continue;
                    if (m.FaceNormals[t].Dot(m.FaceNormals[nb]) < cosEdge) continue;
                    q.Enqueue(nb);
                }
            }
            return changed;
        }

        /// <summary>Unselects every triangle of the connected shell under the seed that passes the filter.</summary>
        public static int RemoveConnected(RegionSet set, int seedTri, Func<int, bool> canChange)
        {
            int changed = 0;
            Flood(set.Mesh, seedTri, t =>
            {
                if (set.OwnerOf(t) != RegionSet.None && canChange(t) && set.Assign(t, RegionSet.None)) changed++;
                return true;
            });
            return changed;
        }

        /// <summary>
        /// Grows from a seed across edges whose dihedral angle is below <paramref name="maxEdgeAngleDeg"/>
        /// and whose normal stays within <paramref name="maxSeedAngleDeg"/> of the seed normal
        /// (≤ 0 disables that test). Stops at sharp edges and at triangles owned by other regions.
        /// </summary>
        public static int GrowSmooth(RegionSet set, int seedTri, int regionId, double maxEdgeAngleDeg, double maxSeedAngleDeg = 0)
        {
            var m = set.Mesh;
            double cosEdge = Math.Cos(maxEdgeAngleDeg * Math.PI / 180);
            double cosSeed = maxSeedAngleDeg > 0 ? Math.Cos(maxSeedAngleDeg * Math.PI / 180) : -2;
            var n0 = m.FaceNormals[seedTri];
            int changed = 0;
            var visited = new HashSet<int> { seedTri };
            var queue = new Queue<int>();
            queue.Enqueue(seedTri);
            if (CanWrite(set, seedTri, regionId, true) && set.Assign(seedTri, regionId)) changed++;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb < 0 || !visited.Add(nb)) continue;
                    if (m.FaceNormals[t].Dot(m.FaceNormals[nb]) < cosEdge) continue;
                    if (m.FaceNormals[nb].Dot(n0) < cosSeed) continue;
                    int o = set.OwnerOf(nb);
                    if (o != RegionSet.None && o != regionId) continue;
                    if (set.Assign(nb, regionId)) changed++;
                    queue.Enqueue(nb);
                }
            }
            return changed;
        }

        /// <summary>
        /// Grows a region to all connected triangles whose vertices lie within <paramref name="tolerance"/>
        /// of the region's fitted surface (and whose normals agree with it). The most effective tool for
        /// reverse engineering: paint a small patch, fit, then grow to the true boundary of the surface.
        /// </summary>
        public static int GrowToSurface(RegionSet set, int regionId, FittedSurface surface, double tolerance, double maxNormalDeviationDeg = 20)
        {
            var m = set.Mesh;
            double cosN = Math.Cos(maxNormalDeviationDeg * Math.PI / 180);
            double h = Math.Max(m.AverageEdgeLength * 0.05, 1e-9);
            var queue = new Queue<int>();
            var visited = new HashSet<int>();
            foreach (int t in set.TrianglesOf(regionId)) { queue.Enqueue(t); visited.Add(t); }
            int changed = 0;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb < 0 || !visited.Add(nb)) continue;
                    if (set.OwnerOf(nb) != RegionSet.None) continue;
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
                    if (set.Assign(nb, regionId)) changed++;
                    queue.Enqueue(nb);
                }
            }
            return changed;
        }

        /// <summary>Assigns every triangle connected to the seed (whole shell) to the region.</summary>
        public static int FillConnected(RegionSet set, int seedTri, int regionId)
        {
            int changed = 0;
            Flood(set.Mesh, seedTri, t =>
            {
                if (CanWrite(set, t, regionId, true) && set.Assign(t, regionId)) changed++;
                return true;
            });
            return changed;
        }

        /// <summary>Adds the ring of neighbouring triangles to (grow &gt; 0) or removes the boundary ring from the region.</summary>
        public static int Dilate(RegionSet set, int regionId, bool grow)
        {
            var m = set.Mesh;
            var targets = new List<int>();
            for (int t = 0; t < m.TriangleCount; t++)
            {
                int o = set.OwnerOf(t);
                bool inside = o == regionId;
                if (grow ? (inside || o != RegionSet.None) : !inside) continue;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    bool nbInside = nb >= 0 && set.OwnerOf(nb) == regionId;
                    if (grow ? nbInside : !nbInside) { targets.Add(t); break; }
                }
            }
            foreach (int t in targets) set.Assign(t, grow ? regionId : RegionSet.None);
            return targets.Count;
        }

        /// <summary>
        /// Fills "islands": connected groups of unassigned triangles that are completely enclosed by the
        /// region (every neighbouring triangle belongs to it and the group has no open mesh edge). These are
        /// the speckles and small patches a smart fill or brush stroke leaves behind on noisy scans.
        /// </summary>
        /// <param name="maxIslandTriangles">Largest island to fill; ≤ 0 means any island smaller than the region itself.</param>
        /// <returns>Number of triangles added.</returns>
        public static int FillIslands(RegionSet set, int regionId, int maxIslandTriangles = 0)
        {
            var m = set.Mesh;
            int regionSize = 0;
            for (int t = 0; t < m.TriangleCount; t++) if (set.OwnerOf(t) == regionId) regionSize++;
            if (regionSize == 0) return 0;
            int limit = maxIslandTriangles > 0 ? maxIslandTriangles : regionSize - 1;

            var visited = new bool[m.TriangleCount];
            var island = new List<int>();
            int added = 0;
            for (int seed = 0; seed < m.TriangleCount; seed++)
            {
                if (visited[seed] || set.OwnerOf(seed) != RegionSet.None) continue;
                // Only start from unassigned triangles touching the region.
                bool touches = false;
                for (int k = 0; k < 3 && !touches; k++)
                {
                    int nb = m.Neighbors[seed * 3 + k];
                    touches = nb >= 0 && set.OwnerOf(nb) == regionId;
                }
                if (!touches) continue;

                island.Clear();
                island.Add(seed);
                visited[seed] = true;
                bool enclosed = true;
                for (int i = 0; i < island.Count; i++)
                {
                    int t = island[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int nb = m.Neighbors[t * 3 + k];
                        if (nb < 0) { enclosed = false; continue; }          // open edge of the scan
                        int o = set.OwnerOf(nb);
                        if (o == regionId) continue;
                        if (o != RegionSet.None) { enclosed = false; continue; } // touches another region
                        if (visited[nb]) continue;
                        visited[nb] = true;
                        island.Add(nb);
                    }
                    if (island.Count > limit) enclosed = false;
                    // Keep flooding even when not enclosed so the whole component is marked visited.
                }
                if (!enclosed) continue;
                foreach (int t in island) if (set.Assign(t, regionId)) added++;
            }
            return added;
        }

        /// <summary>
        /// Automatic segmentation: splits all unassigned triangles into smooth patches separated by edges
        /// sharper than <paramref name="maxEdgeAngleDeg"/>. Patches smaller than
        /// <paramref name="minTriangles"/> are left unassigned. Returns the created regions.
        /// </summary>
        public static List<Region> AutoSegment(RegionSet set, double maxEdgeAngleDeg, int minTriangles)
        {
            var m = set.Mesh;
            double cosEdge = Math.Cos(maxEdgeAngleDeg * Math.PI / 180);
            var label = new int[m.TriangleCount];
            var created = new List<Region>();
            set.BeginStroke("Auto segment");
            for (int seed = 0; seed < m.TriangleCount; seed++)
            {
                if (label[seed] != 0 || set.OwnerOf(seed) != RegionSet.None) continue;
                var patch = new List<int> { seed };
                label[seed] = 1;
                for (int i = 0; i < patch.Count; i++)
                {
                    int t = patch[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int nb = m.Neighbors[t * 3 + k];
                        if (nb < 0 || label[nb] != 0 || set.OwnerOf(nb) != RegionSet.None) continue;
                        if (m.FaceNormals[t].Dot(m.FaceNormals[nb]) < cosEdge) continue;
                        label[nb] = 1;
                        patch.Add(nb);
                    }
                }
                if (patch.Count < minTriangles) continue;
                var r = set.AddRegion();
                foreach (int t in patch) set.Assign(t, r.Id);
                created.Add(r);
            }
            set.EndStroke();
            return created;
        }

        private static bool CanWrite(RegionSet set, int t, int regionId, bool onlyUnassigned)
        {
            if (!onlyUnassigned) return true;
            int o = set.OwnerOf(t);
            return o == RegionSet.None || o == regionId || regionId == RegionSet.None;
        }

        /// <summary>Breadth-first traversal; <paramref name="visit"/> returns false to stop expanding from a triangle.</summary>
        private static void Flood(TriMesh m, int seed, Func<int, bool> visit)
        {
            var visited = new HashSet<int> { seed };
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                if (!visit(t)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb >= 0 && visited.Add(nb)) queue.Enqueue(nb);
                }
            }
        }
    }
}
