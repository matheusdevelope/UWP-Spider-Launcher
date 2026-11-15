using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SpiderManInstaller
{
    // Only the coordinator supplies the fixed runtime root and packaged recipe.
    // No recursive deletion: every individual file is checked before removal.
    public sealed class GameContentCleanup
    {
        readonly string root;
        readonly string archiveFolder;
        readonly List<string> files;
        public long Bytes { get; private set; }
        public int Count => files.Count;
        public bool ArchivesLinked { get; }
        public GameContentCleanup(string runtimeRoot, IEnumerable<string> destinations)
        {
            root = Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar);
            archiveFolder = Path.Combine(root, "StartupContent", "asset_archive");
            ArchivesLinked = GameStorage.IsLink(archiveFolder);
            Check(root);
            if (!File.Exists(Path.Combine(root, "runtime-bootstrap.json")) ||
                !File.Exists(Path.Combine(root, "preparation-source.txt")))
                throw new IOException("No launcher-owned preparation found. Nothing was deleted.");
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string destination in destinations)
            {
                string safe = ImportEngine.SafePath(destination);
                if (safe.Split('/').Any(p => p.Equals("SAVE", StringComparison.OrdinalIgnoreCase) || p.Equals("LocalState", StringComparison.OrdinalIgnoreCase)) || safe.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (safe.Equals("SpiderManNativeStartup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (ArchivesLinked && safe.StartsWith("asset_archive/", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(Resolve("StartupContent/" + safe));
                candidates.Add(Resolve("StartupContent/" + safe + ".prepare-partial"));
            }
            if (!ArchivesLinked) Collect(archiveFolder, candidates);
            Collect(Resolve("PreparedOverlays"), candidates);
            candidates.Add(Resolve("original-asset-toc"));
            files = candidates.Where(File.Exists).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string file in files) { Check(file); Bytes = checked(Bytes + new FileInfo(file).Length); }
        }
        string Resolve(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, ImportEngine.SafePath(relative).Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup path escaped the game folder.");
            Check(path); return path;
        }
        void Check(string path)
        {
            for (string current = Path.GetFullPath(path); !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if (!(File.Exists(current) || Directory.Exists(current))) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0) continue;
                if (ArchivesLinked && current.Equals(archiveFolder, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Cleanup refuses to follow the SSD game folder. Only the internal link is removed.");
                throw new IOException("Cleanup refuses linked files or folders.");
            }
        }
        void Collect(string directory, HashSet<string> candidates)
        {
            Check(directory);
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.GetFiles(directory)) { Check(file); candidates.Add(file); }
            foreach (string child in Directory.GetDirectories(directory)) Collect(child, candidates);
        }
        public void Delete(Action<int, int> progress)
        {
            // Fail before changing readiness when files are in use or read-only.
            foreach (string file in files)
            {
                Check(file);
                if (!File.Exists(file)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0) throw new IOException("A game file is read-only. Nothing was deleted.");
                using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            }
            string pending = Resolve("deletion-pending.txt");
            File.WriteAllText(pending, "Game file deletion started " + DateTime.UtcNow.ToString("o"));
            File.Delete(Resolve("prepared-receipt.txt"));
            int done = 0;
            foreach (string file in files)
            {
                Check(file); File.Delete(file); progress?.Invoke(++done, files.Count);
            }
            if (ArchivesLinked)
            {
                GameStorage.RemoveLink(archiveFolder);
                if (GameStorage.IsLink(archiveFolder)) throw new IOException("The SSD archive link could not be removed. Close the game and retry.");
            }
            else RemoveEmpty(archiveFolder);
            RemoveEmpty(Resolve("PreparedOverlays"));
            if ((!ArchivesLinked && Directory.Exists(archiveFolder)) || Directory.Exists(Resolve("PreparedOverlays")))
                throw new IOException("New game files appeared during cleanup. Close the game and retry.");
            try { File.Delete(Path.Combine(root, "archive-link.txt")); } catch { }
            // Journal stays until completion so a partial deletion can be retried.
            File.Delete(Resolve("preparation-source.txt"));
            File.Delete(pending);
        }
        void RemoveEmpty(string directory)
        {
            Check(directory);
            if (!Directory.Exists(directory)) return;
            foreach (string child in Directory.GetDirectories(directory)) RemoveEmpty(child);
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
}
