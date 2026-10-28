#Requires -Version 5.1
<#
.SYNOPSIS
    Desktop verification harness for Clippy.

.DESCRIPTION
    Optionally places the cursor, sends Win+V or Esc, then captures the screen so
    the popup can be inspected without a human watching.

.EXAMPLE
    .\tools\verify-popup.ps1 -CursorX 600 -CursorY 400 -SendWinV -Out artifacts\popup.png
#>
param(
    [switch]$SendWinV,
    [switch]$SendEscape,
    [string]$Keys = "",
    [int]$CursorX = -1,
    [int]$CursorY = -1,
    [string]$Out = "artifacts\popup.png",
    [int]$DelayMs = 700
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ("ClippyVerifier" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class ClippyVerifier
{
    private const uint KeyUp = 0x0002;
    private const byte LWin = 0x5B;
    private const byte V = 0x56;
    private const byte Escape = 0x1B;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    public static void MakeDpiAware()
    {
        SetProcessDPIAware();
    }

    public static void SendWinV()
    {
        keybd_event(LWin, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(V, 0, 0, UIntPtr.Zero);
        keybd_event(V, 0, KeyUp, UIntPtr.Zero);
        System.Threading.Thread.Sleep(30);
        keybd_event(LWin, 0, KeyUp, UIntPtr.Zero);
    }

    public static void SendEscape()
    {
        keybd_event(Escape, 0, 0, UIntPtr.Zero);
        keybd_event(Escape, 0, KeyUp, UIntPtr.Zero);
    }
}
"@
}

[ClippyVerifier]::MakeDpiAware() | Out-Null

if ($CursorX -ge 0 -and $CursorY -ge 0) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($CursorX, $CursorY)
    Start-Sleep -Milliseconds 120
}

if ($SendWinV) { [ClippyVerifier]::SendWinV() }
if ($SendEscape) { [ClippyVerifier]::SendEscape() }

if ($Keys.Length -gt 0) {
    Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

Start-Sleep -Milliseconds $DelayMs

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)

    $target = Join-Path (Get-Location).Path $Out
    $directory = Split-Path -Parent $target
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output "Captured $($bounds.Width)x$($bounds.Height) to $Out"
