using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpiderManInstaller
{
    public interface IFolderImportSource
    {
        Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancel);
    }

    public static class FolderImportEngine
    {
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        static async Task<string> Hash(Stream stream, CancellationToken cancel)
        {
            using (var hash = SHA256.Create())
            {
                byte[] buffer = new byte[1024 * 1024]; int count;
                while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(hash.Hash);
            }
        }

        public static async Task ImportAsync(IFolderImportSource source, string destination,
            IList<ExpectedFile> expected, long freeSpace, Action<long,long,string> progress, CancellationToken cancel, IList<LanguagePack> languages = null)
        {
            cancel.ThrowIfCancellationRequested();
            if (GameStorage.AccessibleDirectory(destination) || GameStorage.AccessibleFile(destination)) throw new IOException("Import destination already exists.");
            if (expected == null || expected.Count < 2) throw new InvalidDataException("Invalid version manifest.");
            using(var executable=await source.OpenReadAsync("Spider-Man.exe",cancel).ConfigureAwait(false)) {
                if(!executable.CanSeek)throw new InvalidDataException("Game executable must be seekable.");
                expected=GameVersionSupport.Select(expected,executable.Length,await Hash(executable,cancel).ConfigureAwait(false));
            }
            if (languages != null) expected = await LanguageImports.SelectAsync(source, expected, languages, cancel).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long total = 0;
            foreach (var row in expected)
            {
                string name = ImportEngine.SafePath(row.Path);
                if (name.EndsWith("/") || !names.Add(name) || row.Size < 0 || row.Hash == null ||
                    row.Hash.Length != 64 || row.Hash.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new InvalidDataException("Invalid or duplicate manifest entry.");
                string target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe destination.");
                total = checked(total + row.Size);
            }
            if (!names.Contains("Spider-Man.exe") || !names.Contains("asset_archive/toc")) throw new InvalidDataException("Missing version fingerprints.");
            if (total > ImportEngine.MaxArchiveSize || freeSpace < checked(total + ImportEngine.Margin))
                throw new IOException("Not enough storage: need game size plus 1 GiB free.");
            // Fingerprints first: reject an unsupported version before copying archives.
            foreach (string name in new[] { "Spider-Man.exe", "asset_archive/toc" })
            {
                var row = expected.Single(r => r.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
                using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                {
                    if (!input.CanSeek || input.Length != row.Size ||
                        !String.Equals(await Hash(input, cancel).ConfigureAwait(false), row.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsupported or incomplete Steam version: " + name);
                }
            }
            // Check accessibility/length of every required file, including extensionless
            // archives, before creating a potentially very large partial installation.
            foreach (var row in expected)
            {
                cancel.ThrowIfCancellationRequested();
                using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                    if (!input.CanSeek || input.Length != row.Size) throw new InvalidDataException("Missing or wrong-size game file: " + row.Path);
            }
            GameStorage.EnsureWritable(destination);
            var buffer = new byte[1024 * 1024]; long done = 0;
            foreach (var row in expected)
            {
                cancel.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, row.Path.Replace('/', Path.DirectorySeparatorChar));
                GameStorage.EnsureWritable(Path.GetDirectoryName(target));
                string partial = target + ".partial";
                using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                using (var output = GameStorage.OpenCreate(partial, FileAccess.Write))
                using (var hash = SHA256.Create())
                {
                    if (input.Length != row.Size) throw new InvalidDataException("Source changed: " + row.Path);
                    int count; long written = 0;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancel).ConfigureAwait(false)) != 0)
                    {
                        cancel.ThrowIfCancellationRequested(); written = checked(written + count);
                        if (written > row.Size) throw new InvalidDataException("Source grew during copy: " + row.Path);
                        await output.WriteAsync(buffer, 0, count, cancel).ConfigureAwait(false);
                        hash.TransformBlock(buffer, 0, count, buffer, 0); done += count;
                        progress?.Invoke(done, total, row.Path);
                    }
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    if (written != row.Size || !Hex(hash.Hash).Equals(row.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Game file failed SHA-256 verification: " + row.Path);
                    await output.FlushAsync(cancel).ConfigureAwait(false);
                }
                GameStorage.MoveFile(partial, target);
            }
            cancel.ThrowIfCancellationRequested();
            GameStorage.WriteText(Path.Combine(destination, "import-receipt.txt"),
                "Original Steam folder copied and SHA-256 verified.\r\nImported UTC: " + DateTime.UtcNow.ToString("o") +
                "\r\nFiles: " + expected.Count + "\r\nBytes: " + total +
                "\r\n" + (languages == null ? "English audio" : LanguageImports.Description(expected, languages)) + "\r\nReady for game preparation.\r\n");
            progress?.Invoke(total, total, "Import complete");
        }

        // Keep the Steam folder where it is (USB/SSD). Prepare still verifies every archive.
        public static async Task VerifyInPlaceAsync(IFolderImportSource source, string folder,
            IList<ExpectedFile> expected, Action<long,long,string> progress, CancellationToken cancel, IList<LanguagePack> languages = null)
        {
            cancel.ThrowIfCancellationRequested();
            if (String.IsNullOrEmpty(folder)) throw new DirectoryNotFoundException("Game folder not found: " + folder);
            // Xbox AppContainer System.IO returns false on removable E:; verify
            // through the StorageFolder broker only.
            string root;
            try { root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch (Exception ex) { throw new DirectoryNotFoundException("Game folder not found: " + folder, ex); }
            using (var executable = await source.OpenReadAsync("Spider-Man.exe", cancel).ConfigureAwait(false))
            {
                if (!executable.CanSeek) throw new InvalidDataException("Game executable must be seekable.");
                expected = GameVersionSupport.Select(expected, executable.Length, await Hash(executable, cancel).ConfigureAwait(false));
            }
            if (languages != null) expected = await LanguageImports.SelectAsync(source, expected, languages, cancel).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var row in expected)
            {
                string name = ImportEngine.SafePath(row.Path);
                if (name.EndsWith("/") || !names.Add(name) || row.Size < 0 || row.Hash == null ||
                    row.Hash.Length != 64 || row.Hash.Any(ch => !Uri.IsHexDigit(ch)))
                    throw new InvalidDataException("Invalid or duplicate manifest entry.");
                string target = Path.GetFullPath(Path.Combine(folder, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe destination.");
                total = checked(total + row.Size);
            }
            if (!names.Contains("Spider-Man.exe") || !names.Contains("asset_archive/toc")) throw new InvalidDataException("Missing version fingerprints.");
            foreach (string name in new[] { "Spider-Man.exe", "asset_archive/toc" })
            {
                var row = expected.Single(r => r.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
                using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                {
                    if (!input.CanSeek || input.Length != row.Size ||
                        !String.Equals(await Hash(input, cancel).ConfigureAwait(false), row.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsupported or incomplete Steam version: " + name);
                }
            }
            long done = 0;
            foreach (var row in expected)
            {
                cancel.ThrowIfCancellationRequested();
                using (var input = await source.OpenReadAsync(row.Path, cancel).ConfigureAwait(false))
                    if (!input.CanSeek || input.Length != row.Size) throw new InvalidDataException("Missing or wrong-size game file: " + row.Path);
                done = checked(done + row.Size);
                progress?.Invoke(done, total, row.Path);
            }
            GameStorage.WriteText(Path.Combine(folder, "import-inplace.txt"), folder + "\r\nUTC: " + DateTime.UtcNow.ToString("o") + "\r\n");
            GameStorage.WriteText(Path.Combine(folder, "import-receipt.txt"),
                "Original Steam folder verified in place. Files were not copied.\r\nImported UTC: " + DateTime.UtcNow.ToString("o") +
                "\r\nFiles: " + expected.Count + "\r\nBytes: " + total +
                "\r\n" + (languages == null ? "English audio" : LanguageImports.Description(expected, languages)) + "\r\nReady for game preparation.\r\n");
            progress?.Invoke(total, total, "Verified in place");
        }
    }
}
