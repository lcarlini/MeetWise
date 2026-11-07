# Drives the real MeetWise window through the scenarios that need no audio hardware, and prints PASS/FAIL per step.
#   powershell -ExecutionPolicy Bypass -File tools\ui-test.ps1 [-ShotDir <folder>]
# The app runs against a scratch data folder (MEETWISE_DATA), so nothing of yours is read or changed.
param(
    [string]$ShotDir = (Join-Path $env:TEMP "meetwise-ui-shots"),
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\MeetWise.App\bin\Release\net10.0-windows10.0.19041.0\MeetWise.App.exe")
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ui-lib.ps1")
New-Item -ItemType Directory -Force $ShotDir | Out-Null
$data = Join-Path $env:TEMP "mw-ui-test-data"; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$data\roaming", "$data\out" | Out-Null; '{}' | Set-Content "$data\roaming\settings.json"
Set-Setting $data outputFolder "$data\out"

function Alive { $script:Proc.Refresh(); -not $script:Proc.HasExited -and $script:Proc.Responding }

foreach ($lang in @("en", "pt", "es")) {
    Start-App -DataDir $data -Exe $Exe -Language $lang
    Check "[$lang] starts and shows a window" ($script:StartupMs -lt 8000) "$($script:StartupMs) ms"
    # a control that exists only on that page proves the page really changed (not just the highlighted button)
    $pages = [ordered]@{ NavRecord = "RecordButton"; NavSubtitles = "RefineButton"; NavAssistant = "SendButton"; NavLibrary = "ItemGrid"; NavMeetings = "CreateMicButton" }
    foreach ($n in $pages.Keys) {
        Click $n
        $shown = Wait-For { $c = Find $pages[$n]; $c -and -not $c.Current.IsOffscreen } 6
        Shot (Join-Path $ShotDir "$lang-$n.png")
        Check "[$lang] page $n opens (its own control is visible) and the window stays responsive" ($shown -and (Alive))
    }
    Click "NavRecord"
    $ids = "RecordButton", "MicCombo", "ModeCombo", "LanguageCombo", "UseMicCheck", "SystemAudioCheck", "SrtCheck"
    $missing = @($ids | Where-Object { -not (Find $_) })
    Check "[$lang] record page controls present" ($missing.Count -eq 0) ($missing -join ",")
    $mics = Combo-Items "MicCombo"
    Check "[$lang] microphone list is not empty" ($mics.Count -gt 0) ($mics -join " | ")
    Check "[$lang] combo items have readable names" (-not ($mics -match "Choice \{")) ""
    Stop-App
    Start-Sleep -Milliseconds 800
}
$bad = @($script:Results | Where-Object { -not $_.Ok })
"`n{0} checks, {1} failed" -f $script:Results.Count, $bad.Count
if ($bad.Count) { exit 1 }
