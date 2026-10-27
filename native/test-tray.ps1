# Dev helper: verify the app keeps running while hidden in the tray (no window).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File native\test-tray.ps1 -Exe dist\SteamTune.exe
param([string]$Exe)

$ErrorActionPreference = 'Stop'

if (-not ('TrayTest' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TrayTest {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
}
'@
}

$process = Start-Process -FilePath (Resolve-Path $Exe).Path -ArgumentList '--tray', '--preview' -PassThru
try {
    Start-Sleep -Seconds 4
    $process.Refresh()

    if ($process.HasExited) { throw "process exited while starting in tray (exit code $($process.ExitCode))" }
    $hwnd = $process.MainWindowHandle
    $visible = $false
    if ($hwnd -ne [IntPtr]::Zero) { $visible = [TrayTest]::IsWindowVisible($hwnd) }

    Write-Host "pid=$($process.Id) alive=True hwnd=$hwnd visible=$visible"
    if ($visible) { throw 'window is visible although --tray was passed' }
    Write-Host 'OK: running hidden in tray'
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
}
