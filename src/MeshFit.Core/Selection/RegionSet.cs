using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Selection
{
    /// <summary>A painted group of triangles that will become one surface.</summary>
    public sealed class Region
    {
        public int Id { get; internal set; }
        public string Name { get; set; }
        /// <summary>Display colour as 0xRRGGBB.</summary>
        public int Color { get; set; }
        public FitRequest Request { get; set; } = FitRequest.Auto;
        public FitResult Fit { get; set; }
        public int TriangleCount { get; internal set; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Assignment of mesh triangles to regions (each triangle belongs to at most one region) with
    /// stroke based undo/redo.
    /// </summary>
    public sealed class RegionSet
    {
        public const int None = -1;
        private static readonly int[] Palette =
        {
            0xE6194B, 0x3CB44B, 0x4363D8, 0xF58231, 0x911EB4, 0x46F0F0, 0xF032E6, 0xBCF60C,
            0xFABEBE, 0x008080, 0xE6BEFF, 0x9A6324, 0xFFE119, 0x800000, 0xAAFFC3, 0x808000
        };

        private readonly int[] _owner;
        private readonly List<Region> _regions = new List<Region>();
        private int _nextId = 1;
        private List<(int tri, int before)> _stroke;
        private readonly Stack<List<(int tri, int before, int after)>> _undo = new Stack<List<(int, int, int)>>();
        private readonly Stack<List<(int tri, int before, int after)>> _redo = new Stack<List<(int, int, int)>>();

        public TriMesh Mesh { get; }
        public IReadOnlyList<Region> Regions => _regions;
        /// <summary>Raised after any change of triangle ownership or the region list.</summary>
        public event EventHandler Changed;

        public RegionSet(TriMesh mesh)
        {
            Mesh = mesh;
            _owner = new int[mesh.TriangleCount];
            for (int i = 0; i < _owner.Length; i++) _owner[i] = None;
        }

        public int OwnerOf(int tri) => _owner[tri];
        public Region Find(int id) => _regions.FirstOrDefault(r => r.Id == id);

        public Region AddRegion(string name = null)
        {
            var r = new Region { Id = _nextId++, Color = Palette[(_nextId - 2) % Palette.Length] };
            r.Name = name ?? "Region " + r.Id;
            _regions.Add(r);
            OnChanged();
            return r;
        }

        /// <summary>Recreates a region with a known id (project loading).</summary>
        public Region RestoreRegion(int id, string name, int color, FitRequest request)
        {
            var r = new Region { Id = id, Name = name, Color = color, Request = request };
            _regions.Add(r);
            _nextId = Math.Max(_nextId, id + 1);
            return r;
        }

        public void RemoveRegion(Region r)
        {
            BeginStroke();
            for (int t = 0; t < _owner.Length; t++) if (_owner[t] == r.Id) Assign(t, None);
            EndStroke();
            _regions.Remove(r);
            OnChanged();
        }

        public IEnumerable<int> TrianglesOf(int regionId)
        {
            for (int t = 0; t < _owner.Length; t++) if (_owner[t] == regionId) yield return t;
        }

        public List<int> TriangleListOf(int regionId) => TrianglesOf(regionId).ToList();

        // ------------------------------------------------------------ strokes / undo

        public void BeginStroke() { if (_stroke == null) _stroke = new List<(int, int)>(); }

        public void EndStroke()
        {
            if (_stroke == null) return;
            var rec = _stroke.Where(s => _owner[s.tri] != s.before).Select(s => (s.tri, s.before, _owner[s.tri])).ToList();
            _stroke = null;
            if (rec.Count > 0) { _undo.Push(rec); _redo.Clear(); }
            UpdateCounts();
            OnChanged();
        }

        /// <summary>Assigns a triangle to a region (or <see cref="None"/>). Call between Begin/EndStroke for undo.</summary>
        public bool Assign(int tri, int regionId)
        {
            if (_owner[tri] == regionId) return false;
            _stroke?.Add((tri, _owner[tri]));
            _owner[tri] = regionId;
            return true;
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var rec = _undo.Pop();
            for (int i = rec.Count - 1; i >= 0; i--) _owner[rec[i].tri] = rec[i].before;
            _redo.Push(rec);
            UpdateCounts(); OnChanged();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var rec = _redo.Pop();
            foreach (var r in rec) _owner[r.tri] = r.after;
            _undo.Push(rec);
            UpdateCounts(); OnChanged();
        }

        public void UpdateCounts()
        {
            var counts = new Dictionary<int, int>();
            foreach (int o in _owner) if (o != None) counts[o] = counts.TryGetValue(o, out int c) ? c + 1 : 1;
            foreach (var r in _regions) r.TriangleCount = counts.TryGetValue(r.Id, out int c) ? c : 0;
        }

        public void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Raw ownership array (read only use).</summary>
        public int[] Owners => _owner;
    }
}
