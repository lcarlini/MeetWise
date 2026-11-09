# Captures a real screenshot of every Studio page into docs\img.
#
#   powershell -ExecutionPolicy Bypass -File tools\capture-screens.ps1 [-DataDir <folder>] [-Language en|pt|es]
#
# The app runs against MEETWISE_DATA=<DataDir>, so your own recordings never appear in the pictures. Seed that folder first with:
#   $env:MEETWISE_SEED_DEMO = "<DataDir>"; dotnet test tests\MeetWise.Tests -c Release --filter DemoData
# The capture process is made DPI-aware before measuring the window; otherwise Windows reports a scaled-down size and the picture is cropped.
param(
    [string]$DataDir = (Join-Path $env:TEMP "meetwise-demo"),
    [string]$OutDir = (Join-Path $PSScriptRoot "..\docs\img"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\MeetWise.App\bin\Release\net10.0-windows10.0.19041.0\MeetWise.App.exe"),
    [ValidateSet("en", "pt", "es")][string]$Language = "en",
    [string]$Suffix = ""
)
$ErrorActionPreference = "Stop"
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

$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "roaming") | Out-Null
$settings = Join-Path $DataDir "roaming\settings.json"
if (-not (Test-Path $settings)) { '{}' | Set-Content $settings }
$json = Get-Content $settings -Raw | ConvertFrom-Json
$json | Add-Member -NotePropertyName uiLanguage -NotePropertyValue $Language -Force
# MeetWise hides its own window from screen capture by default (so it never appears in recordings); a screenshot needs it visible
$json | Add-Member -NotePropertyName hideWindowFromCapture -NotePropertyValue $false -Force
$json | ConvertTo-Json -Depth 5 | Set-Content $settings

if (Get-Process MeetWise.App -ErrorAction SilentlyContinue) { throw "MeetWise is running. Close it first (tray icon > Exit): this script never closes your own MeetWise." }
$env:MEETWISE_DATA = $DataDir
$proc = Start-Process $exePath -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 500; $proc.Refresh(); $h = $proc.MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { throw "MeetWise did not open a window." }
Start-Sleep -Seconds 3
$root = [System.Windows.Automation.AutomationElement]::FromHandle($h)

function Shot([string]$name) {
    $r = New-Object Cap+Rect; [Cap]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [Cap]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
    $file = Join-Path $OutDir "$name$Suffix.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    "saved $file (${w}x${ht})"
}

# Navigation entries are radio buttons: focusing one and pressing Space is a real click.
function GoTo([string]$automationName) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $automationName)
    $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { throw "Navigation entry '$automationName' not found." }
    [Cap]::SetForegroundWindow($h) | Out-Null
    $el.SetFocus(); Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait(" ")
    Start-Sleep -Milliseconds 1500
}

$names = @{
    en = @("Record", "Subtitles", "AI Assistant", "Library", "Meetings")
    pt = @("Gravar", "Legendas", "Assistente de IA", "Biblioteca", "Reuniões")
    es = @("Grabar", "Subtítulos", "Asistente de IA", "Biblioteca", "Reuniones")
}[$Language]
$files = @("studio-record", "studio-subtitles", "studio-assistant", "studio-library", "studio-meetings")
for ($i = 0; $i -lt $names.Count; $i++) { GoTo $names[$i]; Shot $files[$i] }

Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
