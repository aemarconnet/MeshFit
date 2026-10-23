using System;
using System.IO;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// SOLIDWORKS does not catch .NET exceptions thrown in window-message callbacks: one escaping exception
    /// terminates SOLIDWORKS. Every UI callback therefore runs through <see cref="Run"/>, which logs the
    /// exception to %LOCALAPPDATA%\MeshFit\MeshFit.log and reports it instead.
    /// </summary>
    internal static class Guarded
    {
        private static readonly object Gate = new object();

        public static string LogPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshFit", "MeshFit.log");

        /// <summary>Receives a one-line description of each caught error (the task pane shows it).</summary>
        public static event Action<string> Reported;

        public static void Run(string where, Action a)
        {
            try { a(); }
            catch (Exception ex) { Report(where, ex); }
        }

        public static void Report(string where, Exception ex)
        {
            Write($"ERROR in {where}: {ex}");
            try { Reported?.Invoke($"Internal error ({where}): {ex.GetBaseException().Message} — details in {LogPath}"); }
            catch { /* never throw from the error path */ }
        }

        /// <summary>
        /// Breadcrumb before/after a heavy or native step, with the process memory, so a crash that leaves no
        /// exception behind still shows in the log where it happened.
        /// </summary>
        public static void Step(string what)
        {
            long mb = 0;
            int gdi = 0, user = 0;
            try
            {
                using (var p = System.Diagnostics.Process.GetCurrentProcess())
                {
                    mb = p.PrivateMemorySize64 >> 20;
                    gdi = GetGuiResources(p.Handle, 0);
                    user = GetGuiResources(p.Handle, 1);
                }
            }
            catch { }
            // Windows allows 10,000 GDI and 10,000 USER handles per process; SOLIDWORKS dies near the limit.
            Write($"· {what} [{mb:N0} MB, GDI {gdi}, USER {user}]" + (gdi > 8000 || user > 8000 ? "  ⚠ handle count near the Windows limit" : ""));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 4 * 1024 * 1024) fi.Delete();
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { /* logging must never fail */ }
        }
    }
}
