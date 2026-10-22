using System;
using System.Diagnostics;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Attaches to one SOLIDWORKS model view: draws the overlay after each frame and forwards mouse
    /// events to the session while a paint tool is active.
    /// </summary>
    internal sealed class ViewHook : IDisposable
    {
        private const int MK_SHIFT = 0x0004, MK_LBUTTON = 0x0001;

        private readonly ModelView _view;
        private readonly IModelDoc2 _doc;
        private readonly Mouse _mouse;
        private readonly IMathUtility _math;
        private readonly Session _session;
        private readonly OverlayRenderer _renderer;
        private ScreenProjector _projector;
        private readonly Stopwatch _redrawClock = Stopwatch.StartNew();
        private bool _disposed;

        public event Action Destroyed;
        public ModelView View => _view;

        public ViewHook(ModelView view, IModelDoc2 doc, IMathUtility math, Session session, OverlayRenderer renderer)
        {
            _view = view; _doc = doc; _math = math; _session = session; _renderer = renderer;
            _view.BufferSwapNotify += OnBufferSwap;
            _view.DestroyNotify2 += OnDestroy;
            _view.ViewChangeNotify += OnViewChange;
            _mouse = _view.GetMouse();
            _mouse.MouseLBtnDownNotify += OnLBtnDown;
            _mouse.MouseLBtnUpNotify += OnLBtnUp;
            _mouse.MouseMoveNotify += OnMove;
            _session.HoverChanged += ThrottledRedraw;
        }

        private ScreenProjector Projector => _projector ?? (_projector = new ScreenProjector(_view, _math));

        private int OnViewChange(object view)
        {
            _projector = null;
            return 0;
        }

        private int OnBufferSwap()
        {
            try
            {
                // Refresh the pick projection every frame: any rotate/zoom/pan ends with a redraw.
                _projector = new ScreenProjector(_view, _math);
                _renderer.Draw(_projector.PixelSize);
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit draw: " + ex); }
            return 0;
        }

        private int OnLBtnDown(int x, int y, int wParam)
        {
            if (_session.Tool == PaintTool.None || !_session.HasMesh || _session.Busy) return 0;
            try
            {
                Projector.PickLine(x, y, out var p, out var d);
                _session.MouseDown(p, d, (wParam & MK_SHIFT) != 0);
                Redraw();
            }
            catch (Exception ex) { _session.Say("Paint failed: " + ex.Message); }
            return 0;
        }

        private int OnLBtnUp(int x, int y, int wParam)
        {
            if (_session.Tool == PaintTool.None) return 0;
            _session.MouseUp();
            // Painting is not a selection: drop whatever SOLIDWORKS selected under the cursor.
            _doc.ClearSelection2(true);
            Redraw();
            return 0;
        }

        private int OnMove(int x, int y, int wParam)
        {
            if (!_session.HasMesh || _session.Busy) return 0;
            // Without a tool, only track the cursor when the deviation readout needs it.
            if (_session.Tool == PaintTool.None && !_session.ShowDeviation) return 0;
            try
            {
                if ((wParam & MK_LBUTTON) == 0 && _session.IsStroking) _session.MouseUp();
                Projector.PickLine(x, y, out var p, out var d);
                _session.Hover(p, d);
                if (_session.IsStroking) ThrottledRedraw();
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit move: " + ex); }
            return 0;
        }

        private void ThrottledRedraw()
        {
            if (_redrawClock.ElapsedMilliseconds < 30) return;
            Redraw();
        }

        public void Redraw()
        {
            _redrawClock.Restart();
            try { _view.GraphicsRedraw(null); } catch { /* view closing */ }
        }

        private int OnDestroy(int destroyType)
        {
            Dispose();
            Destroyed?.Invoke();
            return 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _session.HoverChanged -= ThrottledRedraw;
            try
            {
                _view.BufferSwapNotify -= OnBufferSwap;
                _view.DestroyNotify2 -= OnDestroy;
                _view.ViewChangeNotify -= OnViewChange;
                _mouse.MouseLBtnDownNotify -= OnLBtnDown;
                _mouse.MouseLBtnUpNotify -= OnLBtnUp;
                _mouse.MouseMoveNotify -= OnMove;
            }
            catch { /* SOLIDWORKS already released the view */ }
        }
    }
}
