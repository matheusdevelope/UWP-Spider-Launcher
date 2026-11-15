using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SpiderManInstaller
{
    public sealed class PreparationFile
    {
        public string Destination, Origin, Source, Hash;
        public long Size;
    }
    public sealed class ModuleReplacement { public long Offset; public byte[] Before, After; }
    public sealed class ModulePatch
    {
        public long SourceSize, ResultSize;
        public string SourceHash, ResultHash;
        public byte[] Append;
        public List<ModuleReplacement> Replacements;
    }
    public static class PreparationEngine
    {
        // A combined package owns these runtime components in its immutable
        // installed location. The data preparation receipt covers only files
        // the game actually reads from the writable game-data directory.
        public static List<PreparationFile> SelectDataFiles(IList<PreparationFile> files,IList<PreparationFile> includedRuntime)
        {
            var included=new Dictionary<string,PreparationFile>(StringComparer.OrdinalIgnoreCase);
            foreach(var component in includedRuntime) {
                if(String.IsNullOrEmpty(component.Destination)||ImportEngine.SafePath(component.Destination)!=component.Destination||component.Destination.EndsWith("/"))
                    throw new IOException("Invalid included runtime component path");
                included.Add(component.Destination,component);
            }
            var result=new List<PreparationFile>();
            foreach(var row in files) {
                PreparationFile component;
                if(!included.TryGetValue(row.Destination,out component)){result.Add(row);continue;}
                if(row.Origin!="payload"||row.Source!=row.Destination||row.Size!=component.Size||!String.Equals(row.Hash,component.Hash,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Included runtime recipe differs: "+row.Destination);
                // VerifyPayload has already checked the packaged bytes. Do not
                // copy or inspect an obsolete separate-runtime copy on D:.
            }
            return result;
        }
        // Version-specific metadata shares one set of support files.
        internal static string SharedPayloadRoot(string supportBase)=>Path.Combine(supportBase,"PreparationPayload");
        internal static void VerifyPayloadInputs(string payloadRoot,IEnumerable<PreparationFile> files,CancellationToken cancel)
        {
            foreach(var row in files.Where(r=>r.Origin=="payload")) {
                cancel.ThrowIfCancellationRequested();Verify(Full(payloadRoot,row.Source),row.Size,row.Hash,cancel);
            }
        }
        static string Full(string root,string relative)
        {
            string safe=ImportEngine.SafePath(relative);
            string prefix=Path.GetFullPath(root).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
            string result=Path.GetFullPath(Path.Combine(root,safe.Replace('/',Path.DirectorySeparatorChar)));
            if(!result.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe preparation path");
            return result;
        }
        static string Hash(string path,CancellationToken cancel)
        {
            using(var input=GameStorage.OpenRead(path)) using(var hash=SHA256.Create())
            {
                var b=new byte[1024*1024];int n;
                while((n=input.Read(b,0,b.Length))!=0) { cancel.ThrowIfCancellationRequested();hash.TransformBlock(b,0,n,b,0); }
                hash.TransformFinalBlock(new byte[0],0,0);
                return BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();
            }
        }
        static string ArchiveCheckIdentity(IList<PreparationFile> archives)
        {
            using(var hash=SHA256.Create())
            {
                foreach(var row in archives.OrderBy(r=>r.Source,StringComparer.OrdinalIgnoreCase))
                {
                    byte[] line=Encoding.UTF8.GetBytes(row.Source+"|"+row.Size+"|"+row.Hash+"\n");
                    hash.TransformBlock(line,0,line.Length,line,0);
                }
                hash.TransformFinalBlock(new byte[0],0,0);
                return BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();
            }
        }
        static string ArchiveCheckFile(string importRoot)=>Path.Combine(importRoot,"import-archive-check.txt");
        static bool ArchivesAlreadyChecked(string importRoot,IList<PreparationFile> archives,Action<string> report)
        {
            if(archives==null||archives.Count==0)return true;
            string identity=ArchiveCheckIdentity(archives);
            bool marked=false;
            try
            {
                string marker=ArchiveCheckFile(importRoot);
                if(GameStorage.AccessibleFile(marker))
                {
                    using(var reader=new StreamReader(GameStorage.OpenRead(marker)))
                        marked=reader.ReadToEnd().IndexOf(identity,StringComparison.OrdinalIgnoreCase)>=0;
                }
            }
            catch { marked=false; }
            if(!marked && !GameStorage.AccessibleFile(Path.Combine(importRoot,"import-receipt.txt"))) return false;
            foreach(var row in archives)
            {
                string path=Full(importRoot,row.Source);
                if(!GameStorage.AccessibleFile(path)||GameStorage.FileLength(path)!=row.Size) return false;
            }
            if(!marked) RememberArchiveCheck(importRoot,archives);
            report?.Invoke("SSD archives already checked ("+archives.Count+" files, sizes match). Skipping another full hash.");
            return true;
        }
        static void RememberArchiveCheck(string importRoot,IList<PreparationFile> archives)
        {
            GameStorage.WriteText(ArchiveCheckFile(importRoot),ArchiveCheckIdentity(archives)+"\nfiles="+archives.Count+"\nUTC: "+DateTime.UtcNow.ToString("o")+"\n");
        }
        static void Verify(string path,long size,string hash,CancellationToken cancel)
        {
            using(var input=GameStorage.OpenRead(path))
                if(input.Length!=size) throw new IOException("Preparation verification failed: "+Path.GetFileName(path));
            if(!Hash(path,cancel).Equals(hash,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Preparation verification failed: "+Path.GetFileName(path));
        }
        static void Copy(string source,string target,long size,string hash,CancellationToken cancel)
        {
            if(GameStorage.AccessibleFile(target)) { Verify(target,size,hash,cancel); return; }
            GameStorage.EnsureWritable(Path.GetDirectoryName(target));
            string partial=target+".prepare-partial";
            GameStorage.DeleteFile(partial);
            using(var input=GameStorage.OpenRead(source)) using(var output=GameStorage.OpenCreate(partial,FileAccess.Write))
            {
                var b=new byte[1024*1024];int n;
                while((n=input.Read(b,0,b.Length))!=0) { cancel.ThrowIfCancellationRequested();output.Write(b,0,n); }
                output.Flush();
            }
            Verify(partial,size,hash,cancel); GameStorage.MoveFile(partial,target);
        }
        internal static void Derive(string source,string target,ModulePatch patch,CancellationToken cancel)
        {
            if(GameStorage.AccessibleFile(target)) { Verify(target,patch.ResultSize,patch.ResultHash,cancel);return; }
            Verify(source,patch.SourceSize,patch.SourceHash,cancel);
            if(patch.Append==null || patch.SourceSize+patch.Append.Length!=patch.ResultSize) throw new IOException("Invalid module recipe");
            long end=0;
            foreach(var change in patch.Replacements)
            {
                if(change.Offset<end || change.Before.Length!=change.After.Length || change.Offset+change.Before.Length>patch.SourceSize)
                    throw new IOException("Invalid module replacement range");
                end=change.Offset+change.Before.Length;
            }
            string partial=target+".prepare-partial";
            GameStorage.DeleteFile(partial);
            GameStorage.CopyFile(source,partial);
            using(var output=GameStorage.OpenExisting(partial,FileAccess.ReadWrite))
            {
                foreach(var change in patch.Replacements)
                {
                    cancel.ThrowIfCancellationRequested();output.Position=change.Offset;
                    foreach(byte value in change.Before) if(output.ReadByte()!=value) throw new IOException("Module input mismatch");
                    output.Position=change.Offset;output.Write(change.After,0,change.After.Length);
                }
                output.Position=patch.SourceSize;output.Write(patch.Append,0,patch.Append.Length);output.Flush();
            }
            Verify(partial,patch.ResultSize,patch.ResultHash,cancel);GameStorage.MoveFile(partial,target);
        }
        public static void Run(string importRoot,string runtimeRoot,string payloadRoot,
            IList<PreparationFile> files,ModulePatch patch,string recipeHash,Action<string> report,CancellationToken cancel,string generatedRoot=null)
        {
            cancel.ThrowIfCancellationRequested();
            importRoot=Path.GetFullPath(importRoot);runtimeRoot=Path.GetFullPath(runtimeRoot);
            if(importRoot.Equals(runtimeRoot,StringComparison.OrdinalIgnoreCase) || runtimeRoot.StartsWith(importRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) || importRoot.StartsWith(runtimeRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Runtime must be separate from the import");
            if(files.Count==0 || recipeHash==null || recipeHash.Length!=64 || recipeHash.Any(c=>!Uri.IsHexDigit(c))) throw new IOException("Invalid preparation identity");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var row in files)
            {
                Full(runtimeRoot,row.Destination);Full(payloadRoot,row.Source);
                if(!names.Add(ImportEngine.SafePath(row.Destination)) || row.Size<0 || row.Hash==null || row.Hash.Length!=64 || row.Hash.Any(c=>!Uri.IsHexDigit(c))) throw new IOException("Invalid preparation recipe");
                if(row.Origin!="import" && row.Origin!="payload" && row.Origin!="derived" && row.Origin!="generated")throw new IOException("Unknown preparation input");
                if(row.Origin=="generated") { if(generatedRoot==null)throw new IOException("Generated assets missing");Full(generatedRoot,row.Source); }
                if(row.Origin=="import" && row.Source.StartsWith("asset_archive/",StringComparison.OrdinalIgnoreCase) && row.Source!=row.Destination) throw new IOException("Archive paths must match");
                if(row.Origin=="derived" && (row.Size!=patch.ResultSize || row.Hash!=patch.ResultHash))throw new IOException("Module recipe identity mismatch");
            }
            VerifyPayloadInputs(payloadRoot,files,cancel);
            string stage=Path.Combine(runtimeRoot,"StartupContent");
            string journal=Path.Combine(runtimeRoot,"preparation-source.txt");
            string binding=recipeHash+"\n"+importRoot;
            if(File.Exists(journal) && File.ReadAllText(journal)!=binding)throw new IOException("Runtime belongs to a different preparation. Keep it and choose another runtime.");
            if(!File.Exists(Path.Combine(runtimeRoot,"runtime-bootstrap.json"))) throw new IOException("Install the separate game runtime first");
            if(!GameStorage.AccessibleFile(Path.Combine(importRoot,"import-receipt.txt"))) throw new IOException("Choose a completed, verified import");
            string ready=Path.Combine(runtimeRoot,"prepared-receipt.txt");
            if(File.Exists(ready))throw new IOException("This runtime is already prepared");
            Directory.CreateDirectory(stage);
            if(!File.Exists(journal)) File.WriteAllText(journal,binding);
            string sourceAssets=Path.Combine(importRoot,"asset_archive");
            string targetAssets=Path.Combine(stage,"asset_archive");
            bool sourcePresent=GameStorage.AccessibleDirectory(sourceAssets)||GameStorage.AccessibleFile(Path.Combine(sourceAssets,"toc"));
            bool linked=GameStorage.IsLink(targetAssets);
            bool targetPresent=Directory.Exists(targetAssets)&&(linked||Directory.EnumerateFileSystemEntries(targetAssets).Any());
            bool moved=!sourcePresent && targetPresent && !linked;
            if(!moved && !sourcePresent)throw new IOException("Imported assets are missing");
            Verify(Full(importRoot,"Spider-Man.exe"),patch.SourceSize,patch.SourceHash,cancel);
            var archiveInputs=files.Where(r=>r.Origin=="import" && r.Source.StartsWith("asset_archive/",StringComparison.OrdinalIgnoreCase)).ToList();
            string archiveRoot=moved?stage:importRoot;
            if(!ArchivesAlreadyChecked(archiveRoot,archiveInputs,report))
            {
                int checkedFiles=0;
                foreach(var row in archiveInputs)
                {
                    report?.Invoke("Verifying game archives "+(++checkedFiles)+" / "+archiveInputs.Count+": "+Path.GetFileName(row.Source));
                    Verify(Full(archiveRoot,row.Source),row.Size,row.Hash,cancel);
                }
                RememberArchiveCheck(archiveRoot,archiveInputs);
            }
            string pending=Path.Combine(runtimeRoot,"PreparedOverlays");
            int index=0;
            foreach(var row in files)
            {
                cancel.ThrowIfCancellationRequested();
                if(row.Origin=="import" && row.Source.StartsWith("asset_archive/",StringComparison.OrdinalIgnoreCase)) continue;
                report?.Invoke("Preparing Xbox files "+(++index)+": "+row.Destination);
                bool overlay=row.Destination.StartsWith("asset_archive/",StringComparison.OrdinalIgnoreCase);
                string target=Full(overlay?pending:stage,row.Destination);
                if(row.Origin=="derived") Derive(Full(importRoot,row.Source),target,patch,cancel);
                else Copy(Full(row.Origin=="import"?importRoot:row.Origin=="generated"?generatedRoot:payloadRoot,row.Source),target,row.Size,row.Hash,cancel);
            }
            // Same-volume installs move the archives next to the runtime host.
            // USB/SSD installs keep the archives on that drive and link them.
            cancel.ThrowIfCancellationRequested();
            report?.Invoke(GameStorage.SameVolume(sourceAssets,targetAssets)
                ?"Finishing preparation—keeping game archives on this drive:"
                :"Finishing preparation—keeping game archives on the SSD:");
            if(!moved)GameStorage.PlaceGameArchives(sourceAssets,targetAssets,report);
            string savedToc=Path.Combine(runtimeRoot,"original-asset-toc");
            string toc=Path.Combine(targetAssets,"toc");
            if(!GameStorage.AccessibleLocal(savedToc))GameStorage.MoveFile(toc,savedToc);
            foreach(var row in files.Where(r=>(r.Origin=="payload"||r.Origin=="generated") && r.Destination.StartsWith("asset_archive/",StringComparison.OrdinalIgnoreCase)))
            {
                string target=Full(stage,row.Destination),from=Full(pending,row.Destination);
                if(GameStorage.AccessibleLocal(target))Verify(target,row.Size,row.Hash,CancellationToken.None);
                else GameStorage.MoveFile(from,target);
            }
            foreach(var row in files)
            {
                string target=Full(stage,row.Destination);
                if(!GameStorage.AccessibleFile(target)||GameStorage.FileLength(target)!=row.Size)
                    throw new IOException("Incomplete prepared runtime: "+row.Destination);
            }
            File.WriteAllText(ready,recipeHash+"\nPrepared from owned verified import: "+importRoot+"\nUTC: "+DateTime.UtcNow.ToString("o"));
            report?.Invoke("Prepared and verified. Ready to play.");
        }
    }
}
