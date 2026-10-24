param([string]$Configuration = 'Release', [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $candidates = @()
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $candidates += & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    }
    $candidates += Join-Path $HOME 'Programs/VSBuildTools'
    $installation = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll') } | Select-Object -First 1
    if (-not $installation) { throw 'Existing Visual C++ build tools were not found. No tools were installed.' }
    Import-Module (Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -InstallPath $installation -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
}
Get-Command cl.exe -ErrorAction Stop | Out-Null
Get-Command link.exe -ErrorAction Stop | Out-Null
if ($env:VSCMD_ARG_TGT_ARCH -and $env:VSCMD_ARG_TGT_ARCH -notin @('x64', 'amd64')) {
    throw 'The native launch router requires the x64 Visual C++ toolchain.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "bin/$Configuration" }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$vendor = 'vendor/minhook-1.3.4'
$cppSources = @('NativeLaunchRouter.cpp', 'FolderTabRouting.cpp', 'NativeRouteQueue.cpp', 'TargetFolderBridge.cpp', 'NativeSourcePresentation.cpp')
$cSources = @("$vendor/src/buffer.c", "$vendor/src/hook.c", "$vendor/src/trampoline.c", "$vendor/src/hde/hde64.c")
$inputs = @('build.ps1', 'NativeLaunchRouter.cpp', 'NativeLaunchProtocol.h', 'FolderTabRouting.cpp', 'FolderTabRouting.h', 'NativeRouteQueue.cpp', 'NativeRouteQueue.h', 'TargetFolderBridge.cpp', 'TargetFolderBridge.h', 'TargetFolderProtocol.h', 'NativeSourcePresentation.cpp', 'NativeSourcePresentation.h')
$inputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $vendor) -Recurse -File | ForEach-Object { [System.IO.Path]::GetRelativePath($PSScriptRoot, $_.FullName).Replace('\', '/') }
$fingerprints = $inputs | Sort-Object -Unique | ForEach-Object {
    $path = Join-Path $PSScriptRoot $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required native router input is missing: $_" }
    "${_}:$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)"
}
$hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes(($fingerprints -join "`n")))).Substring(0, 16).ToLowerInvariant()
$output = Join-Path $OutputDirectory "ExplorerOne.NativeLaunchRouter.v1.$hash.dll"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    $objects = @()
    Push-Location $PSScriptRoot
    try {
        foreach ($source in ($cppSources + $cSources)) {
            $object = Join-Path $OutputDirectory ([System.IO.Path]::GetFileNameWithoutExtension($source) + '.obj')
            $options = @('/nologo', '/c', '/W4', '/WX', '/O2', '/MT', '/DUNICODE', '/D_UNICODE', "/I$vendor/include", "/Fo:$object")
            if ($source.EndsWith('.cpp', [System.StringComparison]::Ordinal)) { $options += @('/std:c++20', '/EHsc', '/TP') }
            # The unchanged MinHook C sources intentionally use anonymous unions,
            # byte-sized BOOL fields and truncated relative-jump constants.
            # Keep all other level-four warnings fatal, and all C++ warnings fatal.
            else { $options += @('/TC', '/wd4201', '/wd4244', '/wd4310') }
            # The decoder's sixteen-iteration prefix loop always assigns c.
            if ($source -eq "$vendor/src/hde/hde64.c") { $options += '/wd4701' }
            & cl.exe @options $source
            if ($LASTEXITCODE -ne 0) { throw "Native launch router compilation failed: $source" }
            $objects += $object
        }
        & link.exe /nologo /DLL /MACHINE:X64 "/OUT:$output" "/IMPLIB:$OutputDirectory/NativeLaunchRouter.lib" @objects user32.lib shell32.lib ole32.lib oleaut32.lib uuid.lib advapi32.lib bcrypt.lib runtimeobject.lib
        if ($LASTEXITCODE -ne 0) { throw 'Native launch router linking failed.' }
    } finally { Pop-Location }
}
# Each source version remains loadable while an older Explorer process pins it.
$manifest = Join-Path $OutputDirectory 'ExplorerOne.NativeLaunchRouter.current'
$filename = [System.IO.Path]::GetFileName($output)
if (-not (Test-Path -LiteralPath $manifest) -or [System.IO.File]::ReadAllText($manifest) -cne $filename) {
    [System.IO.File]::WriteAllText($manifest, $filename, [System.Text.UTF8Encoding]::new($false))
}
Write-Output $output
