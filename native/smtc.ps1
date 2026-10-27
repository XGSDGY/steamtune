# SteamTune - Windows media session probe (long running, one JSON object per line).
# Requires Windows 10 1809+ (GlobalSystemMediaTransportControls / SMTC).
# Keep this file ASCII-only: Windows PowerShell may read it with the ANSI code page.
param(
    [int]$IntervalMs = 1500,
    [int]$ParentPid = 0,
    [switch]$Once
)

$ErrorActionPreference = 'Continue'
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch { }

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($WinRtTask, $ResultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
$null = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus, Windows.Media.Control, ContentType = WindowsRuntime]

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class SteamTuneFg {
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static string Title() {
        StringBuilder sb = new StringBuilder(1024);
        GetWindowTextW(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessId() {
        uint pid;
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        return pid;
    }
}
"@

$manager = $null
$failureStreak = 0

do {
    $payload = [ordered]@{
        ts         = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        sessions   = @()
        foreground = $null
        error      = $null
    }

    try {
        if ($null -eq $manager) {
            $manager = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])
        }

        $list = New-Object System.Collections.ArrayList
        foreach ($session in $manager.GetSessions()) {
            $playback = $null
            try { $playback = $session.GetPlaybackInfo() } catch { continue }

            $props = $null
            try {
                $props = Await ($session.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
            } catch { }

            $updated = 0
            try { $updated = $session.LastUpdatedTime.ToUnixTimeMilliseconds() } catch { }

            [void]$list.Add([ordered]@{
                    aumid   = [string]$session.SourceAppUserModelId
                    status  = [string]$playback.PlaybackStatus
                    title   = if ($props) { [string]$props.Title } else { '' }
                    artist  = if ($props) { [string]$props.Artist } else { '' }
                    album   = if ($props) { [string]$props.AlbumTitle } else { '' }
                    updated = $updated
                })
        }
        $payload.sessions = @($list)
        $failureStreak = 0
    }
    catch {
        $payload.error = $_.Exception.Message
        $manager = $null
        $failureStreak++
    }

    try {
        $title = [SteamTuneFg]::Title()
        $pid_ = [SteamTuneFg]::ProcessId()
        $procName = ''
        try { $procName = (Get-Process -Id $pid_ -ErrorAction Stop).ProcessName } catch { }
        if ($title -or $procName) {
            $payload.foreground = [ordered]@{ title = [string]$title; process = [string]$procName }
        }
    }
    catch { }

    try {
        [Console]::Out.WriteLine(($payload | ConvertTo-Json -Depth 6 -Compress))
        [Console]::Out.Flush()
    }
    catch { }

    if ($Once) { break }

    # 宿主进程退出时一起退出，避免留下孤儿 PowerShell 进程
    if ($ParentPid -gt 0) {
        $alive = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
        if ($null -eq $alive) { break }
    }

    if ($failureStreak -gt 20) { Start-Sleep -Seconds 5 } else { Start-Sleep -Milliseconds $IntervalMs }
} while ($true)
