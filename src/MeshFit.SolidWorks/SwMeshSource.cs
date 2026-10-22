using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace MeshFit.SolidWorks
{
    /// <summary>Builds a triangle mesh from SOLIDWORKS bodies (mesh BREP bodies or ordinary bodies) via face tessellation.</summary>
    internal static class SwMeshSource
    {
        /// <summary>Bodies of the selected faces/bodies, or every visible body of the part when nothing is selected.</summary>
        public static TriMesh FromSelectionOrPart(IModelDoc2 doc, out string description)
        {
            var bodies = new List<Body2>();
            var names = new HashSet<string>();
            var sel = (ISelectionMgr)doc.SelectionManager;
            int n = sel.GetSelectedObjectCount2(-1);
            for (int i = 1; i <= n; i++)
            {
                object o = sel.GetSelectedObject6(i, -1);
                Body2 b = o as Body2 ?? (o as Face2)?.GetBody() as Body2;
                if (b != null && names.Add(b.Name)) bodies.Add(b);
            }
            if (bodies.Count == 0 && doc is PartDoc part)
            {
                var all = part.GetBodies2((int)swBodyType_e.swAllBodies, true) as object[];
                if (all != null) foreach (Body2 b in all) bodies.Add(b);
            }
            if (bodies.Count == 0) throw new InvalidOperationException("No bodies found. Select a mesh body (or one of its faces) first.");

            var verts = new List<Vec3>();
            var map = new Dictionary<(float, float, float), int>();
            var idx = new List<int>();
            foreach (var body in bodies)
            {
                var faces = body.GetFaces() as object[];
                if (faces == null) continue;
                foreach (Face2 f in faces)
                {
                    var tess = f.GetTessTriangles(true) as float[];
                    if (tess == null) continue;
                    for (int k = 0; k + 2 < tess.Length; k += 3)
                    {
                        var key = (tess[k], tess[k + 1], tess[k + 2]);
                        if (!map.TryGetValue(key, out int vi))
                        {
                            vi = verts.Count;
                            verts.Add(new Vec3(key.Item1, key.Item2, key.Item3));
                            map[key] = vi;
                        }
                        idx.Add(vi);
                    }
                }
            }
            description = bodies.Count == 1 ? "Body " + bodies[0].Name : bodies.Count + " bodies";
            return new TriMesh(verts.ToArray(), idx.ToArray());
        }
    }
}
