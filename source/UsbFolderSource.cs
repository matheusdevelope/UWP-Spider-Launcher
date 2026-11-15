using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace SpiderManInstaller
{
    // Read-only access through the UWP StorageFolder broker used for Xbox
    // removable drives. System.IO and CreateFile2FromAppW are not used here.
    sealed class UsbFolderSource : IFolderImportSource
    {
        readonly StorageFolder folder;
        public UsbFolderSource(StorageFolder folder) { this.folder = folder; }

        public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            string relative = ImportEngine.SafePath(relativePath).Replace('/', '\\');
            try
            {
                StorageFile file;
                try { file = await folder.GetFileAsync(relative); }
                catch (Exception)
                {
                    var parts = relative.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    var current = folder;
                    for (int i = 0; i < parts.Length - 1; i++) current = await current.GetFolderAsync(parts[i]);
                    file = await current.GetFileAsync(parts[parts.Length - 1]);
                }
                cancel.ThrowIfCancellationRequested();
                return await file.OpenStreamForReadAsync();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new IOException("Cannot read USB file " + relativePath + ". Use Find files, then Use selected folder. " + error.Message, error);
            }
        }
    }
}
