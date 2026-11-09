# Drives the real window to run a "Try it" test on a page and (optionally) screenshot the result.
#
#   powershell -File tools\try-drive.ps1 -Page Meetings -Test "Microphone clean-up" [-Shot docs\img\x.png] [-Language en|pt] [-Clip clip.wav]
#
# The app runs against MEETWISE_DATA=<DataDir> so your own data is untouched; the clip is placed where the Try panels look for the previous recording.
param(
    [Parameter(Mandatory)][string]$Page,          # automation name of the navigation entry (Record, Meetings, Library, AI Assistant ...)
    [string]$Test = "",                           # start of the test's name as shown in the drop-down (empty = just open the page)
    [string]$Shot = "",
    [ValidateSet("en", "pt", "es")][string]$Language = "en",
    [string]$Clip = (Join-Path $env:APPDATA "MeetWise\try\benchmark-clip.wav"),
    [string]$DataDir = (Join-Path $env:TEMP "meetwise-demo"),
    [string]$Exe = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..\src\MeetWise.App\bin\Release\net10.0-windows10.0.19041.0\MeetWise.App.exe"),
    [string]$TypeText = "",
    [string]$HoverName = "",                      # hover this control (by name) and capture its tooltip
    [string]$DialogButton = "",                   # click this button and capture the dialog it opens
    [string]$LanguageChoice = "",
    [int]$TimeoutSeconds = 420,
    [switch]$KeepOpen
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Cap2 {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[Cap2]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "roaming\try") | Out-Null
Copy-Item $Clip (Join-Path $DataDir "roaming\try\last-mic.wav") -Force
$settings = Join-Path $DataDir "roaming\settings.json"
if (-not (Test-Path $settings)) { '{}' | Set-Content $settings }
$json = Get-Content $settings -Raw | ConvertFrom-Json
$json | Add-Member -NotePropertyName uiLanguage -NotePropertyValue $Language -Force
$json | Add-Member -NotePropertyName hideWindowFromCapture -NotePropertyValue $false -Force
$json | Add-Member -NotePropertyName alwaysOnMicrophone -NotePropertyValue $false -Force
$json | ConvertTo-Json -Depth 5 | Set-Content $settings

if (Get-Process MeetWise.App -ErrorAction SilentlyContinue) { throw "MeetWise is running. Close it first (tray icon > Exit): this script never closes your own MeetWise." }
$env:MEETWISE_DATA = $DataDir
$proc = Start-Process (Resolve-Path $Exe).Path -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh(); $h = $proc.MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { throw "MeetWise did not open a window." }
Start-Sleep -Seconds 3
$root = $AE::FromHandle($h)
try { $root.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern).Resize(1320, 1100) } catch { }
Start-Sleep -Milliseconds 800

function Find([string]$name, $ctype = $null) {
    $c = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    if ($ctype) { $c = New-Object System.Windows.Automation.AndCondition($c, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctype))) }
    $root.FindFirst($TS::Descendants, $c)
}
function ById([string]$id) { $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }

# navigation entries are radio buttons: focus + Space is a real click
$nav = Find $Page ([System.Windows.Automation.ControlType]::RadioButton)
if (-not $nav) { throw "Navigation entry '$Page' not found." }
[Cap2]::SetForegroundWindow($h) | Out-Null
$nav.SetFocus(); Start-Sleep -Milliseconds 200; [System.Windows.Forms.SendKeys]::SendWait(" "); Start-Sleep -Milliseconds 1500

