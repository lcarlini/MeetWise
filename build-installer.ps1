# Builds the portable zip and, when Inno Setup 6 is installed, Setup.exe.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

[xml]$xml = Get-Content (Join-Path $PSScriptRoot "src\MeetWise.App\MeetWise.App.csproj")
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = "0.1.0" }
Write-Host "Building MeetWise $version" -ForegroundColor Cyan

& (Join-Path $PSScriptRoot "publish.ps1")
$publishDir = Join-Path $PSScriptRoot "dist\MeetWise"

$zip = Join-Path $PSScriptRoot "dist\MeetWise-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zip -Force
Write-Host "Portable zip: $zip" -ForegroundColor Green

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DMyAppVersion=$version" "/DSourceDir=$publishDir" (Join-Path $PSScriptRoot "installer\MeetWise.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
    Write-Host "Setup.exe is in dist\installer" -ForegroundColor Green
} else {
    Write-Host "Inno Setup 6 not found: skipped Setup.exe (https://jrsoftware.org/isinfo.php). The zip is complete and portable." -ForegroundColor Yellow
}
