# Helper: start SteamTune, capture its window to PNG, then stop it.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File native\screenshot.ps1 -Exe dist\SteamTune.exe -Out build\shot.png [-Args "--preview"] [-DelaySeconds 4]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$AppArgs = "",
    [int]$DelaySeconds = 4
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not ('WinCap' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WinCap {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
}
'@
}

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = (Resolve-Path $Exe).Path
$startInfo.Arguments = $AppArgs
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = Split-Path -Parent (Resolve-Path $Exe).Path
$process = [System.Diagnostics.Process]::Start($startInfo)

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt $DelaySeconds * 4; $i++) {
    Start-Sleep -Milliseconds 250
    $process.Refresh()
    if ($process.HasExited) { throw "process exited early with code $($process.ExitCode)" }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $process.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { throw "no window handle after $DelaySeconds seconds" }
Start-Sleep -Milliseconds 800

$rect = New-Object WinCap+RECT
[void][WinCap]::GetWindowRect($hwnd, [ref]$rect)
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
Write-Host "window ${width}x${height}"

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$hdc = $graphics.GetHdc()
$ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
$graphics.ReleaseHdc($hdc)
$graphics.Dispose()

$outPath = [System.IO.Path]::GetFullPath($Out)
$bitmap.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

if (-not $process.HasExited) { $process.Kill() }
$process.WaitForExit(3000) | Out-Null

Write-Host "saved $outPath (printwindow=$ok, $((Get-Item $outPath).Length) bytes)"
