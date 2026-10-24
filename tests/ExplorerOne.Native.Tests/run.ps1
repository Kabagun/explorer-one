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
    throw 'Native protocol tests require the x64 Visual C++ toolchain.'
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputDirectory = Join-Path $projectRoot 'temp/native-tests'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executable = Join-Path $outputDirectory 'ExplorerOne.Native.Tests.exe'
$object = Join-Path $outputDirectory 'ExplorerOne.Native.Tests.obj'
$library = Join-Path $outputDirectory 'ExplorerOne.Native.Tests.lib'
$source = Join-Path $PSScriptRoot 'Program.cpp'
& cl.exe /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /DUNICODE /D_UNICODE "/Fe:$executable" "/Fo:$object" $source /link "/IMPLIB:$library" user32.lib shell32.lib ole32.lib oleaut32.lib uuid.lib advapi32.lib bcrypt.lib runtimeobject.lib
if ($LASTEXITCODE -ne 0) { throw 'Native protocol test compilation failed.' }
& $executable
if ($LASTEXITCODE -ne 0) { throw 'Native protocol tests failed.' }