if ($Test) {
$panel = $null
foreach ($e in $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "TryExpander")))) { if ($e.Current.IsOffscreen -eq $false) { $panel = $e; break } }
if (-not $panel) { $panel = ById "TryExpander" }
if (-not $panel) { throw "No Try panel on page '$Page'." }
try { $panel.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
$panel.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 800

# choose the test
$combos = $root.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, "TryTestCombo")))
$combo = $null; foreach ($c in $combos) { if ($c.Current.IsOffscreen -eq $false) { $combo = $c; break } }
if (-not $combo) { $combo = $combos[0] }
$items = @()
for ($try = 0; $try -lt 4 -and $items.Count -eq 0; $try++) {
    try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { }
    Start-Sleep -Milliseconds (500 + 400 * $try)
    $items = @($combo.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
}
$picked = $false
foreach ($it in $items) { if ($it.Current.Name -like "$Test*") { $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $picked = $true; break } }
try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
if (-not $picked) { throw "Test '$Test' not found. Available: " + (($items | ForEach-Object { $_.Current.Name }) -join " | ") }
Start-Sleep -Milliseconds 600

if ($TypeText) {
    $box = ById "TryInput"
    if ($box) { $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($TypeText) }
}
if ($LanguageChoice) {
    $lc = ById "TryLanguageCombo"
    if ($lc) {
        $lc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 300
        foreach ($it in $lc.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) {
            if ($it.Current.Name -like "$LanguageChoice*") { $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break }
        }
        try { $lc.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
    }
}

# run it and wait for the status line to say it is done (or failed)
$run = ById "TryRunButton"
$sw = [Diagnostics.Stopwatch]::StartNew()
$run.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
$status = ""
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    Start-Sleep -Milliseconds 700
    $s = ById "TryStatus"; if ($s) { $status = $s.Current.Name }
    if ($status -match "^(Done in|Concluído em|Terminado en|The test failed|O teste falhou|La prueba falló)") { break }
}
"status after {0:n1}s: {1}" -f $sw.Elapsed.TotalSeconds, $status
# scroll until the status line (just above the results) is near the top of the window, so the whole result list is on the picture
$st = ById "TryStatus"
if ($st) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $win = $root.Current.BoundingRectangle
    $node = $st
    while ($node) {
        $node = $walker.GetParent($node)
        if ($node -and $node.GetCurrentPropertyValue($AE::IsScrollPatternAvailableProperty)) {
            $sp = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if (-not $sp.Current.VerticallyScrollable -or $node.Current.AutomationId -eq "TryBody") { continue }   # the panel's own body scroll stays at the top: controls first
            for ($n = 0; $n -lt 120; $n++) {
                $y = $st.Current.BoundingRectangle.Y
                if ($y -lt $win.Y + 260 -or $sp.Current.VerticalScrollPercent -ge 99.5) { break }
                $sp.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, [System.Windows.Automation.ScrollAmount]::SmallIncrement)
            }
        }
    }
}
Start-Sleep -Milliseconds 800
}

function SaveWindowShot($handle, [string]$file) {
    $r = New-Object Cap2+Rect; [Cap2]::GetWindowRect($handle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [Cap2]::PrintWindow($handle, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    "saved $file (${w}x${ht})"
}

$target = $h
if ($DialogButton) {
    $b = Find $DialogButton ([System.Windows.Automation.ControlType]::Button)
    if (-not $b) { throw "Button '$DialogButton' not found." }
    try { $b.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
    # a modal dialog would block Invoke(): focus the button and press Space, which is a real click and returns at once
    [Cap2]::SetForegroundWindow($h) | Out-Null
    $b.SetFocus(); Start-Sleep -Milliseconds 300; [System.Windows.Forms.SendKeys]::SendWait(" ")
    $dlg = $null
    for ($i = 0; $i -lt 40 -and -not $dlg; $i++) {
        Start-Sleep -Milliseconds 500
        $dlg = $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    }
    if (-not $dlg) { throw "The dialog did not open." }
    Start-Sleep -Seconds 4   # models for the dialog are prepared on open
    $target = [IntPtr]$dlg.Current.NativeWindowHandle
}

if ($HoverName) {
    $el = Find $HoverName
    if (-not $el) { throw "Control '$HoverName' not found." }
    try { $el.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
    Start-Sleep -Milliseconds 500
    $rc = $el.Current.BoundingRectangle
    [Cap2]::SetForegroundWindow($h) | Out-Null
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rc.X + 20), [int]($rc.Y + $rc.Height / 2))
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($rc.X + 24), [int]($rc.Y + $rc.Height / 2 + 1))
    Start-Sleep -Seconds 3   # the tooltip appears after about a second
    # a tooltip is a separate window that PrintWindow does not include: copy this part of the screen instead
    $r = New-Object Cap2+Rect; [Cap2]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    if ($Shot) { $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png); "saved $Shot (${w}x${ht})" }
}
elseif ($Shot) { SaveWindowShot $target $Shot }
if (-not $KeepOpen) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
