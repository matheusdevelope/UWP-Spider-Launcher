using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Streams;
using Windows.System.Profile;
namespace SpiderManInstaller
{
    // Xbox removable storage (E: and other USB/SSD volumes) is only usable
    // through StorageFolder: RemovableDevices, FutureAccessList, GetFileAsync,
    // CreateFileAsync, CreateFolderAsync, and System.FreeSpace. System.IO and
    // CreateFile2FromAppW return error 5 or false on those volumes, including
    // for .exe files. Internal D:, C:, LocalFolder, and the package stay on
    // ordinary file APIs. Directory links from the D: runtime host to SSD
    // archives are remapped back to the SSD path before any I/O.
    static class GameStorage
    {
        internal const string ExternalFolder = @"dumps\SpiderMan";
        internal const string AccessToken = "game-storage";
        const string SettingsRoot = "game-storage-root";
        const string SettingsKind = "game-storage-kind";
        const string ExtraImportsKey = "game-import-folders";
        const string FolderFileName = "game-folder.txt";
        const uint DirectoryLinkFlags = 1u | 2u; // DIRECTORY | ALLOW_UNPRIVILEGED_CREATE

        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

        [DllImport("OwnedShaderConvert.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateDirectoryFromAppCompat(string path);
        [DllImport("OwnedShaderConvert.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool RemoveDirectoryFromAppCompat(string path);
        [DllImport("OwnedShaderConvert.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        static extern byte CreateDirectoryLinkCompat(string link, string target, uint flags);

        static StorageFolder brokerFolder;
        static string brokerRoot;

        internal static bool OnXbox => AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";
        internal static string InternalRoot => OnXbox
            ? @"D:\DevelopmentFiles\SpiderManInstaller-Imports"
            : Path.Combine(ApplicationData.Current.LocalFolder.Path, "GameImports");
        internal static string ExternalDefault => Path.Combine("E:\\", ExternalFolder);
        static IPropertySet Values => ApplicationData.Current.LocalSettings.Values;
        static string FolderFile => Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderFileName);

        internal static string OpenRoot()
        {
            string root = ChosenRoot();
            if (IsDriveRoot(root))
            {
                root = Path.Combine(root, ExternalFolder);
                Remember(root, "external");
            }
            if (!AccessibleDirectory(root)) EnsureDirectory(root);
            return root;
        }
        internal static string ChosenRoot()
        {
            string remembered = RememberedRoot();
            if (!String.IsNullOrEmpty(remembered)) return remembered;
            return InternalRoot;
        }
        internal static string Describe(string path)
        {
            if (String.IsNullOrEmpty(path)) return "not set";
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"D:\DevelopmentFiles", StringComparison.OrdinalIgnoreCase)) return "internal storage";
            string root = Path.GetPathRoot(full);
            if (!String.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':')
                return "USB / SSD (" + root.TrimEnd('\\') + ")";
            return "selected folder";
        }
        internal static bool NeedsBroker(string path)
        {
            // Do not remap D: overlay paths onto the SSD here. Writes to
            // StartupContent\asset_archive must stay on internal storage.
            if (!OnXbox || String.IsNullOrEmpty(path)) return false;
            try { path = Path.GetFullPath(path); }
            catch { return true; }
            if (path.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase)) return false;
            if (path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string local = ApplicationData.Current.LocalFolder.Path;
                if (!String.IsNullOrEmpty(local) && path.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            try
            {
                string pkg = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
                if (!String.IsNullOrEmpty(pkg) && path.StartsWith(pkg, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
            return true;
        }
        internal static long FreeBytes(string directory)
        {
            directory = Physical(Path.GetFullPath(directory));
            if (!NeedsBroker(directory))
            {
                ulong available, total, free;
                if (GetDiskFreeSpaceExW(directory, out available, out total, out free)) return checked((long)available);
                string volume = Path.GetPathRoot(directory);
                if (!String.IsNullOrEmpty(volume) && GetDiskFreeSpaceExW(volume, out available, out total, out free))
                    return checked((long)available);
                throw new IOException("Cannot measure destination free space: " + directory);
            }
            return BrokerFreeBytes(directory);
        }
        internal static bool SameVolume(string left, string right)
        {
            string a = Path.GetPathRoot(Path.GetFullPath(left));
            string b = Path.GetPathRoot(Path.GetFullPath(right));
            return !String.IsNullOrEmpty(a) && String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        internal static bool IsLink(string path)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return false;
                return (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0;
            }
            catch { return false; }
        }
        internal static void PlaceGameArchives(string source, string target, Action<string> report = null)
        {
            source = Path.GetFullPath(source);
            target = Path.GetFullPath(target);
            if (IsLink(target) && LinkPointsAt(target, source)) return;
            if (IsLink(target)) RemoveLink(target);
            if (Directory.Exists(target) && !HasEntries(target))
            {
                try { Directory.Delete(target); }
                catch { RemoveDirectoryFromAppCompat(target); }
            }
            if (SameVolume(source, target))
            {
                if (!Directory.Exists(target)) Directory.Move(source, target);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string marker = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(target)), "archive-link.txt");
            if (!Directory.Exists(target) && TryCreateDirectoryLink(target, source, out string error))
            {
                File.WriteAllText(marker, source);
                return;
            }
            try { if (Directory.Exists(target) && IsLink(target)) RemoveLink(target); } catch { }
            // Xbox AppContainer cannot create a D:→SSD reparse, and copying the
            // archives would fill internal storage. Keep the originals on the SSD
            // and write a marker so the native storage adapter opens them in place.
            // Generated overlays still go into a real folder on D:.
            report?.Invoke("Keeping game archives on the SSD. Internal storage only receives the small Xbox overlays.");
            File.WriteAllText(marker, source);
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);
        }
        static bool HasEntries(string path)
        {
            try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
            catch { return false; }
        }
        internal static void RemoveLink(string path)
        {
            if (!IsLink(path)) return;
            if (RemoveDirectoryFromAppCompat(path)) return;
            Directory.Delete(path);
        }
        internal static void UseInternal()
        {
            Remember(InternalRoot, "internal");
            EnsureDirectory(InternalRoot);
        }
        internal static async Task<string> UseExternalDefaultAsync()
        {
            Exception last = null;
            foreach (string candidate in await ExternalCandidatesAsync().ConfigureAwait(true))
            {
                try
                {
                    EnsureDirectory(candidate);
                    Remember(candidate, "external");
                    return candidate;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new IOException(last?.Message ?? ("No USB / SSD drive was found. Connect the drive and copy the game to \\" + ExternalFolder + ", or choose the folder."));
        }
        internal static void ApplyPickedFolder(StorageFolder folder)
        {
            if (folder == null) throw new IOException("No folder was chosen.");
            string path = folder.Path;
            if (String.IsNullOrEmpty(path)) throw new IOException("Xbox did not expose this folder's path. Copy the game to E:\\" + ExternalFolder + " on a PC, or pick a folder that shows a drive letter.");
            if (IsDriveRoot(path)) path = Path.Combine(path, ExternalFolder);
            BindFolder(folder, path);
            if (!AccessibleDirectory(path)) EnsureDirectory(path);
            Remember(path, "picked");
        }
        internal static void BindFolder(StorageFolder folder, string path = null)
        {
            if (folder == null) return;
            try
            {
                StorageApplicationPermissions.FutureAccessList.AddOrReplace(AccessToken, folder);
            }
            catch { }
            if (String.IsNullOrEmpty(path)) path = folder.Path;
            if (String.IsNullOrEmpty(path)) path = RememberedRoot();
            if (String.IsNullOrEmpty(path)) return;
            RememberBroker(folder, Path.GetFullPath(path));
        }
        internal static void RegisterImport(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            path = Path.GetFullPath(path);
            var known = ExtraImports().ToList();
            if (known.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
            known.Add(path);
            Values[ExtraImportsKey] = String.Join("\n", known);
        }
        internal static IEnumerable<string> ImportSearchRoots()
        {
            var roots = new List<string>();
            void add(string path)
            {
                if (String.IsNullOrEmpty(path)) return;
                try { path = Path.GetFullPath(path); }
                catch { return; }
                if (roots.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                roots.Add(path);
            }
            add(ChosenRoot());
            add(InternalRoot);
            foreach (string extra in ExtraImports()) add(extra);
            add(ExternalDefault);
            return roots;
        }
        internal static async Task<IList<string>> ExternalCandidatesAsync()
        {
            var found = new List<string>();
            void add(string path)
            {
                if (String.IsNullOrEmpty(path) || found.Any(p => p.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
                found.Add(path);
            }
            add(ExternalDefault);
            try
            {
                foreach (var drive in await KnownFolders.RemovableDevices.GetFoldersAsync())
                {
                    if (String.IsNullOrEmpty(drive.Path)) continue;
                    add(Path.Combine(drive.Path, ExternalFolder));
                }
            }
            catch { }
            return found;
        }
        internal static bool AccessibleFile(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try
            {
                path = Physical(Path.GetFullPath(path));
                if (!NeedsBroker(path)) return File.Exists(path);
                using (OpenBrokerRead(path)) return true;
            }
            catch { return false; }
        }
        internal static bool AccessibleLocal(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try
            {
                path = Physical(Path.GetFullPath(path), true);
                if (!NeedsBroker(path)) return File.Exists(path);
                using (OpenBrokerRead(path)) return true;
            }
            catch { return false; }
        }
        internal static bool AccessibleDirectory(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            try
            {
                path = Physical(Path.GetFullPath(path));
                if (!NeedsBroker(path)) return Directory.Exists(path);
                return BrokerDirectoryExists(path);
            }
            catch { return false; }
        }
        internal static void EnsureWritable(string path) { EnsureDirectory(path); }
        internal static Stream OpenRead(string path)
        {
            path = Physical(Path.GetFullPath(path));
            if (!NeedsBroker(path))
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return OpenBrokerRead(path);
        }
        internal static Stream OpenCreate(string path, FileAccess access)
        {
            return OpenWrite(path, true, access);
        }
        internal static Stream OpenCreateAlways(string path, FileAccess access)
        {
            return OpenWrite(path, false, access);
        }
        internal static Stream OpenExisting(string path, FileAccess access)
        {
            path = Physical(Path.GetFullPath(path));
            if (!NeedsBroker(path))
                return new FileStream(path, FileMode.Open, access, FileShare.None);
            return OpenBrokerExisting(path, access);
        }
        internal static Stream OpenOrCreate(string path)
        {
            path = Physical(Path.GetFullPath(path), true);
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent)) EnsureDirectory(parent);
            if (!NeedsBroker(path))
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return OpenBrokerOrCreate(path);
        }
        internal static void CopyFile(string source, string destination)
        {
            using (var input = OpenRead(source))
            using (var output = OpenCreate(destination, FileAccess.Write))
                input.CopyTo(output);
        }
        internal static void MoveFile(string source, string destination)
        {
            string original = Path.GetFullPath(source);
            source = Physical(original);
            destination = Physical(Path.GetFullPath(destination), true);
            bool leaveOriginal = !String.Equals(original, source, StringComparison.OrdinalIgnoreCase);
            if (!NeedsBroker(source) && !NeedsBroker(destination))
            {
                try
                {
                    if (leaveOriginal) { CopyOverwrite(original, destination); return; }
                    File.Move(source, destination);
                    return;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            CopyOverwrite(leaveOriginal ? original : source, destination);
            if (!leaveOriginal) DeleteFile(original);
        }
        internal static void ReplaceFile(string source, string destination, string backup)
        {
            string original = Path.GetFullPath(source);
            source = Physical(original);
            destination = Physical(Path.GetFullPath(destination), true);
            bool leaveOriginal = !String.Equals(original, source, StringComparison.OrdinalIgnoreCase);
            if (!String.IsNullOrEmpty(backup)) backup = Physical(Path.GetFullPath(backup), true);
            if (!leaveOriginal && !NeedsBroker(source) && !NeedsBroker(destination) && (String.IsNullOrEmpty(backup) || !NeedsBroker(backup)))
            {
                try
                {
                    if (!String.IsNullOrEmpty(backup)) { File.Replace(source, destination, backup); return; }
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(source, destination);
                    return;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            if (!String.IsNullOrEmpty(backup) && AccessibleFile(destination) && !AccessibleFile(backup))
                CopyOverwrite(destination, backup);
            CopyOverwrite(leaveOriginal ? original : source, destination);
            if (!leaveOriginal) DeleteFile(original);
        }
        internal static void DeleteFile(string path)
        {
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                path = Physical(Path.GetFullPath(path), true);
                if (!NeedsBroker(path))
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                Task.Run(async () =>
                {
                    var file = await GetBrokerFile(path).ConfigureAwait(false);
                    await file.DeleteAsync();
                }).GetAwaiter().GetResult();
            }
            catch { }
        }
        internal static long FileLength(string path)
        {
            path = Physical(Path.GetFullPath(path));
            if (!NeedsBroker(path)) return new FileInfo(path).Length;
            return Task.Run(async () =>
            {
                var file = await GetBrokerFile(path).ConfigureAwait(false);
                var properties = await file.GetBasicPropertiesAsync();
                return checked((long)properties.Size);
            }).GetAwaiter().GetResult();
        }
        static void CopyOverwrite(string source, string destination)
        {
            using (var input = OpenRead(source))
            using (var output = OpenCreateAlways(destination, FileAccess.Write))
                input.CopyTo(output);
        }
        static Stream OpenWrite(string path, bool createNew, FileAccess access)
        {
            path = Physical(Path.GetFullPath(path), true);
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent)) EnsureDirectory(parent);
            if (!NeedsBroker(path))
                return new FileStream(path, createNew ? FileMode.CreateNew : FileMode.Create, access, FileShare.None);
            return OpenBrokerWrite(path, createNew, access);
        }
        static Stream OpenBrokerRead(string path)
        {
            return Task.Run(async () =>
            {
                var file = await GetBrokerFile(path).ConfigureAwait(false);
                return await file.OpenStreamForReadAsync().ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        static Stream OpenBrokerExisting(string path, FileAccess access)
        {
            return Task.Run(async () =>
            {
                var file = await GetBrokerFile(path).ConfigureAwait(false);
                if (access == FileAccess.Read) return await file.OpenStreamForReadAsync().ConfigureAwait(false);
                return (await file.OpenAsync(FileAccessMode.ReadWrite)).AsStream();
            }).GetAwaiter().GetResult();
        }
        static Stream OpenBrokerOrCreate(string path)
        {
            return Task.Run(async () =>
            {
                path = Path.GetFullPath(path);
                var dest = await EnsureRelativeFolders(path).ConfigureAwait(false);
                var file = await dest.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.OpenIfExists);
                return (await file.OpenAsync(FileAccessMode.ReadWrite)).AsStream();
            }).GetAwaiter().GetResult();
        }
        static Stream OpenBrokerWrite(string path, bool createNew, FileAccess access)
        {
            return Task.Run(async () =>
            {
                path = Path.GetFullPath(path);
                var dest = await EnsureRelativeFolders(path).ConfigureAwait(false);
                var file = await dest.CreateFileAsync(Path.GetFileName(path),
                    createNew ? CreationCollisionOption.FailIfExists : CreationCollisionOption.ReplaceExisting);
                if (access == FileAccess.ReadWrite)
                    return (await file.OpenAsync(FileAccessMode.ReadWrite)).AsStream();
                return await file.OpenStreamForWriteAsync();
            }).GetAwaiter().GetResult();
        }
        static async Task<StorageFolder> EnsureRelativeFolders(string path)
        {
            var access = await GetAccessRoot(Path.GetDirectoryName(path)).ConfigureAwait(false);
            string root = Path.GetFullPath(access.Path).TrimEnd('\\') + '\\';
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escaped selected USB folder.");
            string relative = Path.GetDirectoryName(path.Substring(root.Length)) ?? "";
            var dest = access;
            foreach (string part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                dest = await dest.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
            return dest;
        }
        static async Task<StorageFile> GetBrokerFile(string path)
        {
            path = Path.GetFullPath(path);
            var access = await GetAccessRoot(Path.GetDirectoryName(path)).ConfigureAwait(false);
            string root = Path.GetFullPath(access.Path).TrimEnd('\\') + '\\';
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escaped selected USB folder.");
            return await GetDescendantFile(access, path.Substring(root.Length));
        }
        static async Task<StorageFile> GetDescendantFile(StorageFolder root, string relative)
        {
            relative = relative.Replace('/', '\\').Trim('\\');
            try { return await root.GetFileAsync(relative); }
            catch { }
            var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var folder = root;
            for (int i = 0; i < parts.Length - 1; i++)
                folder = await folder.GetFolderAsync(parts[i]);
            return await folder.GetFileAsync(parts[parts.Length - 1]);
        }
        static bool BrokerDirectoryExists(string path)
        {
            return Task.Run(async () =>
            {
                var access = await GetAccessRoot(path).ConfigureAwait(false);
                string root = Path.GetFullPath(access.Path).TrimEnd('\\') + '\\';
                string full = Path.GetFullPath(path).TrimEnd('\\') + '\\';
                if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
                var folder = access;
                foreach (string part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                    folder = await folder.GetFolderAsync(part);
                return true;
            }).GetAwaiter().GetResult();
        }
        static string VolumeRoot(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') return null;
            return Char.ToUpperInvariant(path[0]) + ":\\";
        }
        static bool PathUnder(string directory, string root)
        {
            if (String.IsNullOrEmpty(directory) || String.IsNullOrEmpty(root)) return false;
            directory = directory.TrimEnd('\\') + '\\';
            root = root.TrimEnd('\\') + '\\';
            return directory.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        static void RememberBroker(StorageFolder folder, string root)
        {
            if (folder == null || String.IsNullOrEmpty(root)) return;
            brokerFolder = folder;
            brokerRoot = root.TrimEnd('\\') + '\\';
            DiagnosticLog.Line("broker bound folderPath="+(folder.Path??"")+" brokerRoot="+brokerRoot);
        }
        static async Task<StorageFolder> GetAccessRoot(string directory)
        {
            directory = Path.GetFullPath(directory).TrimEnd('\\') + '\\';
            if (brokerFolder != null && PathUnder(directory, brokerRoot)) return brokerFolder;
            if (StorageApplicationPermissions.FutureAccessList.ContainsItem(AccessToken))
            {
                try
                {
                    var remembered = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(AccessToken);
                    string rememberedPath = remembered.Path;
                    if (String.IsNullOrEmpty(rememberedPath)) rememberedPath = RememberedRoot();
                    if (!String.IsNullOrEmpty(rememberedPath))
                    {
                        string folderRoot = Path.GetFullPath(rememberedPath).TrimEnd('\\') + '\\';
                        if (PathUnder(directory, folderRoot))
                        {
                            RememberBroker(remembered, folderRoot);
                            return remembered;
                        }
                    }
                }
                catch (Exception error) { DiagnosticLog.Line("FutureAccessList failed "+error.GetType().Name+": "+error.Message); }
            }
            string volume = VolumeRoot(directory);
            foreach (var drive in await KnownFolders.RemovableDevices.GetFoldersAsync())
            {
                string driveVolume = VolumeRoot(drive.Path);
                if (driveVolume == null)
                {
                    if (volume == null) continue;
                    try
                    {
                        string relative = directory.Substring(volume.Length).Trim('\\');
                        var probe = drive;
                        foreach (string part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            try { probe = await probe.GetFolderAsync(part); }
                            catch { probe = null; break; }
                        }
                        if (probe == null) continue;
                    }
                    catch { continue; }
                    RememberBroker(drive, volume);
                    return drive;
                }
                if (!PathUnder(directory, driveVolume)) continue;
                RememberBroker(drive, driveVolume);
                return drive;
            }
            var drives = new StringBuilder();
            try
            {
                foreach (var drive in await KnownFolders.RemovableDevices.GetFoldersAsync())
                    drives.Append('[').Append(drive.Name).Append(" path=").Append(drive.Path ?? "").Append("] ");
            }
            catch (Exception error) { drives.Append("list-failed ").Append(error.Message); }
            DiagnosticLog.Line("GetAccessRoot failed directory="+directory+" brokerRoot="+(brokerRoot??"")+" remembered="+(RememberedRoot()??"")+" drives="+drives);
            throw new IOException("Xbox did not expose this folder. Use Find files, then Use selected folder. directory="+directory+" brokerRoot="+(brokerRoot??"")+" drives="+drives);
        }
        internal static bool LooksLikeGame(string folder)
        {
            try
            {
                return AccessibleFile(Path.Combine(folder, "Spider-Man.exe")) &&
                    AccessibleFile(Path.Combine(folder, "asset_archive", "toc"));
            }
            catch { return false; }
        }
        static string RememberedRoot()
        {
            object stored;
            if (Values.TryGetValue(SettingsRoot, out stored))
            {
                string path = stored as string;
                if (!String.IsNullOrEmpty(path))
                {
                    try { return Path.GetFullPath(path); }
                    catch { }
                }
            }
            try
            {
                if (File.Exists(FolderFile))
                {
                    string path = File.ReadLines(FolderFile).FirstOrDefault();
                    if (!String.IsNullOrEmpty(path)) return Path.GetFullPath(path);
                }
            }
            catch { }
            return null;
        }
        static void Remember(string path, string kind)
        {
            path = Path.GetFullPath(path);
            Values[SettingsRoot] = path;
            Values[SettingsKind] = kind;
            try { File.WriteAllText(FolderFile, path + "\n" + kind + "\n"); } catch { }
        }
        static IEnumerable<string> ExtraImports()
        {
            object stored;
            if (!Values.TryGetValue(ExtraImportsKey, out stored) || !(stored is string text) || String.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();
            return text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim()).Where(p => p.Length > 0);
        }
        internal static void WriteText(string path, string text)
        {
            path = Physical(Path.GetFullPath(path));
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent)) EnsureDirectory(parent);
            using (var output = OpenCreateAlways(path, FileAccess.Write))
            using (var writer = new StreamWriter(output, Encoding.UTF8))
                writer.Write(text);
        }
        static bool IsDriveRoot(string path)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (String.IsNullOrEmpty(root)) return false;
            return String.Equals(full.TrimEnd('\\') + '\\', root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
        }
        static string Physical(string path, bool writing = false)
        {
            if (String.IsNullOrEmpty(path)) return path;
            path = Path.GetFullPath(path);
            try
            {
                string dir = Path.GetDirectoryName(path);
                for (int i = 0; i < 8 && !String.IsNullOrEmpty(dir); i++)
                {
                    string parent = Path.GetDirectoryName(dir);
                    if (String.IsNullOrEmpty(parent)) break;
                    string marker = Path.Combine(parent, "archive-link.txt");
                    if (File.Exists(marker))
                    {
                        string source = File.ReadAllText(marker).Trim();
                        if (!String.IsNullOrEmpty(source))
                        {
                            string linkDir = Path.GetFullPath(Path.Combine(parent, "StartupContent", "asset_archive"));
                            string prefix = linkDir.TrimEnd('\\') + '\\';
                            bool under = String.Equals(path, linkDir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                            if (under)
                            {
                                if (writing) return path;
                                try
                                {
                                    if (File.Exists(path)) return path;
                                    if (String.Equals(path, linkDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) return path;
                                }
                                catch { }
                                if (String.Equals(path, linkDir, StringComparison.OrdinalIgnoreCase))
                                    return Path.GetFullPath(source);
                                return Path.GetFullPath(Path.Combine(source, path.Substring(prefix.Length)));
                            }
                        }
                    }
                    dir = parent;
                }
            }
            catch { }
            return path;
        }
        static long BrokerFreeBytes(string directory)
        {
            return Task.Run(async () =>
            {
                var folder = await GetAccessRoot(directory).ConfigureAwait(false);
                var properties = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object value;
                if (!properties.TryGetValue("System.FreeSpace", out value) || value == null)
                    throw new IOException("Cannot check storage space on " + directory);
                return checked((long)Convert.ToUInt64(value));
            }).GetAwaiter().GetResult();
        }
        static void EnsureBrokerFolder(string path)
        {
            Task.Run(async () =>
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                if (IsDriveRoot(full)) return;
                var folder = await GetAccessRoot(full).ConfigureAwait(false);
                string root = Path.GetFullPath(folder.Path).TrimEnd('\\') + '\\';
                if (!full.StartsWith(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Path escaped selected USB folder.");
                string relative = full.Length <= root.Length - 1 ? "" : full.Substring(root.Length);
                foreach (string part in relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
                    folder = await folder.CreateFolderAsync(part, CreationCollisionOption.OpenIfExists);
            }).GetAwaiter().GetResult();
        }
        static void EnsureDirectory(string path)
        {
            path = Path.GetFullPath(path);
            if (IsDriveRoot(path)) return;
            string physical = Physical(path, true);
            if (NeedsBroker(physical))
            {
                if (AccessibleDirectory(physical)) return;
                EnsureBrokerFolder(physical);
                return;
            }
            if (Directory.Exists(path)) return;
            string parent = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(parent) && !parent.Equals(path, StringComparison.OrdinalIgnoreCase) && !IsDriveRoot(parent))
                EnsureDirectory(parent);
            try { Directory.CreateDirectory(path); }
            catch (Exception error)
            {
                if (CreateDirectoryFromAppCompat(path) || Directory.Exists(path)) return;
                throw new IOException("Cannot create game storage at " + path + ". " + error.Message, error);
            }
        }
        static bool TryCreateDirectoryLink(string link, string target, out string error)
        {
            error = null;
            if (CreateDirectoryLinkCompat(link, target, DirectoryLinkFlags) != 0) return true;
            int code = Marshal.GetLastWin32Error();
            if (CreateDirectoryLinkCompat(link, target, 1) != 0) return true;
            error = "Linking the SSD folder failed (Windows error " + code + "). Keep the game under \\" + ExternalFolder + " on the USB drive and keep the drive connected.";
            return false;
        }
        static bool LinkPointsAt(string link, string target)
        {
            try
            {
                string marker = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(link)), "archive-link.txt");
                if (File.Exists(marker) && String.Equals(File.ReadAllText(marker).Trim(), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
            return false;
        }
        internal static string StatusLine()
        {
            var text = new StringBuilder();
            string root = ChosenRoot();
            text.Append("Game files: ").Append(root).Append(" (").Append(Describe(root)).Append(")");
            try { text.Append(" — ").Append((FreeBytes(root) / 1073741824.0).ToString("F2")).Append(" GiB free"); }
            catch (Exception ex) { text.Append(" — ").Append(ex.Message); }
            text.Append("\nDefault SSD folder: E:\\").Append(ExternalFolder);
            try
            {
                long appFree = FreeBytes(ApplicationData.Current.LocalFolder.Path);
                text.Append("\nApp-data storage: ").Append((appFree / 1073741824.0).ToString("F2")).Append(" GiB free");
                if (appFree < 512L * 1024 * 1024) text.Append("\nApp-data space is low. Back up and clear old diagnostic logs before updates.");
            }
            catch { }
            return text.ToString();
        }
    }
}
