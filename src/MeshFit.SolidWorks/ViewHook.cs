using System;
using System.Diagnostics;
using System.Windows.Forms;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Attaches to one SOLIDWORKS model view: draws the overlay after each frame and handles painting input.
    /// While a paint tool is active, left-button and modified-wheel messages are taken directly from the
    /// view window (subclassed), so SOLIDWORKS never sees them: no selection box, no zoom on Ctrl/Shift+wheel.
    /// Middle button (rotate/pan), plain wheel (zoom) and right button (context menu) still reach SOLIDWORKS.
    /// If the window cannot be subclassed, the SOLIDWORKS mouse notifications are used as a fallback.
    /// </summary>
    internal sealed class ViewHook : IDisposable
    {
        private const int MK_LBUTTON = 0x0001, MK_SHIFT = 0x0004, MK_CONTROL = 0x0008;
        private const int WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
                          WM_LBUTTONDBLCLK = 0x0203, WM_MOUSEWHEEL = 0x020A, WM_CAPTURECHANGED = 0x0215;

        private readonly ModelView _view;
        private readonly IModelDoc2 _doc;
        private readonly Mouse _mouse;
        private readonly IMathUtility _math;
        private readonly Session _session;
        private readonly OverlayRenderer _renderer;
        private readonly InputWindow _input;
        private ScreenProjector _projector;
        private readonly Stopwatch _redrawClock = Stopwatch.StartNew();
        private bool _disposed;

        public event Action Destroyed;
        /// <summary>Brush radius changed by the wheel (UI should refresh).</summary>
        public event Action BrushResized;
        public ModelView View => _view;

        public ViewHook(ModelView view, IModelDoc2 doc, IMathUtility math, Session session, OverlayRenderer renderer)
        {
            _view = view; _doc = doc; _math = math; _session = session; _renderer = renderer;
            _view.BufferSwapNotify += OnBufferSwap;
            _view.DestroyNotify2 += OnDestroy;
            _view.ViewChangeNotify += OnViewChange;
            _session.HoverChanged += ThrottledRedraw;

            try
            {
                var hwnd = new IntPtr(_view.GetViewHWndx64());
                if (hwnd != IntPtr.Zero) _input = new InputWindow(this, hwnd);
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit: view subclass failed, using mouse notifications: " + ex.Message); }

            _mouse = _view.GetMouse();
            _mouse.MouseLBtnDownNotify += (x, y, w) => _input == null ? Down(x, y, w) : 0;
            _mouse.MouseLBtnUpNotify += (x, y, w) => _input == null ? Up() : 0;
            _mouse.MouseMoveNotify += (x, y, w) => _input == null ? Move(x, y, w) : 0;
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

        private bool PaintActive => _session.Tool != PaintTool.None && _session.HasMesh && !_session.Busy;

        // ------------------------------------------------------------------ input handlers

        private int Down(int x, int y, int wParam)
        {
            if (!PaintActive) return 0;
            try
            {
                bool shift = (wParam & MK_SHIFT) != 0, ctrl = (wParam & MK_CONTROL) != 0;
                Projector.PickLine(x, y, out var p, out var d);
                // Remember the anchor before the stroke starts (Ctrl+click paints from it).
                var anchor = _session.LastPaintPoint;
                double anchorRadius = _session.LastPaintRadius;
                if (!_session.BeginStroke(p, d, shift)) { Redraw(); return 0; }
                if (ctrl && anchor.HasValue && _session.Tool != PaintTool.PickRegion)
                    PaintLine(anchor.Value, anchorRadius, x, y, _session.BrushRadius);
                else
                    _session.StrokeSample(p, d, _session.BrushRadius);
                Redraw();
            }
            catch (Exception ex) { _session.Say("Paint failed: " + ex.Message); }
            return 0;
        }

        /// <summary>
        /// Straight line in screen space (current view) from the anchor point to the pixel, sampled so the
        /// brush overlaps, with the radius blended from the anchor's to the current one.
        /// </summary>
        private void PaintLine(Vec3 anchor, double r0, int x1, int y1, double r1)
        {
            var proj = Projector;
            proj.ToPixel(anchor, out double x0, out double y0);
            double px = proj.PixelSize;
            double lenPx = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            double stepPx = Math.Max(1, 0.4 * Math.Min(r0, r1) / px);
            int n = (int)Math.Min(2000, Math.Ceiling(lenPx / stepPx));
            for (int i = 0; i <= n; i++)
            {
                double f = n == 0 ? 1 : (double)i / n;
                proj.PickLine(x0 + (x1 - x0) * f, y0 + (y1 - y0) * f, out var p, out var d);
                _session.StrokeSample(p, d, r0 + (r1 - r0) * f);
            }
        }

        private int Up()
        {
            if (!_session.IsStroking) return 0;
            _session.EndStroke();
            Redraw();
            return 0;
        }

        private int Move(int x, int y, int wParam)
        {
            if (!_session.HasMesh || _session.Busy) return 0;
            // Without a tool, only track the cursor when the deviation readout needs it.
            if (_session.Tool == PaintTool.None && !_session.ShowDeviation) return 0;
            try
            {
                if ((wParam & MK_LBUTTON) == 0 && _session.IsStroking) _session.EndStroke();
                _session.LinePreview = (wParam & MK_CONTROL) != 0 && _session.LastPaintPoint.HasValue && !_session.IsStroking;
                Projector.PickLine(x, y, out var p, out var d);
                _session.Hover(p, d);
                if (_session.IsStroking)
                {
                    _session.StrokeSample(p, d, _session.BrushRadius);
                    ThrottledRedraw();
                }
            }
            catch (Exception ex) { Trace.WriteLine("MeshFit move: " + ex); }
            return 0;
        }

        /// <summary>Ctrl/Shift + wheel: resize the brush by 10% per notch (Photoshop style).</summary>
        private void Wheel(int delta)
        {
            double f = Math.Pow(1.1, delta / 120.0);
            _session.BrushRadius = Math.Max(1e-5, Math.Min(10, _session.BrushRadius * f));
            BrushResized?.Invoke();
            Redraw();
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
            try { _input?.ReleaseHandle(); } catch { }
            try
            {
                _view.BufferSwapNotify -= OnBufferSwap;
                _view.DestroyNotify2 -= OnDestroy;
                _view.ViewChangeNotify -= OnViewChange;
            }
            catch { /* SOLIDWORKS already released the view */ }
        }

        // ------------------------------------------------------------------ window subclass

        /// <summary>Subclasses the view window to take left-button and modified-wheel input while painting.</summary>
        private sealed class InputWindow : NativeWindow
        {
            private readonly ViewHook _h;
            private bool _captured;

            public InputWindow(ViewHook h, IntPtr hwnd)
            {
                _h = h;
                AssignHandle(hwnd);
            }

            private static int X(IntPtr lp) => (short)((long)lp & 0xFFFF);
            private static int Y(IntPtr lp) => (short)(((long)lp >> 16) & 0xFFFF);

            protected override void WndProc(ref Message m)
            {
                try
                {
                    bool paint = _h.PaintActive;
                    switch (m.Msg)
                    {
                        case WM_LBUTTONDOWN:
                        case WM_LBUTTONDBLCLK:
                            if (!paint) break;
                            Capture(true);
                            _h.Down(X(m.LParam), Y(m.LParam), (int)(long)m.WParam);
                            return; // swallow: no SOLIDWORKS selection or selection box
                        case WM_LBUTTONUP:
                            if (!paint && !_captured) break;
                            _h.Up();
                            Capture(false);
                            if (paint) return;
                            break;
                        case WM_MOUSEMOVE:
                            _h.Move(X(m.LParam), Y(m.LParam), (int)(long)m.WParam);
                            if (paint && ((int)(long)m.WParam & MK_LBUTTON) != 0) return;
                            break;
                        case WM_MOUSEWHEEL:
                        {
                            int keys = (int)((long)m.WParam & 0xFFFF);
                            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                            if (paint && (keys & (MK_CONTROL | MK_SHIFT)) != 0) { _h.Wheel(delta); return; }
                            break;
                        }
                        case WM_CAPTURECHANGED:
                            if (_captured && m.LParam != Handle) { _captured = false; _h.Up(); }
                            break;
                    }
                }
                catch (Exception ex) { Trace.WriteLine("MeshFit input: " + ex); }
                base.WndProc(ref m);
            }

            private void Capture(bool on)
            {
                if (on) { _captured = true; SetCapture(Handle); }
                else if (_captured) { _captured = false; ReleaseCapture(); }
            }

            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
            [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
        }
    }
}
