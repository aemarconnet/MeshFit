using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Esc detection that works wherever the keyboard focus is inside SOLIDWORKS (graphics area, task
    /// pane, feature tree), by polling the key state while a SOLIDWORKS window is in the foreground.
    /// </summary>
    internal static class EscKey
    {
        private const int VK_ESCAPE = 0x1B;
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        private static readonly uint OurPid = (uint)Process.GetCurrentProcess().Id;

        public static bool IsDown()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == OurPid && (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
        }
    }
}
