# Build the native WinForms app with the C# compiler shipped in .NET Framework 4.8.
# Usage: powershell -ExecutionPolicy Bypass -File native\build.ps1 [-Clean]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'dist'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
if ($Clean -and (Test-Path $outDir)) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$resources = @(
    "/win32icon:$root\build\icon.ico",
    "/resource:$root\build\icon.ico,SteamTune.ico",
    "/resource:$root\native\smtc.ps1,smtc.ps1",
    "/resource:$root\steam\richpresence_localization.json,localization.json"
)

$references = @(
    "/r:$framework\System.dll",
    "/r:$framework\System.Core.dll",
    "/r:$framework\System.Drawing.dll",
    "/r:$framework\System.Windows.Forms.dll",
    "/r:$framework\System.Web.Extensions.dll"
)

$sources = @(Get-ChildItem (Join-Path $root 'native\*.cs') | ForEach-Object { $_.FullName })

$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/debug-',
    '/codepage:65001',
    "/out:$outDir\SteamTune.exe"
) + $references + $resources + $sources

Write-Host 'compiling...'
& $csc $compilerArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

Copy-Item (Join-Path $root 'native\vendor\steam_api64.dll') (Join-Path $outDir 'steam_api64.dll') -Force
Copy-Item (Join-Path $root 'steam\richpresence_localization.json') (Join-Path $outDir 'richpresence_localization.json') -Force

# Copy the dev AppID next to the exe for local testing; exclude it from the depot.
$devAppId = Join-Path $root 'steam_appid.txt'
if (Test-Path $devAppId) {
    Copy-Item $devAppId (Join-Path $outDir 'steam_appid.txt') -Force
    Write-Host 'note: copied dev steam_appid.txt (exclude it from the Steam depot)'
}

$exe = Get-Item (Join-Path $outDir 'SteamTune.exe')
Write-Host ("built: {0} ({1:N0} bytes)" -f $exe.FullName, $exe.Length)
Get-ChildItem $outDir | Select-Object Name, Length | Format-Table -AutoSize
