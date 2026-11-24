param(
    [string]$GameBin,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $projectRoot 'updates\spotter'
$companionRoot = Join-Path $packageRoot 'Spotter\Apex5'
$nativeObjects = Join-Path $projectRoot 'src\obj\spotter-bootstrap'
New-Item -ItemType Directory -Path $packageRoot,$companionRoot,$nativeObjects -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'MSVC x64 build tools were not found.' }
$msvc = Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1
$toolBin = Join-Path $msvc.FullName 'bin\Hostx64\x64'
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\Windows.h') } |
    Sort-Object Name -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK headers were not found.' }
$oldPath = $env:PATH
try {
    $env:PATH = "$toolBin;$env:PATH"
    $compilerArguments = @('/nologo','/LD','/O2','/W4','/WX','/GS-','/Zl',
        "/I$(Join-Path $msvc.FullName 'include')",
        "/I$(Join-Path $sdk.FullName 'shared')",
        "/I$(Join-Path $sdk.FullName 'um')",
        "/I$(Join-Path $sdk.FullName 'ucrt')",
        "/Fo$(Join-Path $nativeObjects 'SpotterBootstrap.obj')",
        "/Fe$(Join-Path $packageRoot 'SnowRunnerApex5.asi')",
        (Join-Path $PSScriptRoot 'SpotterBootstrap.c'),
        '/link','/NODEFAULTLIB','/ENTRY:DllMain','/MACHINE:X64','/DYNAMICBASE','/NXCOMPAT',
        "/IMPLIB:$(Join-Path $nativeObjects 'SnowRunnerApex5.lib')",
        (Join-Path $sdkRoot "Lib\$($sdk.Name)\um\x64\kernel32.lib"),
        (Join-Path $msvc.FullName 'lib\x64\libcmt.lib'))
    & (Join-Path $toolBin 'cl.exe') @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Native bootstrap build failed: $LASTEXITCODE" }
} finally {
    $env:PATH = $oldPath
}

& dotnet publish (Join-Path $projectRoot 'src\SnowRunnerApex5.csproj') -c Release -r win-x64 `
    --self-contained false -o $companionRoot -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw ".NET publish failed: $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SnowRunnerApex5.modinfo.json') -Destination $packageRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Assets\AdaptiveRunner.banner.png') `
    -Destination (Join-Path $packageRoot 'AdaptiveRunner.banner.png') -Force

if ($Install) {
    if ([string]::IsNullOrWhiteSpace($GameBin)) {
        throw 'Specify -GameBin with the path to SnowRunner\Sources\Bin.'
    }
    $resolvedGameBin = (Resolve-Path -LiteralPath $GameBin).Path
    if (!(Test-Path -LiteralPath (Join-Path $resolvedGameBin 'SnowRunner.exe'))) {
        throw 'The selected folder does not contain SnowRunner.exe.'
    }
    $files = @('SnowRunnerApex5.asi','SnowRunnerApex5.modinfo.json','AdaptiveRunner.banner.png',
        'Spotter\Apex5\SnowRunnerApex5.exe')
    $backupRoot = Join-Path $projectRoot ('backup\spotter-install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    foreach ($relative in $files) {
        $source = Join-Path $packageRoot $relative
        $destination = Join-Path $resolvedGameBin $relative
        if (Test-Path -LiteralPath $destination) {
            $backupFile = Join-Path $backupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $backupFile
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
            throw "Installed file verification failed: $relative"
        }
    }
    Write-Output "Installed into $resolvedGameBin. The game was not started."
}
