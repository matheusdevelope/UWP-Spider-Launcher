param([string]$VisualStudio = 'C:\Program Files\Microsoft Visual Studio\2022\Community', [string]$SteamKitAssembly, [string]$CombinedRuntimePayloadRoot, [string]$CombinedPreparationSupportRoot)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
function Embed-XboxRuntimes([string]$msixPath) {
    $makeappx = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe'
    if (!(Test-Path -LiteralPath $makeappx)) { Write-Output 'Skipping Xbox runtime embed: MakeAppx not found.'; return }
    $layout = Join-Path ([IO.Path]::GetTempPath()) ('spider-msix-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $layout -Force | Out-Null
    try {
        & $makeappx unpack /p $msixPath /d $layout /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'MakeAppx unpack failed.' }
        $dep = Join-Path (Split-Path $msixPath -Parent) 'Dependencies\x64'
        if (!(Test-Path -LiteralPath $dep)) { $dep = Join-Path $repo 'packages\SpiderManInstaller_0.6.0.61_x64_Test\Dependencies\x64' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        function Copy-ZipFiles([string]$appx, [string[]]$names) {
            if (!(Test-Path -LiteralPath $appx)) { return }
            $zip = [IO.Compression.ZipFile]::OpenRead($appx)
            try {
                foreach ($name in $names) {
                    $entry = $zip.Entries | Where-Object { $_.Name -eq $name } | Select-Object -First 1
                    if (!$entry) { continue }
                    $dest = Join-Path $layout $entry.Name
                    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                }
            } finally { $zip.Dispose() }
        }
        Copy-ZipFiles (Join-Path $dep 'Microsoft.NET.Native.Framework.2.2.appx') @('SharedLibrary.dll')
        Copy-ZipFiles (Join-Path $dep 'Microsoft.NET.Native.Runtime.2.2.appx') @('mrt100_app.dll','mrt_map.dll')
        Copy-ZipFiles (Join-Path $dep 'Microsoft.VCLibs.x64.14.00.appx') @(
            'concrt140_app.dll','msvcp140_1_app.dll','msvcp140_2_app.dll','msvcp140_app.dll',
            'msvcp140_atomic_wait_app.dll','msvcp140_codecvt_ids_app.dll','vcamp140_app.dll',
            'vccorlib140_app.dll','vcomp140_app.dll','vcruntime140_1_app.dll','vcruntime140_app.dll'
        )
        $desktop = @('concrt140.dll','msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vccorlib140.dll','vcruntime140.dll','vcruntime140_1.dll','vcruntime140_threads.dll')
        foreach ($prior in @(
            (Join-Path $repo '.local\msix-61\layout'),
            'C:\Users\emers\Downloads\UWP-Spider-Launcher-0.6.0.57-x64-Private-Test.msix'
        )) {
            if ((Test-Path -LiteralPath $prior) -and $prior.EndsWith('.msix')) { Copy-ZipFiles $prior $desktop; break }
            if (Test-Path -LiteralPath $prior) {
                foreach ($name in $desktop) {
                    $src = Join-Path $prior $name
                    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $layout $name) -Force }
                }
                break
            }
        }
        if (!(Test-Path -LiteralPath (Join-Path $layout 'SharedLibrary.dll'))) { throw 'Xbox runtime embed failed: SharedLibrary.dll missing.' }
        Remove-Item -LiteralPath (Join-Path $layout 'AppxSignature.p7x') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $layout 'AppxBlockMap.xml') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $layout 'AppxMetadata') -Recurse -Force -ErrorAction SilentlyContinue
        & $makeappx pack /d $layout /p $msixPath /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'MakeAppx pack failed.' }
        Write-Output "Embedded Xbox runtime DLLs into $msixPath"
    } finally { Remove-Item -LiteralPath $layout -Recurse -Force -ErrorAction SilentlyContinue }
}
$msbuild = Join-Path $VisualStudio 'MSBuild\Current\Bin\MSBuild.exe'
if ([bool]$CombinedRuntimePayloadRoot -ne [bool]$CombinedPreparationSupportRoot) { throw 'Provide both combined-runtime input directories, or neither for the default launcher build.' }
& (Join-Path $repo 'asset-preparation\build.ps1') -VisualStudio $VisualStudio
& (Join-Path $PSScriptRoot 'make-icons.ps1')
$options = @('/restore','/m:2','/p:Configuration=Release','/p:Platform=x64','/p:PrivateValidation=true','/p:AppxPackageSigningEnabled=false','/p:UapAppxPackageBuildMode=SideloadOnly','/verbosity:minimal','/nologo')
if ($SteamKitAssembly) {
    $assembly = (Resolve-Path -LiteralPath $SteamKitAssembly).Path
    $options += "/p:SteamKitAssembly=$assembly"
}
if ($CombinedRuntimePayloadRoot) {
    $payloadInput = (Resolve-Path -LiteralPath $CombinedRuntimePayloadRoot).Path
    $supportInput = (Resolve-Path -LiteralPath $CombinedPreparationSupportRoot).Path
    $bridges = @(
        (Join-Path $repo 'native\SpShellBridge.vcxproj'),
        (Join-Path $repo 'native\SpArgs40Bridge.vcxproj')
    )
    $toolset = $null
    foreach ($vcVer in @('v180','v170','v160')) {
        $toolsetsDir = Join-Path $VisualStudio "MSBuild\Microsoft\VC\$vcVer\Platforms\x64\PlatformToolsets"
        if (Test-Path -LiteralPath $toolsetsDir) {
            $toolset = (Get-ChildItem -LiteralPath $toolsetsDir -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
            break
        }
    }
    if (!$toolset) { throw 'No MSVC PlatformToolset was found under this Visual Studio install.' }
    $nativeProps = @("/p:PlatformToolset=$toolset")
    $vc144 = Join-Path $VisualStudio 'VC\Tools\MSVC\14.44.35207'
    if (Test-Path -LiteralPath $vc144) { $nativeProps += '/p:VCToolsVersion=14.44.35207' }
    foreach ($bridge in $bridges) {
        & $msbuild $bridge /m:2 /p:Configuration=Release /p:Platform=x64 @nativeProps /verbosity:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw "SSD storage adapter build failed: $bridge" }
    }
    $options += '/p:CombinedRuntimeBuild=true'
    $options += "/p:CombinedRuntimePayloadRoot=$payloadInput"
    $options += "/p:CombinedPreparationSupportRoot=$supportInput"
}
& $msbuild (Join-Path $repo 'source\SpiderManInstaller.csproj') @options
if ($LASTEXITCODE -ne 0) { throw 'Private validation build failed.' }
$msix = Get-ChildItem (Join-Path $repo 'packages') -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($msix) { Embed-XboxRuntimes $msix.FullName }
Write-Output 'Launcher build complete. This does not approve public binary redistribution. Read release/STATUS.md.'
