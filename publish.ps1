# Publishes a self-contained Windows x64 build to dist\MeetWise (no .NET install needed on the target PC).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot "dist\MeetWise"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish src\MeetWise.App\MeetWise.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=false -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "Published to $out" -ForegroundColor Green
