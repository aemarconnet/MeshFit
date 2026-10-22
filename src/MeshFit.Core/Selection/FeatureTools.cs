using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Selection
{
    /// <summary>
    /// Selection tools that recognise adjacent features (constant or variable fillets, roll-overs, feature
    /// lines) from curvature rather than from edge angles, which stay small across tangent blends.
    /// A triangle is a "feature" relative to a surface when its largest curvature exceeds the surface's
    /// typical curvature by more than 1 / <c>blendRadius</c>; e.g. blendRadius = 30 mm flags every fillet
    /// tighter than about 30 mm on a flat or gently crowned panel.
    /// </summary>
    public static class FeatureTools
    {
        public static Func<int, bool> IsFeature(Curvature c, double baselineK, double blendRadius)
        {
            double limit = baselineK + 1.0 / blendRadius;
            return t => c.KMax[t] > limit;
        }

        /// <summary>Typical curvature near a seed (median over its 2-ring), used when no region exists yet.</summary>
        public static double SeedBaseline(TriMesh m, Curvature c, int seed)
        {
            var ring = new HashSet<int> { seed };
            for (int pass = 0; pass < 2; pass++)
                foreach (int t in ring.ToList())
                    for (int k = 0; k < 3; k++) { int nb = m.Neighbors[t * 3 + k]; if (nb >= 0) ring.Add(nb); }
            return c.MedianKMax(ring);
        }

        /// <summary>
        /// Grows the region over unpainted triangles until it reaches adjacent features (fillets, sharp edges).
        /// <paramref name="maxEdgeAngleDeg"/> still stops at sharp creases.
        /// </summary>
        public static int GrowToFeatures(RegionSet set, int regionId, Curvature c, double blendRadius, double maxEdgeAngleDeg = 30)
        {
            var m = set.Mesh;
            var tris = set.TriangleListOf(regionId);
            if (tris.Count == 0) return 0;
            var feature = IsFeature(c, c.MedianKMax(tris), blendRadius);
            double cosEdge = Math.Cos(maxEdgeAngleDeg * Math.PI / 180);
            var queue = new Queue<int>(tris);
            var visited = new HashSet<int>(tris);
            int added = 0;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb < 0 || !visited.Add(nb)) continue;
                    if (set.OwnerOf(nb) != RegionSet.None || feature(nb)) continue;
                    if (m.FaceNormals[t].Dot(m.FaceNormals[nb]) < cosEdge) continue;
                    if (set.Assign(nb, regionId)) added++;
                    queue.Enqueue(nb);
                }
            }
            return added;
        }

        /// <summary>
        /// Removes the region's triangles that run into adjacent fillets (tangent run-out that biases fits),
        /// keeping pieces of meaningful size.
        /// </summary>
        public static int TrimFeatures(RegionSet set, int regionId, Curvature c, double blendRadius)
        {
            var m = set.Mesh;
            var tris = set.TriangleListOf(regionId);
            if (tris.Count == 0) return 0;
            var feature = IsFeature(c, c.MedianKMax(tris), blendRadius);
            var mask = new bool[m.TriangleCount];
            foreach (int t in tris) if (!feature(t)) mask[t] = true;
            var comps = MaskOps.Components(m, mask);
            var keep = new bool[m.TriangleCount];
            if (comps.Count > 0)
            {
                int min = Math.Max(1, comps[0].Count / 20);
                foreach (var comp in comps) if (comp.Count >= min) foreach (int t in comp) keep[t] = true;
            }
            int removed = 0;
            foreach (int t in tris) if (!keep[t] && set.Assign(t, RegionSet.None)) removed++;
            return removed;
        }

        /// <summary>
        /// Selects the connected fillet / blend band under the seed: triangles curving more than 1 / blendRadius
        /// above the flattest surroundings. Follows variable-radius blends as long as they stay above the limit.
        /// </summary>
        public static int SelectFeatureBand(RegionSet set, int seed, int regionId, Curvature c, double blendRadius, bool onlyUnassigned)
        {
            var m = set.Mesh;
            // Baseline: the flatter surroundings — 10th percentile of curvature within two local fillet
            // radii of the seed (far enough to reach the faces on both sides of the blend).
            double reach = 2.0 / Math.Max(c.KMax[seed], 1e-9) + 2 * c.Scale;
            var origin = m.Centroids[seed];
            var near = new List<int> { seed };
            var seen = new HashSet<int> { seed };
            for (int i = 0; i < near.Count && near.Count < 50000; i++)
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[near[i] * 3 + k];
                    if (nb >= 0 && seen.Add(nb) && m.Centroids[nb].DistanceTo(origin) <= reach) near.Add(nb);
                }
            var ks = near.Select(t => c.KMax[t]).OrderBy(x => x).ToList();
            var feature = IsFeature(c, ks[ks.Count / 10], blendRadius);
            if (!feature(seed)) return 0;
            var queue = new Queue<int>();
            var visited = new HashSet<int> { seed };
            queue.Enqueue(seed);
            int added = 0;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                int o = set.OwnerOf(t);
                if (!onlyUnassigned || o == RegionSet.None || o == regionId)
                    if (set.Assign(t, regionId)) added++;
                for (int k = 0; k < 3; k++)
                {
                    int nb = m.Neighbors[t * 3 + k];
                    if (nb >= 0 && visited.Add(nb) && feature(nb)) queue.Enqueue(nb);
                }
            }
            return added;
        }
    }
}
