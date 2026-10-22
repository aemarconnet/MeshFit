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
        /// <summary>Name of the CAD surface feature created from this region (null if none).</summary>
        public string FeatureName { get; set; }
        public bool SurfaceHidden { get; set; }
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
        private string _strokeLabel;
        private readonly List<(Region region, int index)> _strokeRemoved = new List<(Region, int)>();
        private readonly List<Region> _strokeAdded = new List<Region>();
        private readonly Stack<UndoRecord> _undo = new Stack<UndoRecord>();
        private readonly Stack<UndoRecord> _redo = new Stack<UndoRecord>();

        private sealed class UndoRecord
        {
            public string Label;
            public List<(int tri, int before, int after)> Changes;
            /// <summary>Regions deleted by this step (restored on undo) with their list positions.</summary>
            public List<(Region region, int index)> Removed;
            /// <summary>Regions created inside this step (removed on undo).</summary>
            public List<Region> Added;
        }

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
            RecordAdded(r);
            OnChanged();
            return r;
        }

        private void RecordAdded(Region r) { if (_stroke != null) _strokeAdded.Add(r); }

        /// <summary>Recreates a region with a known id (project loading).</summary>
        public Region RestoreRegion(int id, string name, int color, FitRequest request)
        {
            var r = new Region { Id = id, Name = name, Color = color, Request = request };
            _regions.Add(r);
            _nextId = Math.Max(_nextId, id + 1);
            return r;
        }

        /// <summary>Deletes a region and frees its triangles. Undoable (the region comes back on undo).</summary>
        public void RemoveRegion(Region r)
        {
            bool own = _stroke == null;
            if (own) BeginStroke("Delete " + r.Name);
            for (int t = 0; t < _owner.Length; t++) if (_owner[t] == r.Id) Assign(t, None);
            int idx = _regions.IndexOf(r);
            if (idx >= 0) { _strokeRemoved.Add((r, idx)); _regions.RemoveAt(idx); }
            if (own) EndStroke();
            else OnChanged();
        }

        public IEnumerable<int> TrianglesOf(int regionId)
        {
            for (int t = 0; t < _owner.Length; t++) if (_owner[t] == regionId) yield return t;
        }

        public List<int> TriangleListOf(int regionId) => TrianglesOf(regionId).ToList();

        // ------------------------------------------------------------ strokes / undo

        /// <summary>Starts recording an undoable step. Nested calls join the open step.</summary>
        public void BeginStroke(string label = null)
        {
            if (_stroke != null) return;
            _stroke = new List<(int, int)>();
            _strokeLabel = label;
            _strokeRemoved.Clear();
            _strokeAdded.Clear();
        }

        public void EndStroke()
        {
            if (_stroke == null) return;
            var rec = new UndoRecord
            {
                Label = _strokeLabel ?? "Paint",
                Changes = _stroke.Where(s => _owner[s.tri] != s.before).Select(s => (s.tri, s.before, _owner[s.tri])).ToList(),
                Removed = new List<(Region, int)>(_strokeRemoved),
                Added = new List<Region>(_strokeAdded)
            };
            _stroke = null;
            _strokeRemoved.Clear();
            _strokeAdded.Clear();
            if (rec.Changes.Count > 0 || rec.Removed.Count > 0 || rec.Added.Count > 0) { _undo.Push(rec); _redo.Clear(); }
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
        /// <summary>Description of the step Undo would revert (null if none).</summary>
        public string UndoLabel => _undo.Count > 0 ? _undo.Peek().Label : null;
        public string RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var rec = _undo.Pop();
            for (int i = rec.Removed.Count - 1; i >= 0; i--)
                _regions.Insert(Math.Min(rec.Removed[i].index, _regions.Count), rec.Removed[i].region);
            for (int i = rec.Changes.Count - 1; i >= 0; i--) _owner[rec.Changes[i].tri] = rec.Changes[i].before;
            foreach (var r in rec.Added) _regions.Remove(r);
            _redo.Push(rec);
            UpdateCounts(); OnChanged();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var rec = _redo.Pop();
            foreach (var r in rec.Added) if (!_regions.Contains(r)) _regions.Add(r);
            foreach (var r in rec.Changes) _owner[r.tri] = r.after;
            foreach (var (region, _) in rec.Removed) _regions.Remove(region);
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
