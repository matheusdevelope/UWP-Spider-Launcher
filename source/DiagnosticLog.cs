using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace SpiderManInstaller
{
    // LocalState log for Xbox Device Portal / Copy debug log. Writes only to
    // ApplicationData (D:), never to the SSD, so logging cannot fail the same
    // way removable I/O fails.
    static class DiagnosticLog
    {
        static readonly object Gate = new object();
        const string FileName = "launcher-debug.log";
        const string LastName = "launcher-debug-last.txt";
        const long MaxBytes = 768 * 1024;

        internal static string Folder
        {
            get
            {
                try { return ApplicationData.Current.LocalFolder.Path; }
                catch { return null; }
            }
        }
        internal static string FilePath
        {
            get
            {
                string folder = Folder;
                return String.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
            }
        }

        internal static void Line(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            try
            {
                string path = FilePath;
                if (path == null) return;
                string line = DateTime.UtcNow.ToString("o") + " " + text.Replace('\r', ' ').Replace('\n', ' ') + "\n";
                lock (Gate)
                {
                    Trim(path);
                    File.AppendAllText(path, line);
                }
            }
            catch { }
        }

        internal static void Error(string where, Exception error)
        {
            var text = new StringBuilder();
            text.Append("ERROR ").Append(where ?? "unknown");
            for (var current = error; current != null; current = current.InnerException)
            {
                text.Append(" | ").Append(current.GetType().FullName).Append(": ").Append(current.Message);
                text.Append(" HRESULT=0x").Append(current.HResult.ToString("X8"));
            }
            Line(text.ToString());
            try
            {
                string folder = Folder;
                if (folder == null || error == null) return;
                File.WriteAllText(Path.Combine(folder, LastName),
                    DateTime.UtcNow.ToString("o") + "\n" + where + "\n" + error);
            }
            catch { }
        }

        internal static string ReadAll()
        {
            try
            {
                string path = FilePath;
                if (path != null && File.Exists(path)) return File.ReadAllText(path);
            }
            catch { }
            return "(empty log)";
        }

        static void Trim(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;
                string text = File.ReadAllText(path);
                int cut = text.Length / 2;
                int line = text.IndexOf('\n', cut);
                if (line > 0) text = text.Substring(line + 1);
                File.WriteAllText(path, text);
            }
            catch { }
        }
    }
}
