using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Mesh
{
    /// <summary>
    /// Indexed triangle mesh with the derived data needed for painting and fitting:
    /// face normals, areas, centroids, vertex normals and triangle-triangle adjacency.
    /// </summary>
    public sealed class TriMesh
    {
        public Vec3[] Vertices { get; }
        /// <summary>Vertex indices, three per triangle.</summary>
        public int[] Indices { get; }
        public int TriangleCount => Indices.Length / 3;
        public int VertexCount => Vertices.Length;

        public Vec3[] FaceNormals { get; private set; }
        public double[] FaceAreas { get; private set; }
        public Vec3[] Centroids { get; private set; }
        public Vec3[] VertexNormals { get; private set; }
        /// <summary>Neighbour triangle across edge k (k = 0..2, edge from corner k to k+1), or -1.</summary>
        public int[] Neighbors { get; private set; }
        public BoundingBox Bounds { get; private set; }
        public double AverageEdgeLength { get; private set; }

        private Bvh _bvh;
        public Bvh Bvh => _bvh ?? (_bvh = new Bvh(this));

        public TriMesh(Vec3[] vertices, int[] indices)
        {
            if (indices.Length % 3 != 0) throw new ArgumentException("Index count must be a multiple of 3.");
            Vertices = vertices;
            Indices = RemoveDegenerate(vertices, indices);
            Build();
        }

        public int V(int tri, int corner) => Indices[tri * 3 + corner];
        public Vec3 P(int tri, int corner) => Vertices[Indices[tri * 3 + corner]];

        private static int[] RemoveDegenerate(Vec3[] v, int[] idx)
        {
            var list = new List<int>(idx.Length);
            for (int t = 0; t < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                if (a == b || b == c || a == c) continue;
                if ((v[b] - v[a]).Cross(v[c] - v[a]).LengthSquared <= 0) continue;
                list.Add(a); list.Add(b); list.Add(c);
            }
            return list.ToArray();
        }

        private void Build()
        {
            int nt = TriangleCount;
            FaceNormals = new Vec3[nt];
            FaceAreas = new double[nt];
            Centroids = new Vec3[nt];
            var vn = new Vec3[Vertices.Length];
            var bb = BoundingBox.Empty;
            foreach (var p in Vertices) bb.Add(p);
            Bounds = bb;
            double edgeSum = 0;

            for (int t = 0; t < nt; t++)
            {
                Vec3 a = P(t, 0), b = P(t, 1), c = P(t, 2);
                Vec3 cr = (b - a).Cross(c - a);
                double len = cr.Length;
                FaceAreas[t] = 0.5 * len;
                FaceNormals[t] = len > 0 ? cr / len : Vec3.UnitZ;
                Centroids[t] = (a + b + c) / 3.0;
                for (int k = 0; k < 3; k++) vn[V(t, k)] += cr; // area weighted
                edgeSum += (b - a).Length + (c - b).Length + (a - c).Length;
            }
            for (int i = 0; i < vn.Length; i++) vn[i] = vn[i].Normalized();
            VertexNormals = vn;
            AverageEdgeLength = nt > 0 ? edgeSum / (3.0 * nt) : 0;

            Neighbors = new int[nt * 3];
            for (int i = 0; i < Neighbors.Length; i++) Neighbors[i] = -1;
            var edges = new Dictionary<long, int>(nt * 2);
            long nv = Vertices.Length;
            for (int t = 0; t < nt; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = V(t, k), b = V(t, (k + 1) % 3);
                    long key = a < b ? a * nv + b : b * nv + a;
                    if (edges.TryGetValue(key, out int code))
                    {
                        int other = code / 3, ok = code % 3;
                        if (Neighbors[other * 3 + ok] == -1 && other != t)
                        {
                            Neighbors[other * 3 + ok] = t;
                            Neighbors[t * 3 + k] = other;
                        }
                    }
                    else edges[key] = t * 3 + k;
                }
            }
        }

        /// <summary>Unique vertex indices used by the given triangles.</summary>
        public int[] VerticesOf(IEnumerable<int> triangles)
        {
            var seen = new HashSet<int>();
            foreach (int t in triangles)
                for (int k = 0; k < 3; k++) seen.Add(V(t, k));
            var r = new int[seen.Count];
            seen.CopyTo(r);
            Array.Sort(r);
            return r;
        }

        /// <summary>Returns a copy moved by a rigid transform (same triangle indices).</summary>
        public TriMesh Transformed(RigidTransform x)
        {
            var v = new Vec3[Vertices.Length];
            for (int i = 0; i < v.Length; i++) v[i] = x.Apply(Vertices[i]);
            return new TriMesh(v, (int[])Indices.Clone());
        }

        /// <summary>Returns a copy scaled uniformly (e.g. mm → m).</summary>
        public TriMesh Scaled(double s)
        {
            var v = new Vec3[Vertices.Length];
            for (int i = 0; i < v.Length; i++) v[i] = Vertices[i] * s;
            return new TriMesh(v, (int[])Indices.Clone());
        }
    }
}
