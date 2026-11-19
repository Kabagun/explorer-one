[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishedDirectory,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $IsccPath,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$DotNetRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.15/dotnet-runtime-10.0.15-win-x64.zip'
$DotNetRuntimeSha256 = 'c9f219ad2ec570478f4b1950ae56f88a48e76cabba75e7f54764a1d8a9009e87'
$WindowsDesktopRuntimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.zip'
$WindowsDesktopRuntimeSha256 = '60f6a65f98a0882deeba9ab78ac923745b420cb9c0a84b9fe47cf3ac5f61c7ed'

function Resolve-IsccPath {
    param([string] $ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf)) {
            throw "ISCC.exe not found at: $ExplicitPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    $candidatePaths = @(
        (Join-Path $HOME 'Programs\InnoSetup7\ISCC.exe'),
        (Join-Path $HOME 'Programs\InnoSetup6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidatePaths) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    throw @'
Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup 6.7.1+ or 7.x from https://jrsoftware.org/isdl.php, or pass -IsccPath explicitly. The build script does not download dependencies.
'@
}

function Get-NativeRouterPayload {
    param([string] $PayloadRoot)

    $markerPath = Join-Path $PayloadRoot 'ExplorerOne.NativeLaunchRouter.current'
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw "Published payload is missing ExplorerOne.NativeLaunchRouter.current: $PayloadRoot"
    }

    $routerName = [System.IO.File]::ReadAllText($markerPath).Trim()
    if ($routerName -notmatch '^ExplorerOne\.NativeLaunchRouter\.v1\.[0-9a-f]{16}\.dll$') {
        throw "Native router marker contains an invalid DLL name: $routerName"
    }

    $routers = @(Get-ChildItem -LiteralPath $PayloadRoot -File -Filter 'ExplorerOne.NativeLaunchRouter.*.dll')
    if ($routers.Count -ne 1 -or $routers[0].Name -cne $routerName) {
        $names = ($routers | ForEach-Object Name) -join ', '
        throw "Published payload must contain only the marker-selected native router DLL. Marker: $routerName. DLLs found: $names"
    }

    return $routerName
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $versionPropsPath = Join-Path $repositoryRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
        throw "Cannot derive installer version; Directory.Build.props not found at $versionPropsPath"
    }
    [xml] $versionProps = Get-Content -LiteralPath $versionPropsPath -Raw
    $versionNode = $versionProps.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $versionNode -or $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+$') {
        throw "Directory.Build.props must define a numeric three-part Version for setup metadata."
    }
    $Version = $versionNode.InnerText
}
$publishedRoot = (Resolve-Path -LiteralPath $PublishedDirectory -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $publishedRoot -PathType Container)) {
    throw "Published directory does not exist: $PublishedDirectory"
}

$requiredFiles = @(
    'ExplorerOne.exe',
    'ExplorerOne.dll',
    'ExplorerOne.deps.json',
    'ExplorerOne.runtimeconfig.json',
    'ExplorerOne.NativeLaunchRouter.current',
    'MinHook.LICENSE.txt',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'README.md',
    'docs\manual-tests.md',
    'docs\verification.md'
)
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $publishedRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Published payload is incomplete; missing $relativePath"
    }
}

if ((Test-Path -LiteralPath (Join-Path $publishedRoot 'coreclr.dll')) -or
    (Test-Path -LiteralPath (Join-Path $publishedRoot 'hostfxr.dll'))) {
    throw 'Expected a framework-dependent publish payload; self-contained runtime files are not accepted.'
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishedRoot 'ExplorerOne.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.includedFrameworks) {
    throw 'Expected a framework-dependent runtimeconfig; includedFrameworks indicates self-contained output.'
}

$expectedAssemblyVersion = [Version]::Parse("$Version.0")
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $publishedRoot 'ExplorerOne.dll')).Version
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "Published ExplorerOne.dll version is $assemblyVersion; expected $expectedAssemblyVersion"
}

$routerName = Get-NativeRouterPayload -PayloadRoot $publishedRoot
$compilerPath = Resolve-IsccPath -ExplicitPath $IsccPath

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$environmentNames = @(
    'EXPLORERONE_PUBLISHED_DIRECTORY',
    'EXPLORERONE_OUTPUT_DIRECTORY',
    'EXPLORERONE_VERSION',
    'EXPLORERONE_ROUTER_DLL',
    'EXPLORERONE_DOTNET_URL',
    'EXPLORERONE_DOTNET_SHA256',
    'EXPLORERONE_WINDOWS_DESKTOP_URL',
    'EXPLORERONE_WINDOWS_DESKTOP_SHA256'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:EXPLORERONE_PUBLISHED_DIRECTORY = $publishedRoot
    $env:EXPLORERONE_OUTPUT_DIRECTORY = $outputRoot
    $env:EXPLORERONE_VERSION = $Version
    $env:EXPLORERONE_ROUTER_DLL = $routerName
    $env:EXPLORERONE_DOTNET_URL = $DotNetRuntimeUrl
    $env:EXPLORERONE_DOTNET_SHA256 = $DotNetRuntimeSha256
    $env:EXPLORERONE_WINDOWS_DESKTOP_URL = $WindowsDesktopRuntimeUrl
    $env:EXPLORERONE_WINDOWS_DESKTOP_SHA256 = $WindowsDesktopRuntimeSha256

    $scriptPath = Join-Path $PSScriptRoot 'ExplorerOne.iss'
    & $compilerPath $scriptPath
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup Compiler failed with exit code $LASTEXITCODE"
    }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

$setupPath = Join-Path $outputRoot "ExplorerOne-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "ISCC completed without producing the expected installer: $setupPath"
}
$setupInfo = Get-Item -LiteralPath $setupPath
if ($setupInfo.Length -gt 10MB) {
    throw "Installer is $($setupInfo.Length) bytes; expected a single setup smaller than 10 MiB."
}

Write-Output "Installer created: $setupPath"
