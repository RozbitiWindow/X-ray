# Development helper: drives the running X-RAY window through UI Automation and captures screenshots.
# Usage: powershell -File tools\ui-check.ps1 -Action screenshot -Out shot.png
#        powershell -File tools\ui-check.ps1 -Action click -Name "Disk Analyzer"
#        powershell -File tools\ui-check.ps1 -Action settext -Name "Folder to scan" -Text "C:\Windows"
param(
    [ValidateSet('screenshot', 'click', 'settext', 'list', 'clickxy')] [string]$Action = 'screenshot',
    [int]$X, [int]$Y, [switch]$Right, [switch]$Double,
    [switch]$Global,   # search from the desktop root, e.g. to reach a modal dialog
    [string]$Name,
    [string]$Text,
    [string]$Out = 'shot.png'
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e);
}
"@

$proc = Get-Process XRay -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$hwnd = $proc.MainWindowHandle
$root = if ($Global) { [System.Windows.Automation.AutomationElement]::RootElement } else { [System.Windows.Automation.AutomationElement]::FromHandle($hwnd) }

function Find-ByName([string]$n) {
    $cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $n
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

switch ($Action) {
    'screenshot' {
        $r = New-Object Win+RECT
        [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.R - $r.L; $h = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [Win]::PrintWindow($hwnd, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
        $g.ReleaseHdc($hdc); $g.Dispose()
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        "saved $Out ($w x $h)"
    }
    'click' {
        $el = Find-ByName $Name
        if (-not $el) { throw "Element '$Name' not found" }
        $pattern = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
        elseif ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
        else {
            # Navigation list items expose their name on the inner panel; select the containing ListBoxItem.
            # Text inside a button or list item: walk up to the nearest invokable/selectable ancestor.
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $parent = $walker.GetParent($el); $done = $false
            while ($parent -and -not $done) {
                if ($parent.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); $done = $true }
                elseif ($parent.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); $done = $true }
                else { $parent = $walker.GetParent($parent) }
            }
            if (-not $done) { throw "Element '$Name' cannot be invoked" }
        }
        "clicked $Name"
    }
    'settext' {
        $el = Find-ByName $Name
        if (-not $el) { throw "Element '$Name' not found" }
        $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
        "set $Name"
    }
    'clickxy' {
        # X/Y are pixels relative to the window's top-left corner (same frame as the screenshots).
        $r = New-Object Win+RECT
        [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        [Win]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 200
        [Win]::SetCursorPos($r.L + $X, $r.T + $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        $down = if ($Right) { 0x8 } else { 0x2 }; $up = if ($Right) { 0x10 } else { 0x4 }
        $n = if ($Double) { 2 } else { 1 }
        for ($i = 0; $i -lt $n; $i++) { [Win]::mouse_event($down, 0, 0, 0, [IntPtr]::Zero); [Win]::mouse_event($up, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 60 }
        "clicked at $X,$Y"
    }
    'list' {
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($e in $all) { if ($e.Current.Name) { "{0} | {1}" -f $e.Current.ControlType.ProgrammaticName, $e.Current.Name } }
    }
}
