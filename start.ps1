# MeetWise launcher: builds the latest sources (incremental, fast when nothing changed) and starts the app.
# If MeetWise is already running, it does nothing instead of opening a second copy.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

$exe = Join-Path $Root "src\MeetWise.App\bin\Release\net10.0-windows10.0.19041.0\MeetWise.App.exe"

if (Get-Process -Name "MeetWise.App" -ErrorAction SilentlyContinue) {
  Write-Host "MeetWise is already running. Look for it in the system tray." -ForegroundColor Yellow
  exit 0
}

Write-Host "Building MeetWise (latest sources)..." -ForegroundColor Cyan
dotnet build (Join-Path $Root "MeetWise.slnx") -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) {
  Add-Type -AssemblyName System.Windows.Forms
  [System.Windows.Forms.MessageBox]::Show("The build failed. Run start.ps1 from a terminal to see the errors.", "MeetWise", "OK", "Error") | Out-Null
  exit 1
}

Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
Write-Host "MeetWise started." -ForegroundColor Green
