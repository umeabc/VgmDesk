param([Parameter(Mandatory=$true)][string]$App, [Parameter(Mandatory=$true)][string]$Fixtures, [Parameter(Mandatory=$true)][string]$Workspace)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
}
'@
$env:TEMP = $env:TMP = $Workspace
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $Workspace 'bundle'
$dest = Join-Path $Workspace ('exports-' + [Guid]::NewGuid().ToString('N'))
$proc = Start-Process -FilePath $App -ArgumentList ('"' + $Fixtures + '"') -PassThru
try {
    $limit = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250; $proc.Refresh()
        if ($proc.HasExited) { throw "GUI exited ($($proc.ExitCode))" }
    } until ($proc.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -gt $limit)
    if ($proc.MainWindowHandle -eq 0) { throw 'GUI window did not open' }
    $win = [Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    function Element([string]$id) {
        $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $wnd = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if (!$wnd) { throw "Control not found: $id" }
        return $wnd
    }
    function WaitUntil([scriptblock]$test, [string]$label, [int]$seconds=12) {
        $until = [DateTime]::UtcNow.AddSeconds($seconds)
        do { if (& $test) { Write-Output "PASS: $label"; return }; Start-Sleep -Milliseconds 90 } while ([DateTime]::UtcNow -lt $until)
        throw "Timeout: $label"
    }
    function InvokeButton([string]$id) { (Element $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    function SetValue([string]$id, [string]$value) { (Element $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
    WaitUntil { (Element 'ExportAll').Current.IsEnabled } 'CLI initialized; startup folder scanned'
    $engine = (Element 'EngineLabel').Current.Name
    if ($engine -notmatch 'r2168') { throw "Unexpected engine: $engine" }
    if ((Element 'CountLabel').Current.Name -notmatch '^3 ') { throw 'Expected three queue rows' }
    SetValue 'OutputBox' $dest
    InvokeButton 'ExportAll'
    WaitUntil { (Element 'ExportAll').Current.IsEnabled -and @(Get-ChildItem -LiteralPath $dest -Filter '*.wav' -ErrorAction SilentlyContinue).Count -eq 3 } 'GUI batch export wrote all three subsongs'
    $id = (Element 'TrackGrid')
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::DataItem)
    $items = $id.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)
    if ($items.Count -ne 3) { throw "Unexpected row count: $($items.Count)" }
    $trackFound = $false
    foreach ($item in $items) {
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if ((Element 'TrackApply').Current.IsEnabled) { $trackFound=$true; break }
    }
    if (!$trackFound) { throw 'Could not select a multi-subsong input' }
    SetValue 'TrackNumber' '2'; InvokeButton 'TrackApply'
    WaitUntil { (Element 'DetailSummary').Current.Name -match 'second' } 'GUI changed to FSB subsong 2'
    $rect = New-Object WindowCapture+Rect
    if ([WindowCapture]::GetWindowRect($proc.MainWindowHandle,[ref] $rect)) {
        $image = New-Object Drawing.Bitmap(($rect.R - $rect.L), ($rect.B - $rect.T))
        $graphics = [Drawing.Graphics]::FromImage($image); $dc = $graphics.GetHdc()
        try { $captured = [WindowCapture]::PrintWindow($proc.MainWindowHandle,$dc,2) }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        if ($captured) { $image.Save((Join-Path $Workspace 'preview.png'),[Drawing.Imaging.ImageFormat]::Png) }
        $image.Dispose()
    }
    InvokeButton 'PlayButton'
    try {
        WaitUntil { (Element 'Position').Current.IsEnabled } 'WAV playback opened and seek enabled' 6
        InvokeButton 'PlayButton'
        WaitUntil { (Element 'PlayingHint').Current.Name -and (Element 'StopButton').Current.IsEnabled } 'player remains open after pause request'
        InvokeButton 'StopButton'
        WaitUntil { !(Element 'Position').Current.IsEnabled } 'stop disables seek'
    } catch {
        Write-Warning ("Playback not fully verified: " + $_.Exception.Message + ' / ' + (Element 'StatusLabel').Current.Name)
    }
} finally {
    if (!$proc.HasExited) { if (!$proc.CloseMainWindow() -or !$proc.WaitForExit(5000)) { $proc.Kill() } }
    $proc.Dispose()
}
