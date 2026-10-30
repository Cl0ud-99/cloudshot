# Cloudshot 自动化 UI 测试辅助脚本
# 用法：powershell -File tools\ui-test.ps1 -Stage <capture|desktop|drag|enter|clipboard>
param([string]$Stage = "desktop", [string]$OutDir = "")

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ("U" -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class U {
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "..\artifacts" }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function Save-Screen([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $g.Dispose()
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "saved $path"
}

function Send-Hotkey([byte]$mod, [byte]$key) {
    [U]::keybd_event($mod, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [U]::keybd_event($key, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [U]::keybd_event($key, 0, 2, [UIntPtr]::Zero)
    [U]::keybd_event($mod, 0, 2, [UIntPtr]::Zero)
}

function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    [U]::SetCursorPos($x1, $y1) | Out-Null
    Start-Sleep -Milliseconds 120
    [U]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)  # left down
    Start-Sleep -Milliseconds 80
    for ($i = 1; $i -le 12; $i++) {
        $x = [int]($x1 + ($x2 - $x1) * $i / 12)
        $y = [int]($y1 + ($y2 - $y1) * $i / 12)
        [U]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 16
    }
    Start-Sleep -Milliseconds 80
    [U]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)  # left up
    Start-Sleep -Milliseconds 150
}

function Describe-Foreground {
    $h = [U]::GetForegroundWindow()
    $sb = New-Object System.Text.StringBuilder 512
    [U]::GetWindowText($h, $sb, 512) | Out-Null
    $r = New-Object U+RECT
    [U]::GetWindowRect($h, [ref]$r) | Out-Null
    "foreground='$($sb.ToString())' rect=$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)"
}

switch ($Stage) {
    "desktop" {
        Save-Screen "01-desktop.png"
        Describe-Foreground
    }
    "capture" {
        Send-Hotkey 0x12 0x41   # Alt + A
        Start-Sleep -Milliseconds 700
        Save-Screen "02-overlay.png"
        Describe-Foreground
    }
    "drag" {
        Drag 500 320 1500 980
        Start-Sleep -Milliseconds 400
        Save-Screen "03-selection.png"
    }
    "toolbar" {
        # 点击工具栏里的“矩形”再拖一个框，验证标注绘制
        Drag 2000 1000 2300 1150
        Start-Sleep -Milliseconds 300
        Save-Screen "04-annotation.png"
    }
    "enter" {
        [U]::keybd_event(0x0D, 0, 0, [UIntPtr]::Zero)
        [U]::keybd_event(0x0D, 0, 2, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 600
        Save-Screen "05-after.png"
    }
    "clipboard" {
        if ([System.Windows.Forms.Clipboard]::ContainsImage()) {
            $img = [System.Windows.Forms.Clipboard]::GetImage()
            $path = Join-Path $OutDir "06-clipboard.png"
            $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            "clipboard image: $($img.Width)x$($img.Height) -> $path"
            $img.Dispose()
        } else {
            "clipboard has no image"
        }
    }
}
