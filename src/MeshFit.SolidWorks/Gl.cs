using System;
using System.Runtime.InteropServices;

namespace MeshFit.SolidWorks
{
    /// <summary>Legacy (compatibility profile) OpenGL 1.1 entry points used to draw into the SOLIDWORKS view.</summary>
    internal static class Gl
    {
        private const string Lib = "opengl32.dll";

        public const uint GL_TRIANGLES = 0x0004, GL_LINES = 0x0001, GL_LINE_LOOP = 0x0002, GL_LINE_STRIP = 0x0003, GL_POINTS = 0x0000;
        public const uint GL_DEPTH_TEST = 0x0B71, GL_LIGHTING = 0x0B50, GL_BLEND = 0x0BE2, GL_CULL_FACE = 0x0B44;
        public const uint GL_POLYGON_OFFSET_FILL = 0x8037, GL_LINE_SMOOTH = 0x0B20, GL_TEXTURE_2D = 0x0DE1;
        public const uint GL_SRC_ALPHA = 0x0302, GL_ONE_MINUS_SRC_ALPHA = 0x0303, GL_LEQUAL = 0x0203;
        public const uint GL_ALL_ATTRIB_BITS = 0x000FFFFF, GL_CLIENT_ALL_ATTRIB_BITS = 0xFFFFFFFF;
        public const uint GL_VERTEX_ARRAY = 0x8074, GL_COLOR_ARRAY = 0x8076, GL_FLOAT = 0x1406, GL_UNSIGNED_BYTE = 0x1401;

        [DllImport(Lib)] public static extern void glPushAttrib(uint mask);
        [DllImport(Lib)] public static extern void glPopAttrib();
        [DllImport(Lib)] public static extern void glPushClientAttrib(uint mask);
        [DllImport(Lib)] public static extern void glPopClientAttrib();
        [DllImport(Lib)] public static extern void glEnable(uint cap);
        [DllImport(Lib)] public static extern void glDisable(uint cap);
        [DllImport(Lib)] public static extern void glBlendFunc(uint s, uint d);
        [DllImport(Lib)] public static extern void glDepthFunc(uint f);
        [DllImport(Lib)] public static extern void glDepthMask(byte flag);
        [DllImport(Lib)] public static extern void glPolygonOffset(float factor, float units);
        [DllImport(Lib)] public static extern void glLineWidth(float w);
        [DllImport(Lib)] public static extern void glPointSize(float s);
        [DllImport(Lib)] public static extern void glBegin(uint mode);
        [DllImport(Lib)] public static extern void glEnd();
        [DllImport(Lib)] public static extern void glVertex3d(double x, double y, double z);
        [DllImport(Lib)] public static extern void glColor4ub(byte r, byte g, byte b, byte a);
        [DllImport(Lib)] public static extern void glEnableClientState(uint array);
        [DllImport(Lib)] public static extern void glDisableClientState(uint array);
        [DllImport(Lib)] public static extern void glVertexPointer(int size, uint type, int stride, IntPtr ptr);
        [DllImport(Lib)] public static extern void glColorPointer(int size, uint type, int stride, IntPtr ptr);
        [DllImport(Lib)] public static extern void glDrawArrays(uint mode, int first, int count);
        [DllImport(Lib)] public static extern IntPtr wglGetCurrentContext();
        [DllImport(Lib)] public static extern uint glGenLists(int range);
        [DllImport(Lib)] public static extern void glNewList(uint list, uint mode);
        [DllImport(Lib)] public static extern void glEndList();
        [DllImport(Lib)] public static extern void glCallList(uint list);
        [DllImport(Lib)] public static extern void glDeleteLists(uint list, int range);
        public const uint GL_COMPILE = 0x1300;

        public static void Color(int rgb, byte alpha) =>
            glColor4ub((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);
    }
}
