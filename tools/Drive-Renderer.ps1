<#
.SYNOPSIS
    Sends REAL mouse input to a running vvvv renderer window: wheel notches, or a left-button drag.
.DESCRIPTION
    Rung 4 for gestures when no person is at the mouse. It moves the system cursor and clicks, so it
    must never run while the user is using the machine: it refuses when the top-level window at the
    target point is not the renderer (a full-screen game covered the screen once, 2026-11-09, and the
    events would have gone into it). Never launches or closes vvvv. Pair with
    D:\2026_Projects\vl-overworld\tools\Capture-Renderer.ps1 to photograph the result.
    Positions are fractions of the client area.
.EXAMPLE
    .\tools\Drive-Renderer.ps1 -Title 'Build your own globe scene' -Action wheel -Notches 4 -AtX 0.6 -AtY 0.4
    .\tools\Drive-Renderer.ps1 -Title 'Build your own globe scene' -Action drag -AtX 0.5 -AtY 0.5 -ToX 0.7 -ToY 0.5
#>
param(
    [Parameter(Mandatory)] [string]$Title,
    [ValidateSet('wheel', 'drag')] [string]$Action,
    [int]$Notches = 1,            # wheel: positive = toward the screen (zoom in)
    [double]$AtX = 0.5, [double]$AtY = 0.5,          # fraction of the client area
    [double]$ToX = 0.5, [double]$ToY = 0.5,          # drag end
    [int]$Steps = 20
)
# Drives MY vvvv's renderer with real mouse input. Refuses when another window covers the point.
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class DriveGlobe {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, IntPtr extra);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public static IntPtr Find(uint[] pids, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid);
      if (Array.IndexOf(pids, pid) < 0 || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
      if (sb.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
    return found; }
}
'@
[void][DriveGlobe]::SetProcessDPIAware()
$pids = [uint32[]]@(Get-Process vvvv -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
$h = [DriveGlobe]::Find($pids, $Title)
if ($h -eq [IntPtr]::Zero) { throw "no window '$Title'" }
[void][DriveGlobe]::SetForegroundWindow($h); Start-Sleep -Milliseconds 300
$r = New-Object DriveGlobe+RECT; [void][DriveGlobe]::GetWindowRect($h, [ref]$r)
$top = $r.Top + 31   # below the title bar
function P([double]$fx, [double]$fy) { [int]($r.Left + ($r.Right - $r.Left) * $fx), [int]($top + ($r.Bottom - $top) * $fy) }
$x0, $y0 = P $AtX $AtY
$pt = New-Object DriveGlobe+POINT; $pt.X = $x0; $pt.Y = $y0
if ([DriveGlobe]::GetAncestor([DriveGlobe]::WindowFromPoint($pt), 2) -ne $h) { throw "another window covers ($x0,$y0)" }
[void][DriveGlobe]::SetCursorPos($x0, $y0); Start-Sleep -Milliseconds 200
if ($Action -eq 'wheel') {
    for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) {
        [DriveGlobe]::mouse_event(0x0800, 0, 0, 120 * [Math]::Sign($Notches), [IntPtr]::Zero); Start-Sleep -Milliseconds 120
    }
} else {
    $x1, $y1 = P $ToX $ToY
    [DriveGlobe]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 100
    for ($i = 1; $i -le $Steps; $i++) {
        [void][DriveGlobe]::SetCursorPos([int]($x0 + ($x1 - $x0) * $i / $Steps), [int]($y0 + ($y1 - $y0) * $i / $Steps)); Start-Sleep -Milliseconds 40
    }
    Start-Sleep -Milliseconds 100; [DriveGlobe]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
}
Start-Sleep -Milliseconds 300
"$Action done at ($x0,$y0)"
