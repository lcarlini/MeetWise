# UI Automation helpers for driving the real MeetWise window (dot-source this file).
#   . tools\ui-lib.ps1; Start-App -DataDir $d; Go "Record"; Click "RecordButton"; Shot "x"; Stop-App
# WPF exposes x:Name as AutomationId, so controls are found by their XAML name.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System; using System.Runtime.InteropServices;
public class Cap {
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[Cap]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$script:Results = New-Object System.Collections.Generic.List[object]

function Start-App([string]$DataDir, [string]$Exe, [string]$Language = "en") {
    New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "roaming") | Out-Null
    $settings = Join-Path $DataDir "roaming\settings.json"
    if (-not (Test-Path $settings)) { '{}' | Set-Content $settings }
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    $json | Add-Member -NotePropertyName uiLanguage -NotePropertyValue $Language -Force
    $json | Add-Member -NotePropertyName hideWindowFromCapture -NotePropertyValue $false -Force
    $json | ConvertTo-Json -Depth 5 | Set-Content $settings
    if (Get-Process MeetWise.App -ErrorAction SilentlyContinue) { throw "MeetWise is running. Close it first (tray icon > Exit): this script never closes your own MeetWise." }
    $env:MEETWISE_DATA = $DataDir
    $script:Proc = Start-Process (Resolve-Path $Exe).Path -PassThru
    $script:H = [IntPtr]::Zero
    $sw = [Diagnostics.Stopwatch]::StartNew()
    for ($i = 0; $i -lt 120 -and $script:H -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $script:Proc.Refresh(); $script:H = $script:Proc.MainWindowHandle }
    if ($script:H -eq [IntPtr]::Zero) { throw "MeetWise did not open a window." }
    $script:StartupMs = $sw.ElapsedMilliseconds
    Start-Sleep -Seconds 2
    $script:Root = $AE::FromHandle($script:H)
}
function Stop-App { Stop-Process -Id $script:Proc.Id -Force -ErrorAction SilentlyContinue }
function Find([string]$id, [string]$by = "AutomationId") {
    $prop = if ($by -eq "Name") { $AE::NameProperty } else { $AE::AutomationIdProperty }
    $script:Root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($prop, $id)))
}
function Wait-For([scriptblock]$cond, [int]$Seconds = 30) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 250 }
    return $false
}
function Click([string]$id, [string]$by = "AutomationId") {
    $el = Find $id $by
    if (-not $el) { throw "control '$id' not found" }
    # Navigation entries are RadioButtons: selecting them through UI Automation only checks them; the page changes on a real click,
    # which focus + Space reproduces.
    if ($el.Current.ControlType -eq [System.Windows.Automation.ControlType]::RadioButton) {
        [Cap]::SetForegroundWindow($script:H) | Out-Null; $el.SetFocus(); Start-Sleep -Milliseconds 150; [System.Windows.Forms.SendKeys]::SendWait(" ")
    } else {
        try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        catch {
            try { $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
            catch { [Cap]::SetForegroundWindow($script:H) | Out-Null; $el.SetFocus(); Start-Sleep -Milliseconds 150; [System.Windows.Forms.SendKeys]::SendWait(" ") }
        }
    }
    Start-Sleep -Milliseconds 400
}
function Go([string]$navName) { Click $navName "Name"; Start-Sleep -Milliseconds 1200 }
function Text([string]$id) { $el = Find $id; if ($el) { $el.Current.Name } }
function Enabled([string]$id) { $el = Find $id; $el -and $el.Current.IsEnabled }
function Shot([string]$path) {
    $r = New-Object Cap+Rect; [Cap]::GetWindowRect($script:H, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [Cap]::PrintWindow($script:H, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function Dump-Tree([int]$max = 400) {
    $all = $script:Root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $n = 0
    foreach ($e in $all) {
        $c = $e.Current
        if ($c.AutomationId -or ($c.Name -and $c.ControlType.ProgrammaticName -notmatch "Text$|Pane|Custom")) {
            "{0,-22} id={1,-22} name='{2}' en={3}" -f $c.ControlType.ProgrammaticName.Replace("ControlType.", ""), $c.AutomationId, $c.Name, $c.IsEnabled
            if (++$n -ge $max) { break }
        }
    }
}
function Check([string]$scenario, [bool]$ok, [string]$detail = "") {
    $script:Results.Add([pscustomobject]@{ Scenario = $scenario; Ok = $ok; Detail = $detail })
    "{0}  {1}  {2}" -f ($(if ($ok) { "PASS" } else { "FAIL" })), $scenario, $detail
}

# Picks an item of a ComboBox by its visible text (expands it, selects the item, collapses it).
function Choose([string]$comboId, [string]$itemText) {
    $cb = Find $comboId
    if (-not $cb) { throw "combo '$comboId' not found" }
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 350
    # items are ListItem elements; their Name is the item's text (or, for typed choices, its ToString())
    $item = $null
    $all = $cb.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    foreach ($i in $all) { if ($i.Current.Name -eq $itemText -or $i.Current.Name -like "*Text = $itemText }" -or $i.Current.Name -like "*$itemText*") { $item = $i; break } }
    if (-not $item) { $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); throw "item '$itemText' not in '$comboId'" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 250
    try { $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
}
function Combo-Items([string]$comboId) {
    $cb = Find $comboId
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 350
    $all = $cb.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $names = @($all | ForEach-Object { $_.Current.Name })
    $cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    $names
}
function Set-Setting([string]$dataDir, [string]$key, $value) {
    $p = Join-Path $dataDir "roaming\settings.json"
    $j = Get-Content $p -Raw | ConvertFrom-Json
    $j | Add-Member -NotePropertyName $key -NotePropertyValue $value -Force
    $j | ConvertTo-Json -Depth 5 | Set-Content $p
}
function Play-Wav([string]$wav) { $sp = New-Object System.Media.SoundPlayer $wav; $sp.Load(); $sp.Play(); $sp }
